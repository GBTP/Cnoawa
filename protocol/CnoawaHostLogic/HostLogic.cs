// 房主权威房间状态机。从 Cnoawa/src/Cnoawa/NodeRoom.cs 抽出，与传输无关。
//
// 本文件是共享代码：服务端（Cnoawa .NET 10）和 Unity 客户端各一份，必须逐字节一致。
// 同步纪律与 CnoawaProtocol 相同：改这里后原样复制到
// Anoawa/Assets/Plugins/CnoawaHostLogic/HostLogic.cs。
//
// 语法限制：兼容 C# 9（Unity）。禁集合表达式 = []、required、文件作用域命名空间。
//
// 与原 NodeRoom 的差异：
//   - NodeConnection → IHostPlayer（transport 无关的玩家句柄）
//   - ConcurrentDictionary → Dictionary（_stateLock 已保护，不需要并发字典）
//   - Broadcast/BroadcastExcept → _transport.SendToAll/SendToAllExcept
//   - conn.SendMessage/SendRaw/SendError/CloseAfterSend → IHostPlayer 对应方法
//   - Random.Shared → ThreadSafeRandom.Next
//   - Environment.TickCount64 → Environment.TickCount（int，用 long 比较，49 天溢出对房间无影响）
//   - HttpClient 随机选曲 → IRandomLevelProvider
//   - _players.IsEmpty → _players.Count == 0
//   - 服务端 RoomInfo → HostRoomInfo（避免与 TapTap RoomInfo 同名）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CnoawaProtocol;
using MemoryPack;

namespace Cnoawa.Host
{
    public class HostLogic : IDisposable
    {
        /// <summary>允许的房间人数。社区线 + LAN 线统一。TapTap 线在 TapTapBackend 自己校验。</summary>
        public static readonly int[] AllowedRoomSizes = { 2, 4, 8, 16 };

        /// <summary>校验人数是否在允许的集合里。不在返回 false。</summary>
        public static bool IsAllowedRoomSize(int maxPlayers)
        {
            foreach (var s in AllowedRoomSizes)
                if (s == maxPlayers) return true;
            return false;
        }
        // key = IHostPlayer.TransportId（社区线是 ConnId，其他线可复用 PlayerId）
        readonly Dictionary<int, IHostPlayer> _players = new Dictionary<int, IHostPlayer>();
        readonly Dictionary<byte, bool> _readyState = new Dictionary<byte, bool>();
        readonly Dictionary<byte, VoteEntry> _votes = new Dictionary<byte, VoteEntry>();
        readonly Dictionary<byte, float> _downloadProgress = new Dictionary<byte, float>();
        readonly HashSet<byte> _finishedPlayers = new HashSet<byte>();
        readonly Dictionary<byte, long> _lastComboTime = new Dictionary<byte, long>();
        readonly Dictionary<byte, int> _lastReportedScore = new Dictionary<byte, int>();
        readonly Dictionary<byte, int> _lastReportedMaxCombo = new Dictionary<byte, int>();
        readonly Dictionary<byte, PlayerFinishedMessage> _finishedData = new Dictionary<byte, PlayerFinishedMessage>();
        // 干扰动画的占位时长。动画资源接入后由客户端按自身长度播放，这里只是消息里的上限值。
        const float DistractDuration = 5f;
        bool _disposed;

        readonly object _stateLock = new object();

        readonly ISerializer _serializer;
        readonly IHostTransport _transport;
        readonly IRandomLevelProvider _randomLevelProvider;

        public int RoomId { get; }
        public string RoomName { get; }
        public int MaxPlayers { get; }
        // 私密房的隔离是"不进公开列表、凭房间号直进"，没有密码功能，所以这里没有 Password。
        public bool IsPrivate { get; }
        public IHostPlayer? Creator { get; private set; }
        public RoomState State { get; private set; } = RoomState.Lobby;
        public RoomType RoomType { get; private set; } = RoomType.Competitive;
        public int? SelectedLevelId { get; private set; }
        public string? SelectedLevelName { get; private set; }
        public bool IsEmpty => _players.Count == 0;
        public int PlayerCount => _players.Count;
        /// <summary>IHostTransport 实现用它遍历成员做广播。只读视图，外部不得修改。</summary>
        public IReadOnlyCollection<IHostPlayer> Players => _players.Values;
        public Action? OnStateChanged { get; set; }
        public Action<int>? OnRoomEmpty { get; set; }

        public HostLogic(int roomId, string roomName, int maxPlayers, bool isPrivate,
            IHostPlayer creator, ISerializer serializer, IHostTransport transport, IRandomLevelProvider randomLevelProvider)
        {
            if (!IsAllowedRoomSize(maxPlayers))
                throw new System.ArgumentException($"房间人数 {maxPlayers} 不在允许的集合 {{2,4,8,16}} 内", nameof(maxPlayers));
            RoomId = roomId;
            RoomName = roomName;
            MaxPlayers = maxPlayers;
            IsPrivate = isPrivate;
            Creator = creator;
            _serializer = serializer;
            _transport = transport;
            _randomLevelProvider = randomLevelProvider;
        }

        public void AddPlayer(IHostPlayer player)
        {
            lock (_stateLock)
            {
                AddPlayerInternal(player);
                BroadcastSnapshot();
                OnStateChanged?.Invoke();
            }
        }

        public void RemovePlayer(IHostPlayer player)
        {
            lock (_stateLock)
            {
                RemovePlayerInternal(player);
            }
        }

        void RemovePlayerInternal(IHostPlayer player)
        {
            if (!_players.Remove(player.TransportId)) return;

            _readyState.Remove(player.PlayerId);

            if (IsEmpty)
            {
                OnRoomEmpty?.Invoke(RoomId);
                return;
            }

            if (Creator != null && player.TransportId == Creator.TransportId)
            {
                var next = _players.Values.FirstOrDefault();
                Creator = next;
                if (next != null)
                    OnHostTransferred(RoomId, next);
            }

            if (State == RoomState.Playing)
            {
                _finishedPlayers.Add(player.PlayerId);
                CheckAllFinished();
            }
            else if (State == RoomState.ChartSelect)
            {
                _votes.Remove(player.PlayerId);
                if (_votes.Count >= _players.Count && _players.Count > 0)
                    FinalizeVote();
            }
            else if (State == RoomState.Downloading)
            {
                _downloadProgress.Remove(player.PlayerId);
                _readyForPlay.Remove(player.PlayerId);
                CheckAllDownloaded();
                if (_readyPhaseCts != null && !_readyPhaseCts.IsCancellationRequested
                    && _players.Values.All(p => _readyForPlay.Contains(p.PlayerId)))
                    _readyPhaseCts.Cancel();
            }

            BroadcastSnapshot();
            OnStateChanged?.Invoke();
        }

        /// <summary>房主转移给新玩家时触发。服务端打日志；TapTap/LAN 线由子类或外部覆盖。</summary>
        protected virtual void OnHostTransferred(int roomId, IHostPlayer newHost) { }

        public void HandleJoin(IHostPlayer player)
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    SendToPlayer(player, MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "房间正在解散" });
                    return;
                }

                if (State != RoomState.Lobby)
                {
                    SendToPlayer(player, MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "房间已在游戏中" });
                    return;
                }

                if (_players.Count >= MaxPlayers)
                {
                    SendToPlayer(player, MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "房间已满" });
                    return;
                }

                AddPlayerInternal(player);

                SendToPlayer(player, MessageType.JoinRoomResult, new JoinRoomResultMessage
                {
                    Success = true,
                    Players = _players.Values.Select(p => new CnoawaProtocol.PlayerInfo { PlayerId = p.PlayerId, UserId = p.UserId }).ToArray(),
                    State = (byte)State,
                    RoomType = (byte)RoomType
                });

                BroadcastSnapshot();
                OnStateChanged?.Invoke();
            }
        }

        void AddPlayerInternal(IHostPlayer player)
        {
            byte id = 1;
            var usedIds = _players.Values.Select(p => p.PlayerId).ToHashSet();
            while (usedIds.Contains(id) && id < 255) id++;
            player.PlayerId = id;
            _players[player.TransportId] = player;
        }

        public void HandleMessage(IHostPlayer sender, MessageType type, byte[] payload)
        {
            lock (_stateLock)
            {
                switch (type)
                {
                    case MessageType.LeaveRoom:
                        break;

                    case MessageType.Ready:
                        HandleReady(sender, payload);
                        break;

                    case MessageType.RoomConfig:
                        HandleRoomConfig(sender, payload);
                        break;

                    case MessageType.EnterChartSelect:
                        if (IsCreator(sender))
                            SetState(RoomState.ChartSelect);
                        break;

                    case MessageType.ChartVote:
                        HandleVote(sender, payload);
                        break;

                    case MessageType.DownloadProgress:
                        HandleDownloadProgress(sender, payload);
                        break;

                    case MessageType.DownloadComplete:
                        HandleDownloadComplete(sender);
                        break;

                    case MessageType.ComboUpdate:
                        HandleComboUpdate(sender, payload);
                        break;

                    case MessageType.PlayerFinished:
                        HandlePlayerFinished(sender, payload);
                        break;

                    case MessageType.SkillCast:
                        HandleSkillCast(sender, payload);
                        break;

                    case MessageType.BackToLobby:
                        break;

                    case MessageType.Kick:
                        HandleKick(sender, payload);
                        break;
                }
            }
        }

        public void HandleLeaveRoom(IHostPlayer sender)
        {
            RemovePlayer(sender);
            sender.SendEmpty((byte)MessageType.LeaveRoom);
        }

        bool IsCreator(IHostPlayer player) => Creator != null && player.TransportId == Creator.TransportId;

        void HandleReady(IHostPlayer sender, byte[] payload)
        {
            var msg = _serializer.Deserialize<ReadyMessage>(payload);
            if (msg == null) return;

            if (State == RoomState.Downloading)
            {
                HandleReadyForPlay(sender);
                return;
            }

            if (State == RoomState.Lobby)
            {
                _readyState[sender.PlayerId] = msg.IsReady;
                BroadcastSnapshot();
            }
        }

        void HandleRoomConfig(IHostPlayer sender, byte[] payload)
        {
            if (!IsCreator(sender)) return;
            var msg = _serializer.Deserialize<RoomConfigMessage>(payload);
            if (msg == null) return;
            RoomType = (RoomType)msg.RoomType;
            Broadcast(MessageType.RoomConfig, msg);
            BroadcastSnapshot();
        }

        void HandleVote(IHostPlayer sender, byte[] payload)
        {
            if (State != RoomState.ChartSelect) return;
            var msg = _serializer.Deserialize<ChartVoteMessage>(payload);
            if (msg == null) return;

            _votes[sender.PlayerId] = new VoteEntry
            {
                PlayerId = sender.PlayerId,
                LevelId = msg.LevelId,
                LevelName = msg.LevelName
            };

            BroadcastVoteStatus();

            if (_votes.Count >= _players.Count)
                FinalizeVote();
        }

        void FinalizeVote()
        {
            if (State != RoomState.ChartSelect) return;

            var pool = _votes.Values.Where(v => v.LevelId >= 0).ToList();

            if (pool.Count == 0)
            {
                _ = FinalizeVoteRandomAsync();
                return;
            }

            var pick = pool[ThreadSafeRandom.Next(pool.Count)];
            SelectedLevelId = pick.LevelId;
            SelectedLevelName = pick.LevelName;

            var candidates = pool.Select(v => v.LevelName).Distinct().ToArray();

            Broadcast(MessageType.ChartResult, new ChartResultMessage
            {
                LevelId = pick.LevelId,
                LevelName = pick.LevelName,
                Candidates = candidates
            });

            SetState(RoomState.Downloading);
        }

        async Task FinalizeVoteRandomAsync()
        {
            try
            {
                var items = await _randomLevelProvider.GetRandomLevelsAsync(5);
                if (items == null || items.Length == 0)
                {
                    lock (_stateLock)
                        BroadcastError("没有可用的在线谱面");
                    return;
                }

                var pick = items[ThreadSafeRandom.Next(items.Length)];
                var candidates = items.Select(i => i.LevelName).ToArray();

                lock (_stateLock)
                {
                    if (State != RoomState.ChartSelect) return;

                    SelectedLevelId = pick.Id;
                    SelectedLevelName = pick.LevelName;

                    Broadcast(MessageType.ChartResult, new ChartResultMessage
                    {
                        LevelId = pick.Id,
                        LevelName = pick.LevelName,
                        Candidates = candidates
                    });

                    SetState(RoomState.Downloading);
                }
            }
            catch (Exception ex)
            {
                OnLog($"[HostLogic] 随机选曲失败: {ex.Message}");
                lock (_stateLock)
                    BroadcastError("随机选曲失败，请重试");
            }
        }

        /// <summary>日志钩子。服务端指向 Console.WriteLine，Unity 侧指向 Debug.Log。</summary>
        protected virtual void OnLog(string message) { }

        void HandleDownloadProgress(IHostPlayer sender, byte[] payload)
        {
            var msg = _serializer.Deserialize<DownloadProgressMessage>(payload);
            if (msg == null) return;
            _downloadProgress[sender.PlayerId] = Math.Clamp(msg.Progress, 0f, 1f);
            BroadcastDownloadStatus();
        }

        void HandleDownloadComplete(IHostPlayer sender)
        {
            _downloadProgress[sender.PlayerId] = 1f;
            BroadcastDownloadStatus();
            CheckAllDownloaded();
        }

        void CheckAllDownloaded()
        {
            if (_readyPhaseCts != null && !_readyPhaseCts.IsCancellationRequested)
                return;
            if (_players.Values.All(p => _downloadProgress.TryGetValue(p.PlayerId, out var prog) && prog >= 1f))
                _ = StartReadyPhase();
        }

        CancellationTokenSource? _readyPhaseCts;
        CancellationTokenSource? _playingTimeoutCts;
        readonly HashSet<byte> _readyForPlay = new HashSet<byte>();

        async Task StartReadyPhase()
        {
            lock (_stateLock)
            {
                _readyPhaseCts?.Dispose();
                _readyPhaseCts = new CancellationTokenSource();
                BroadcastSnapshot();

                if (_players.Values.All(p => _readyForPlay.Contains(p.PlayerId)))
                {
                    _readyPhaseCts.Cancel();
                    _readyPhaseCts = null;
                }
            }

            if (_readyPhaseCts == null || _readyPhaseCts.IsCancellationRequested)
            {
                lock (_stateLock)
                {
                    if (_disposed || _players.Count == 0) return;
                    StartGame();
                }
                _playingTimeoutCts?.Dispose();
                _playingTimeoutCts = new CancellationTokenSource();
                _ = ComboHeartbeatCheck(_playingTimeoutCts.Token);
                return;
            }

            try
            {
                for (int i = 30; i >= 1; i--)
                {
                    Broadcast(MessageType.Countdown, new CountdownMessage { Seconds = i });
                    await Task.Delay(1000, _readyPhaseCts.Token);
                }
            }
            catch (TaskCanceledException)
            {
            }

            lock (_stateLock)
            {
                _readyPhaseCts = null;

                if (_disposed || _players.Count == 0) return;

                StartGame();
            }

            if (_disposed || _players.Count == 0) return;

            _playingTimeoutCts?.Dispose();
            _playingTimeoutCts = new CancellationTokenSource();
            _ = ComboHeartbeatCheck(_playingTimeoutCts.Token);
        }

        void StartGame()
        {
            SetState(RoomState.Playing);
            _finishedPlayers.Clear();
            _lastComboTime.Clear();
            _lastReportedScore.Clear();
            _lastReportedMaxCombo.Clear();
            var now = (long)Environment.TickCount;
            foreach (var p in _players.Values)
                _lastComboTime[p.PlayerId] = now;
            Broadcast(MessageType.GameStart, new StateChangeMessage { State = (byte)RoomState.Playing });
        }

        async Task ComboHeartbeatCheck(CancellationToken ct)
        {
            const long timeoutMs = 15000;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5000, ct);

                    lock (_stateLock)
                    {
                        if (State != RoomState.Playing) break;

                        var now = (long)Environment.TickCount;
                        var timedOut = false;
                        foreach (var p in _players.Values)
                        {
                            if (_finishedPlayers.Contains(p.PlayerId)) continue;
                            if (!_lastComboTime.TryGetValue(p.PlayerId, out var last)) continue;
                            if (now - last > timeoutMs)
                            {
                                _finishedPlayers.Add(p.PlayerId);
                                p.SendError(0, "超时未响应，已被标记为完成");
                                timedOut = true;
                            }
                        }
                        if (timedOut) CheckAllFinished();
                    }
                }
            }
            catch (TaskCanceledException) { }
        }

        void HandleReadyForPlay(IHostPlayer sender)
        {
            if (State != RoomState.Downloading) return;
            _readyForPlay.Add(sender.PlayerId);
            BroadcastSnapshot();

            if (_players.Values.All(p => _readyForPlay.Contains(p.PlayerId)))
                _readyPhaseCts?.Cancel();
        }

        void HandleComboUpdate(IHostPlayer sender, byte[] payload)
        {
            if (State != RoomState.Playing) return;
            if (_finishedPlayers.Contains(sender.PlayerId)) return;

            var msg = _serializer.Deserialize<ComboUpdateMessage>(payload);
            if (msg == null) return;

            if (msg.Score < 0 || msg.Combo < 0 || msg.MaxCombo < 0)
                return;

            if (_lastReportedScore.TryGetValue(sender.PlayerId, out var lastScore) && msg.Score < lastScore)
                return;
            _lastReportedScore[sender.PlayerId] = msg.Score;
            _lastReportedMaxCombo[sender.PlayerId] = msg.MaxCombo;

            _lastComboTime[sender.PlayerId] = (long)Environment.TickCount;

            BroadcastExcept(sender.TransportId, MessageType.PlayerSync, new PlayerSyncMessage
            {
                PlayerId = sender.PlayerId,
                Combo = msg.Combo,
                Score = msg.Score,
                MaxCombo = msg.MaxCombo
            });
        }

        void HandlePlayerFinished(IHostPlayer sender, byte[] payload)
        {
            if (State != RoomState.Playing) return;
            if (!_finishedPlayers.Add(sender.PlayerId)) return;

            var msg = _serializer.Deserialize<PlayerFinishedMessage>(payload);
            if (msg == null) return;

            msg.PlayerId = sender.PlayerId;
            _finishedData[sender.PlayerId] = msg;
            BroadcastExcept(sender.TransportId, MessageType.PlayerFinished, msg);
            CheckAllFinished();
        }

        void CheckAllFinished()
        {
            if (_finishedPlayers.Count < _players.Count) return;

            _playingTimeoutCts?.Cancel();
            _playingTimeoutCts = null;
            _lastComboTime.Clear();

            var rankings = _players.Values
                .Select(p =>
                {
                    _finishedData.TryGetValue(p.PlayerId, out var f);
                    return new PlayerResult
                    {
                        PlayerId = p.PlayerId,
                        UserId = p.UserId,
                        Score = f?.FinalScore ?? (_lastReportedScore.TryGetValue(p.PlayerId, out var s) ? s : 0),
                        MaxCombo = f?.MaxCombo ?? (_lastReportedMaxCombo.TryGetValue(p.PlayerId, out var mc) ? mc : 0),
                        MaxPure = f?.MaxPure ?? 0,
                        Pure = f?.Pure ?? 0,
                        Far = f?.Far ?? 0,
                        Lost = f?.Lost ?? 0
                    };
                })
                .OrderByDescending(r => r.Score)
                .ToArray();

            for (int i = 0; i < rankings.Length; i++)
                rankings[i].Rank = i + 1;

            Broadcast(MessageType.Results, new ResultsMessage { Rankings = rankings });
            SetState(RoomState.Lobby);
        }

        void HandleSkillCast(IHostPlayer sender, byte[] payload)
        {
            if (State != RoomState.Playing || RoomType != RoomType.Casual) return;
            if (_finishedPlayers.Contains(sender.PlayerId)) return;

            var msg = _serializer.Deserialize<SkillCastMessage>(payload);
            if (msg == null) return;

            // 现在只剩「干扰别人」这一种网络技能，目标固定是所有其他玩家。
            // SelfBoost（增强自己）纯客户端本地生效，正常客户端不会发到这里；
            // 只认白名单里的 ID，改过的客户端塞别的值进来也广播不出去。
            if (msg.SkillId != (byte)SkillId.DistractOthers) return;

            // 不做冷却限制：技能消耗血条，血条本身就是限流（干扰一次 25 点，回满要上百个 note）。
            // 之前那道 5 秒冷却只会回一条 Error，而客户端把任何 Error 都当断线处理，连点就被踢出对局。

            var effect = new SkillEffectMessage
            {
                CasterPlayerId = sender.PlayerId,
                SkillId = msg.SkillId,
                // 占位时长：动画资源接入后由客户端按自身长度播放，这个值只是上限。
                Duration = DistractDuration
            };

            BroadcastExcept(sender.TransportId, MessageType.SkillEffect, effect);
        }

        void HandleKick(IHostPlayer sender, byte[] payload)
        {
            if (!IsCreator(sender)) return;
            var msg = _serializer.Deserialize<KickMessage>(payload);
            if (msg == null) return;

            var target = _players.Values.FirstOrDefault(p => p.PlayerId == msg.PlayerId);
            if (target == null) return;

            SendToPlayer(target, MessageType.Kick, msg);
            target.Kick();
            RemovePlayerInternal(target);
        }

        CancellationTokenSource? _voteTimeoutCts;

        void SetState(RoomState state)
        {
            State = state;
            Broadcast(MessageType.StateChange, new StateChangeMessage { State = (byte)state });

            _voteTimeoutCts?.Cancel();
            _voteTimeoutCts = null;

            if (state == RoomState.Lobby)
            {
                _votes.Clear();
                _downloadProgress.Clear();
                _finishedPlayers.Clear();
                _finishedData.Clear();
                _lastReportedScore.Clear();
                _lastReportedMaxCombo.Clear();
                _readyState.Clear();
                _readyForPlay.Clear();
                SelectedLevelId = null;
                SelectedLevelName = null;
            }
            else if (state == RoomState.ChartSelect)
            {
                _votes.Clear();
                _downloadProgress.Clear();
                _readyState.Clear();
                _voteTimeoutCts?.Dispose();
                _voteTimeoutCts = new CancellationTokenSource();
                _ = VoteCountdown(_voteTimeoutCts.Token);
            }

            BroadcastSnapshot();
            OnStateChanged?.Invoke();
        }

        async Task VoteCountdown(CancellationToken ct)
        {
            try
            {
                for (int i = 30; i >= 1; i--)
                {
                    Broadcast(MessageType.Countdown, new CountdownMessage { Seconds = i });
                    await Task.Delay(1000, ct);
                }
            }
            catch (TaskCanceledException) { return; }

            lock (_stateLock)
            {
                if (State != RoomState.ChartSelect) return;

                foreach (var p in _players.Values)
                {
                    if (!_votes.ContainsKey(p.PlayerId))
                    {
                        _votes[p.PlayerId] = new VoteEntry
                        {
                            PlayerId = p.PlayerId,
                            LevelId = -1,
                            LevelName = "任意"
                        };
                    }
                }

                BroadcastVoteStatus();
                FinalizeVote();
            }
        }

        void BroadcastSnapshot()
        {
            var hostPlayerId = Creator?.PlayerId ?? (byte)0;
            var players = _players.Values.Select(p => new SnapshotPlayer
            {
                PlayerId = p.PlayerId,
                UserId = p.UserId,
                IsReady = State == RoomState.Downloading
                    ? _readyForPlay.Contains(p.PlayerId)
                    : _readyState.TryGetValue(p.PlayerId, out var r) && r,
                DownloadProgress = _downloadProgress.TryGetValue(p.PlayerId, out var prog) ? prog : 0f
            }).ToArray();

            foreach (var p in _players.Values)
            {
                var msg = new RoomSnapshotMessage
                {
                    State = (byte)State,
                    RoomType = (byte)RoomType,
                    HostPlayerId = hostPlayerId,
                    LocalPlayerId = p.PlayerId,
                    Players = players,
                    SelectedLevelId = SelectedLevelId,
                    SelectedLevelName = SelectedLevelName
                };
                SendToPlayer(p, MessageType.RoomSnapshot, msg);
            }
        }

        void BroadcastVoteStatus()
        {
            Broadcast(MessageType.VoteStatus, new VoteStatusMessage { Votes = _votes.Values.ToArray() });
        }

        void BroadcastDownloadStatus()
        {
            var status = _players.Values.Select(p => new PlayerDownloadInfo
            {
                PlayerId = p.PlayerId,
                Progress = _downloadProgress.TryGetValue(p.PlayerId, out var prog) ? prog : 0f,
                Complete = _downloadProgress.TryGetValue(p.PlayerId, out var c) && c >= 1f
            }).ToArray();
            Broadcast(MessageType.DownloadStatus, new DownloadStatusMessage { Players = status });
        }

        void Broadcast<T>(MessageType type, T message) where T : class
        {
            var payload = _serializer.Serialize(message);
            _transport.SendToAll((byte)type, payload);
        }

        void BroadcastExcept<T>(int excludeTransportId, MessageType type, T message) where T : class
        {
            var payload = _serializer.Serialize(message);
            _transport.SendToAllExcept(excludeTransportId, (byte)type, payload);
        }

        // 发序列化消息给单个玩家。原 NodeConnection.SendMessage<T> 的 transport 无关版。
        void SendToPlayer<T>(IHostPlayer player, MessageType type, T message) where T : class
        {
            var payload = _serializer.Serialize(message);
            player.SendRaw((byte)type, payload);
        }

        void BroadcastError(string message)
        {
            Broadcast(MessageType.Error, new ErrorMessage { Message = message });
        }

        public HostRoomInfo GetInfo() => new HostRoomInfo
        {
            RoomId = RoomId,
            RoomName = RoomName,
            HostUserId = Creator?.UserId ?? 0,
            CurrentPlayers = _players.Count,
            MaxPlayers = MaxPlayers,
            Status = State.ToString(),
            IsPrivate = IsPrivate,
            SelectedLevelId = SelectedLevelId,
            SelectedLevelName = SelectedLevelName
        };

        public void Dispose()
        {
            _disposed = true;
            _readyPhaseCts?.Cancel();
            _playingTimeoutCts?.Cancel();
            _voteTimeoutCts?.Cancel();
            // 不在这里关连接——HostLogic 不知道"怎么关"（那是 IHostPlayer.Kick 的职责），
            // 而且房间解散是强制关（Close），不是踢人（CloseAfterSend）。
            // 各 transport/薄壳的 Dispose 自己遍历成员关连接。
            _players.Clear();
        }
    }
}
