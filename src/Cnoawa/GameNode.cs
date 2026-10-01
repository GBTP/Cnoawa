using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CnoawaProtocol;
using Microsoft.IdentityModel.Tokens;

namespace Cnoawa;

public class GameNode
{
    const int MaxPlayersPerRoom = 16;

    readonly ushort _port;
    readonly ConcurrentDictionary<int, NodeConnection> _connections = new();
    readonly ConcurrentDictionary<int, NodeRoom> _rooms = new();
    TcpListener _listener = null!;
    CancellationTokenSource _cts = new();
    int _nextConnId;

    RsaSecurityKey? _jwtPublicKey;
    string _jwtIssuer = "";
    string _jwtAudience = "";

    public string ApiUrl { get; set; } = "";
    public string NodeToken { get; set; } = "";
    public int MaxRooms { get; set; } = 50;
    public int MaxConnections => MaxRooms * MaxPlayersPerRoom;
    public int ActiveRoomCount => _rooms.Count;
    public int ActiveConnectionCount => _connections.Count;
    public Func<Task>? OnRoomStateChanged { get; set; }
    public TaskCompletionSource ListeningReady { get; } = new();

    public GameNode(ushort port)
    {
        _port = port;
    }

    public void ConfigureJwt(string publicKeyPem, string issuer, string audience)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        _jwtPublicKey = new RsaSecurityKey(rsa);
        _jwtIssuer = issuer;
        _jwtAudience = audience;
        Console.WriteLine("[Cnoawa] JWT 公钥已配置，本地验签就绪");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Server.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        _listener.Start();
        ListeningReady.TrySetResult();
        Console.WriteLine($"[Cnoawa] 游戏节点启动，端口: {_port}");

        _ = Task.Run(() => CleanupLoop(_cts.Token), _cts.Token);

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);

                    if (_connections.Count >= MaxConnections)
                    {
                        tcp.Close();
                        continue;
                    }

                    tcp.NoDelay = true;
                    tcp.SendBufferSize = 256 * 1024;
                    tcp.ReceiveBufferSize = 256 * 1024;

                    var connId = Interlocked.Increment(ref _nextConnId);
                    var conn = new NodeConnection(connId, tcp, this);
                    _connections[connId] = conn;

                    var endpoint = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    Console.WriteLine($"[Cnoawa] 新连接: #{connId} ({endpoint})");

                    _ = conn.RunAsync(_cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Cnoawa] Accept 异常: {ex.Message}");
                    await Task.Delay(100);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
            _listener.Server.Close();
            foreach (var conn in _connections.Values)
                conn.Close();
            Console.WriteLine("[Cnoawa] 节点已停止");
        }
    }

    public void DisconnectExistingUser(int userId, int exceptConnId)
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.UserId == userId && conn.IsAuthenticated && conn.ConnId != exceptConnId)
            {
                Console.WriteLine($"[Cnoawa] 顶号: userId={userId}, 踢掉旧连接 #{conn.ConnId}");
                conn.Close();
            }
        }
    }

    public void RemoveConnection(int connId)
    {
        if (_connections.TryRemove(connId, out var conn))
        {
            if (conn.CurrentRoom != null)
                conn.CurrentRoom.RemovePlayer(conn);
            Console.WriteLine($"[Cnoawa] 连接断开: #{connId}");
        }
    }

    public NodeRoom? GetRoom(int roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    public NodeRoom? CreateRoom(int roomId, string roomName, int maxPlayers, bool isPrivate, NodeConnection creator)
    {
        var room = new NodeRoom(roomId, roomName, maxPlayers, isPrivate, creator, ApiUrl, NodeToken);
        room.OnStateChanged = NotifyRoomStateChanged;
        room.OnRoomEmpty = RemoveRoom;
        if (!_rooms.TryAdd(roomId, room))
        {
            room.Dispose();
            return null;
        }
        Console.WriteLine($"[Cnoawa] 房间创建: #{roomId} \"{roomName}\" (创建者: userId={creator.UserId})");
        NotifyRoomStateChanged();
        return room;
    }

    public void RemoveRoom(int roomId)
    {
        if (_rooms.TryRemove(roomId, out var room))
        {
            room.Dispose();
            Console.WriteLine($"[Cnoawa] 房间移除: #{roomId}");
            NotifyRoomStateChanged();
        }
    }

    public List<RoomInfo> GetRoomInfos()
    {
        return _rooms.Values.Select(r =>
        {
            var info = r.GetInfo();
            return new RoomInfo
            {
                RoomId = info.RoomId,
                RoomName = info.RoomName,
                HostUserId = info.HostUserId,
                CurrentPlayers = info.CurrentPlayers,
                MaxPlayers = info.MaxPlayers,
                Status = info.Status,
                IsPrivate = info.IsPrivate,
                SelectedLevelId = info.SelectedLevelId,
                SelectedLevelName = info.SelectedLevelName
            };
        }).ToList();
    }

    void NotifyRoomStateChanged()
    {
        if (OnRoomStateChanged == null)
        {
            Console.WriteLine("[Cnoawa] 警告: OnRoomStateChanged 未注册");
            return;
        }
        _ = OnRoomStateChanged.Invoke();
    }

    async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct);
            var empty = _rooms.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList();
            foreach (var id in empty)
                RemoveRoom(id);
            if (_rooms.Count > 0)
                Console.WriteLine($"[Cnoawa] 活跃: {_rooms.Count} 房间, {_connections.Count} 连接");
        }
    }

    public (int userId, int? roomId)? ValidateToken(string token)
    {
        if (_jwtPublicKey == null) return null;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _jwtIssuer,
                ValidateAudience = true,
                ValidAudience = _jwtAudience,
                ValidateLifetime = true,
                // 默认 ClockSkew 是 5 分钟，会把主 API 精心设计的 60 秒连接 token 变成 6 分钟，
                // 恶意节点有充足时间把玩家交上来的 token 中继到别的节点冒名。
                // 主 API 和节点都用 UTC、都在公网上，10 秒足够容忍时钟漂移。
                ClockSkew = TimeSpan.FromSeconds(10),
                IssuerSigningKey = _jwtPublicKey,
                ValidateIssuerSigningKey = true
            };

            var principal = handler.ValidateToken(token, parameters, out _);
            var userIdClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? principal.FindFirst("sub")?.Value
                ?? principal.FindFirst("userId")?.Value;

            if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
                return null;

            int? roomId = null;
            var roomIdClaim = principal.FindFirst("roomId")?.Value;
            if (roomIdClaim != null && int.TryParse(roomIdClaim, out var rid))
                roomId = rid;

            return (userId, roomId);
        }
        catch
        {
            return null;
        }
    }
}

public class RoomInfo
{
    public int RoomId { get; set; }
    public string RoomName { get; set; } = "";
    public int HostUserId { get; set; }
    public int CurrentPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public string Status { get; set; } = "Lobby";
    public bool IsPrivate { get; set; }
    public int? SelectedLevelId { get; set; }
    public string? SelectedLevelName { get; set; }
}
