using System.Collections.Concurrent;
using System.Net.Http.Json;
using CnoawaProtocol;
using MemoryPack;

namespace Cnoawa;

public class NodeRoom : IDisposable
{
    readonly ConcurrentDictionary<int, NodeConnection> _players = new();
    readonly Dictionary<byte, bool> _readyState = new();
    readonly Dictionary<byte, VoteEntry> _votes = new();
    readonly Dictionary<byte, float> _downloadProgress = new();
    readonly HashSet<byte> _finishedPlayers = new();
    readonly Dictionary<byte, long> _lastComboTime = new();
    readonly Dictionary<byte, int> _lastReportedScore = new();
    readonly Dictionary<byte, int> _skillCooldown = new();
    readonly string _apiUrl;
    static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };
    int _nextPlayerId = 1;
    bool _disposed;

    readonly object _stateLock = new();

    public int RoomId { get; }
    public string RoomName { get; }
    public int MaxPlayers { get; }
    public bool IsPrivate { get; }
    public string? Password { get; }
    public NodeConnection? Creator { get; private set; }
    public RoomState State { get; private set; } = RoomState.Lobby;
    public RoomType RoomType { get; private set; } = RoomType.Competitive;
    public int? SelectedLevelId { get; private set; }
    public string? SelectedLevelName { get; private set; }
    public bool IsEmpty => _players.IsEmpty;
    public int PlayerCount => _players.Count;
    public Action? OnStateChanged { get; set; }

    public NodeRoom(int roomId, string roomName, int maxPlayers, bool isPrivate, string? password, NodeConnection creator, string apiUrl)
    {
        RoomId = roomId;
        RoomName = roomName;
        MaxPlayers = maxPlayers;
        IsPrivate = isPrivate;
        Password = password;
        Creator = creator;
        _apiUrl = apiUrl;
    }

    public void AddPlayer(NodeConnection conn)
    {
        lock (_stateLock)
        {
            AddPlayerInternal(conn);
            BroadcastSnapshot();
        }
    }

    public void RemovePlayer(NodeConnection conn)
    {
        if (!_players.TryRemove(conn.ConnId, out _)) return;
        conn.CurrentRoom = null;

        lock (_stateLock)
        {
            _readyState.Remove(conn.PlayerId);

            if (IsEmpty) return;

            if (Creator != null && conn.ConnId == Creator.ConnId)
            {
                var next = _players.Values.FirstOrDefault();
                Creator = next;
                if (next != null)
                    Console.WriteLine($"[房间#{RoomId}] 房主已转移给 userId={next.UserId}");
            }

            if (State == RoomState.Playing)
            {
                _finishedPlayers.Add(conn.PlayerId);
                CheckAllFinished();
            }
            else if (State == RoomState.ChartSelect)
            {
                _votes.Remove(conn.PlayerId);
                if (_votes.Count >= _players.Count && _players.Count > 0)
                    FinalizeVote();
            }
            else if (State == RoomState.Downloading)
            {
                _downloadProgress.Remove(conn.PlayerId);
                _readyForPlay.Remove(conn.PlayerId);
                CheckAllDownloaded();
                if (_readyPhaseCts != null && !_readyPhaseCts.IsCancellationRequested
                    && _players.Values.All(p => _readyForPlay.Contains(p.PlayerId)))
                    _readyPhaseCts.Cancel();
            }

            BroadcastSnapshot();
        }
    }

    public void HandleJoin(NodeConnection conn, string? password)
    {
        lock (_stateLock)
        {
            if (State != RoomState.Lobby)
            {
                conn.SendMessage(MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "房间已在游戏中" });
                return;
            }

            if (_players.Count >= MaxPlayers)
            {
                conn.SendMessage(MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "房间已满" });
                return;
            }

            if (!string.IsNullOrEmpty(Password) && password != Password)
            {
                conn.SendMessage(MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "密码错误" });
                return;
            }

            AddPlayerInternal(conn);

            conn.SendMessage(MessageType.JoinRoomResult, new JoinRoomResultMessage
            {
                Success = true,
                Players = _players.Values.Select(p => new PlayerInfo { PlayerId = p.PlayerId, UserId = p.UserId }).ToArray(),
                State = (byte)State,
                RoomType = (byte)RoomType
            });

            BroadcastSnapshot();

            Console.WriteLine($"[房间#{RoomId}] userId={conn.UserId} 加入 (playerId={conn.PlayerId}, 当前{_players.Count}人)");
        }
    }

    void AddPlayerInternal(NodeConnection conn)
    {
        var id = _nextPlayerId++;
        if (_nextPlayerId > 254) _nextPlayerId = 1;
        conn.PlayerId = (byte)id;
        conn.CurrentRoom = this;
        _players[conn.ConnId] = conn;
    }

    public void HandleMessage(NodeConnection sender, MessageType type, byte[] payload)
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
                    if (IsCreator(sender))
                        SetState(RoomState.Lobby);
                    break;

                case MessageType.Kick:
                    HandleKick(sender, payload);
                    break;
            }
        }
    }

    public void HandleLeaveRoom(NodeConnection sender)
    {
        RemovePlayer(sender);
        sender.SendEmpty(MessageType.LeaveRoom);
    }

    bool IsCreator(NodeConnection conn) => Creator != null && conn.ConnId == Creator.ConnId;

    void HandleReady(NodeConnection sender, byte[] payload)
    {
        var msg = MemoryPackSerializer.Deserialize<ReadyMessage>(payload);
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

    void HandleRoomConfig(NodeConnection sender, byte[] payload)
    {
        if (!IsCreator(sender)) return;
        var msg = MemoryPackSerializer.Deserialize<RoomConfigMessage>(payload);
        if (msg == null) return;
        RoomType = (RoomType)msg.RoomType;
        Broadcast(MessageType.RoomConfig, msg);
        BroadcastSnapshot();
    }

    void HandleVote(NodeConnection sender, byte[] payload)
    {
        if (State != RoomState.ChartSelect) return;
        var msg = MemoryPackSerializer.Deserialize<ChartVoteMessage>(payload);
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

        var pick = pool[Random.Shared.Next(pool.Count)];
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
            var response = await s_http.GetFromJsonAsync<RandomLevelResponse>($"{_apiUrl}/api/levels/random?count=5");
            if (response?.Items == null || response.Items.Length == 0)
            {
                lock (_stateLock)
                    BroadcastError("没有可用的在线谱面");
                return;
            }

            var pick = response.Items[Random.Shared.Next(response.Items.Length)];
            var candidates = response.Items.Select(i => i.LevelName).ToArray();

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
            Console.WriteLine($"[NodeRoom] 随机选曲失败: {ex.Message}");
            lock (_stateLock)
                BroadcastError("随机选曲失败，请重试");
        }
    }

    record RandomLevelItem(int Id, string LevelName);
    record RandomLevelResponse(RandomLevelItem[]? Items);

    void HandleDownloadProgress(NodeConnection sender, byte[] payload)
    {
        var msg = MemoryPackSerializer.Deserialize<DownloadProgressMessage>(payload);
        if (msg == null) return;
        _downloadProgress[sender.PlayerId] = Math.Clamp(msg.Progress, 0f, 1f);
        BroadcastDownloadStatus();
    }

    void HandleDownloadComplete(NodeConnection sender)
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
    readonly HashSet<byte> _readyForPlay = new();

    async Task StartReadyPhase()
    {
        _readyForPlay.Clear();
        _readyPhaseCts = new CancellationTokenSource();

        BroadcastSnapshot();

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

            if (_disposed || _players.IsEmpty) return;

            SetState(RoomState.Playing);
            _finishedPlayers.Clear();
            _lastComboTime.Clear();
            _lastReportedScore.Clear();
            _skillCooldown.Clear();
            var now = Environment.TickCount64;
            foreach (var conn in _players.Values)
                _lastComboTime[conn.PlayerId] = now;
            Broadcast(MessageType.GameStart, new StateChangeMessage { State = (byte)RoomState.Playing });
        }

        if (_disposed || _players.IsEmpty) return;

        _playingTimeoutCts = new CancellationTokenSource();
        _ = ComboHeartbeatCheck(_playingTimeoutCts.Token);
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

                    var now = Environment.TickCount64;
                    var timedOut = false;
                    foreach (var conn in _players.Values)
                    {
                        if (_finishedPlayers.Contains(conn.PlayerId)) continue;
                        if (!_lastComboTime.TryGetValue(conn.PlayerId, out var last)) continue;
                        if (now - last > timeoutMs)
                        {
                            _finishedPlayers.Add(conn.PlayerId);
                            conn.SendError(0, "超时未响应，已被标记为完成");
                            timedOut = true;
                        }
                    }
                    if (timedOut) CheckAllFinished();
                }
            }
        }
        catch (TaskCanceledException) { }
    }

    void HandleReadyForPlay(NodeConnection sender)
    {
        if (State != RoomState.Downloading) return;
        _readyForPlay.Add(sender.PlayerId);
        BroadcastSnapshot();

        if (_players.Values.All(p => _readyForPlay.Contains(p.PlayerId)))
            _readyPhaseCts?.Cancel();
    }

    void HandleComboUpdate(NodeConnection sender, byte[] payload)
    {
        if (State != RoomState.Playing) return;
        if (_finishedPlayers.Contains(sender.PlayerId)) return;

        var msg = MemoryPackSerializer.Deserialize<ComboUpdateMessage>(payload);
        if (msg == null) return;

        if (msg.Score < 0 || msg.Score > 10100000 || msg.Combo < 0 || msg.MaxCombo < 0)
            return;

        if (_lastReportedScore.TryGetValue(sender.PlayerId, out var lastScore) && msg.Score < lastScore)
            return;
        _lastReportedScore[sender.PlayerId] = msg.Score;

        _lastComboTime[sender.PlayerId] = Environment.TickCount64;

        BroadcastExcept(sender.ConnId, MessageType.PlayerSync, new PlayerSyncMessage
        {
            PlayerId = sender.PlayerId,
            Combo = msg.Combo,
            Score = msg.Score,
            MaxCombo = msg.MaxCombo
        });
    }

    void HandlePlayerFinished(NodeConnection sender, byte[] payload)
    {
        if (State != RoomState.Playing) return;
        if (!_finishedPlayers.Add(sender.PlayerId)) return;

        var msg = MemoryPackSerializer.Deserialize<PlayerFinishedMessage>(payload);
        if (msg == null) return;

        msg.PlayerId = sender.PlayerId;
        BroadcastExcept(sender.ConnId, MessageType.PlayerFinished, msg);
        CheckAllFinished();
    }

    void CheckAllFinished()
    {
        if (_finishedPlayers.Count < _players.Count) return;

        _playingTimeoutCts?.Cancel();
        _playingTimeoutCts = null;
        _lastComboTime.Clear();

        var rankings = _players.Values
            .Select(p => new PlayerResult
            {
                PlayerId = p.PlayerId,
                UserId = p.UserId,
                Score = _lastReportedScore.TryGetValue(p.PlayerId, out var s) ? s : 0
            })
            .OrderByDescending(r => r.Score)
            .ToArray();

        for (int i = 0; i < rankings.Length; i++)
            rankings[i].Rank = i + 1;

        Broadcast(MessageType.Results, new ResultsMessage { Rankings = rankings });
        SetState(RoomState.Results);
    }

    void HandleSkillCast(NodeConnection sender, byte[] payload)
    {
        if (State != RoomState.Playing || RoomType != RoomType.Casual) return;
        if (_finishedPlayers.Contains(sender.PlayerId)) return;

        var msg = MemoryPackSerializer.Deserialize<SkillCastMessage>(payload);
        if (msg == null) return;

        var now = Environment.TickCount;
        if (_skillCooldown.TryGetValue(sender.PlayerId, out var lastCast) && now - lastCast < 5000)
        {
            sender.SendError(0, "技能冷却中");
            return;
        }
        _skillCooldown[sender.PlayerId] = now;

        var effect = new SkillEffectMessage
        {
            CasterPlayerId = sender.PlayerId,
            TargetPlayerId = msg.TargetPlayerId,
            SkillId = msg.SkillId,
            Duration = 5f
        };

        if (msg.TargetPlayerId == 0xFF)
            BroadcastExcept(sender.ConnId, MessageType.SkillEffect, effect);
        else
        {
            var target = _players.Values.FirstOrDefault(p => p.PlayerId == msg.TargetPlayerId);
            target?.SendMessage(MessageType.SkillEffect, effect);
        }
    }

    void HandleKick(NodeConnection sender, byte[] payload)
    {
        if (!IsCreator(sender)) return;
        var msg = MemoryPackSerializer.Deserialize<KickMessage>(payload);
        if (msg == null) return;

        var target = _players.Values.FirstOrDefault(p => p.PlayerId == msg.PlayerId);
        if (target == null) return;

        _players.TryRemove(target.ConnId, out _);
        target.CurrentRoom = null;
        _readyState.Remove(target.PlayerId);

        target.SendMessage(MessageType.Kick, msg);
        target.Close();

        BroadcastSnapshot();
    }

    void SetState(RoomState state)
    {
        State = state;
        Broadcast(MessageType.StateChange, new StateChangeMessage { State = (byte)state });

        if (state == RoomState.Lobby)
        {
            _votes.Clear();
            _downloadProgress.Clear();
            _finishedPlayers.Clear();
            _lastReportedScore.Clear();
            _skillCooldown.Clear();
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
        }

        BroadcastSnapshot();
        OnStateChanged?.Invoke();
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

        foreach (var conn in _players.Values)
        {
            var msg = new RoomSnapshotMessage
            {
                State = (byte)State,
                RoomType = (byte)RoomType,
                HostPlayerId = hostPlayerId,
                LocalPlayerId = conn.PlayerId,
                Players = players,
                SelectedLevelId = SelectedLevelId,
                SelectedLevelName = SelectedLevelName
            };
            conn.SendMessage(MessageType.RoomSnapshot, msg);
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
        var payload = MemoryPackSerializer.Serialize(message);
        foreach (var conn in _players.Values)
            conn.SendRaw(type, payload);
    }

    void BroadcastExcept<T>(int excludeConnId, MessageType type, T message) where T : class
    {
        var payload = MemoryPackSerializer.Serialize(message);
        foreach (var conn in _players.Values)
            if (conn.ConnId != excludeConnId)
                conn.SendRaw(type, payload);
    }

    void BroadcastError(string message)
    {
        Broadcast(MessageType.Error, new ErrorMessage { Message = message });
    }

    public RoomInfo GetInfo() => new()
    {
        RoomId = RoomId,
        RoomName = RoomName,
        HostUserId = Creator?.UserId ?? 0,
        CurrentPlayers = _players.Count,
        MaxPlayers = MaxPlayers,
        Status = State.ToString(),
        SelectedLevelId = SelectedLevelId,
        SelectedLevelName = SelectedLevelName
    };

    public void Dispose()
    {
        _disposed = true;
        _readyPhaseCts?.Cancel();
        _playingTimeoutCts?.Cancel();
        foreach (var conn in _players.Values)
        {
            conn.CurrentRoom = null;
            conn.Close();
        }
        _players.Clear();
    }
}
