using System.Collections.Concurrent;
using System.Net.Http.Json;
using CnoawaProtocol;
using MemoryPack;

namespace Cnoawa;

public class NodeRoom : IDisposable
{
    readonly ConcurrentDictionary<int, NodeConnection> _players = new();
    readonly Dictionary<byte, VoteEntry> _votes = new();
    readonly Dictionary<byte, float> _downloadProgress = new();
    readonly HashSet<byte> _finishedPlayers = new();
    readonly Dictionary<byte, long> _lastComboTime = new();
    readonly string _apiUrl;
    static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };
    byte _nextPlayerId = 1;

    public int RoomId { get; }
    public string RoomName { get; }
    public int MaxPlayers { get; }
    public bool IsPrivate { get; }
    public string? Password { get; }
    public NodeConnection Creator { get; }
    public RoomState State { get; private set; } = RoomState.Lobby;
    public RoomType RoomType { get; private set; } = RoomType.Competitive;
    public int? SelectedLevelId { get; private set; }
    public string? SelectedLevelName { get; private set; }
    public bool IsEmpty => _players.IsEmpty;
    public int PlayerCount => _players.Count;

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
        conn.PlayerId = _nextPlayerId++;
        conn.CurrentRoom = this;
        _players[conn.ConnId] = conn;
    }

    public void RemovePlayer(NodeConnection conn)
    {
        if (!_players.TryRemove(conn.ConnId, out _)) return;
        conn.CurrentRoom = null;

        Broadcast(MessageType.PlayerLeft, new PlayerLeftMessage
        {
            PlayerId = conn.PlayerId,
            Reason = "断开连接"
        });

        if (IsEmpty) return;

        // 游玩中有人退出，检查是否全部完成
        if (State == RoomState.Playing)
            CheckAllFinished();
    }

    public void HandleJoin(NodeConnection conn, string? password)
    {
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

        AddPlayer(conn);

        var players = _players.Values.Select(p => new PlayerInfo
        {
            PlayerId = p.PlayerId,
            UserId = p.UserId
        }).ToArray();

        conn.SendMessage(MessageType.JoinRoomResult, new JoinRoomResultMessage
        {
            Success = true,
            Players = players,
            State = (byte)State,
            RoomType = (byte)RoomType
        });

        BroadcastExcept(conn.ConnId, MessageType.PlayerJoined, new PlayerJoinedMessage
        {
            PlayerId = conn.PlayerId,
            UserId = conn.UserId
        });

        Console.WriteLine($"[房间#{RoomId}] userId={conn.UserId} 加入 (playerId={conn.PlayerId}, 当前{_players.Count}人)");
    }

    public void HandleMessage(NodeConnection sender, MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.LeaveRoom:
                RemovePlayer(sender);
                sender.SendEmpty(MessageType.LeaveRoom);
                break;

            case MessageType.Ready:
                HandleReady(sender, payload);
                break;

            case MessageType.RoomConfig:
                HandleRoomConfig(sender, payload);
                break;

            case MessageType.EnterChartSelect:
                if (sender.ConnId == Creator.ConnId)
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
                if (sender.ConnId == Creator.ConnId)
                    SetState(RoomState.Lobby);
                break;

            case MessageType.Kick:
                HandleKick(sender, payload);
                break;
        }
    }

    void HandleReady(NodeConnection sender, byte[] payload)
    {
        var msg = MemoryPackSerializer.Deserialize<ReadyMessage>(payload);
        if (msg == null) return;

        if (State == RoomState.Downloading)
        {
            HandleReadyForPlay(sender);
            return;
        }

        BroadcastReadyStatus();
    }

    void HandleRoomConfig(NodeConnection sender, byte[] payload)
    {
        if (sender.ConnId != Creator.ConnId) return;
        var msg = MemoryPackSerializer.Deserialize<RoomConfigMessage>(payload);
        if (msg == null) return;
        RoomType = (RoomType)msg.RoomType;
        Broadcast(MessageType.RoomConfig, msg);
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
                BroadcastError("没有可用的在线谱面");
                return;
            }

            var pick = response.Items[Random.Shared.Next(response.Items.Length)];
            SelectedLevelId = pick.Id;
            SelectedLevelName = pick.LevelName;

            var candidates = response.Items.Select(i => i.LevelName).ToArray();

            Broadcast(MessageType.ChartResult, new ChartResultMessage
            {
                LevelId = pick.Id,
                LevelName = pick.LevelName,
                Candidates = candidates
            });

            SetState(RoomState.Downloading);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NodeRoom] 随机选曲失败: {ex.Message}");
            BroadcastError("随机选曲失败，请重试");
        }
    }

    record RandomLevelItem(int Id, string LevelName);
    record RandomLevelResponse(RandomLevelItem[]? Items);

    void HandleDownloadProgress(NodeConnection sender, byte[] payload)
    {
        var msg = MemoryPackSerializer.Deserialize<DownloadProgressMessage>(payload);
        if (msg == null) return;
        _downloadProgress[sender.PlayerId] = msg.Progress;
        BroadcastDownloadStatus();
    }

    void HandleDownloadComplete(NodeConnection sender)
    {
        _downloadProgress[sender.PlayerId] = 1f;
        BroadcastDownloadStatus();

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

        BroadcastReadyStatus();

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

        _readyPhaseCts = null;

        SetState(RoomState.Playing);
        _finishedPlayers.Clear();
        _lastComboTime.Clear();
        var now = Environment.TickCount64;
        foreach (var conn in _players.Values)
            _lastComboTime[conn.PlayerId] = now;
        Broadcast(MessageType.GameStart, new StateChangeMessage { State = (byte)RoomState.Playing });

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
        catch (TaskCanceledException) { }
    }

    void HandleReadyForPlay(NodeConnection sender)
    {
        if (State != RoomState.Downloading) return;
        _readyForPlay.Add(sender.PlayerId);
        BroadcastReadyStatus();

        if (_players.Values.All(p => _readyForPlay.Contains(p.PlayerId)))
            _readyPhaseCts?.Cancel();
    }

    void HandleComboUpdate(NodeConnection sender, byte[] payload)
    {
        if (State != RoomState.Playing) return;
        var msg = MemoryPackSerializer.Deserialize<ComboUpdateMessage>(payload);
        if (msg == null) return;

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
        _finishedPlayers.Add(sender.PlayerId);

        var msg = MemoryPackSerializer.Deserialize<PlayerFinishedMessage>(payload);
        if (msg == null) return;

        // 广播给其他人
        BroadcastExcept(sender.ConnId, MessageType.PlayerFinished, msg);

        CheckAllFinished();
    }

    void CheckAllFinished()
    {
        if (_finishedPlayers.Count < _players.Count) return;

        _playingTimeoutCts?.Cancel();
        _playingTimeoutCts = null;
        _lastComboTime.Clear();

        SetState(RoomState.Results);
    }

    void HandleSkillCast(NodeConnection sender, byte[] payload)
    {
        if (State != RoomState.Playing || RoomType != RoomType.Casual) return;
        var msg = MemoryPackSerializer.Deserialize<SkillCastMessage>(payload);
        if (msg == null) return;

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
        if (sender.ConnId != Creator.ConnId) return;
        var msg = MemoryPackSerializer.Deserialize<KickMessage>(payload);
        if (msg == null) return;

        var target = _players.Values.FirstOrDefault(p => p.PlayerId == msg.PlayerId);
        if (target == null) return;

        RemovePlayer(target);
        target.SendMessage(MessageType.Kick, msg);
        target.Close();
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
            SelectedLevelId = null;
            SelectedLevelName = null;
        }
        else if (state == RoomState.ChartSelect)
        {
            _votes.Clear();
            _downloadProgress.Clear();
        }
    }

    void BroadcastReadyStatus()
    {
        // 简化实现：这里只通知状态变化
        // 完整实现需要跟踪每人 ready 状态
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
        var frame = FrameCodec.Encode(type, payload);
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
        HostUserId = Creator.UserId,
        CurrentPlayers = _players.Count,
        MaxPlayers = MaxPlayers,
        Status = State.ToString(),
        SelectedLevelId = SelectedLevelId,
        SelectedLevelName = SelectedLevelName
    };

    public void Dispose()
    {
        foreach (var conn in _players.Values)
        {
            conn.CurrentRoom = null;
            conn.Close();
        }
        _players.Clear();
    }
}
