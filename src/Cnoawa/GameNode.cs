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
    public int ActiveRoomCount => _rooms.Count;
    public int ActiveConnectionCount => _connections.Count;

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
        _listener.Start();
        Console.WriteLine($"[Cnoawa] 游戏节点启动，端口: {_port}");

        _ = Task.Run(() => CleanupLoop(_cts.Token), _cts.Token);

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
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
            foreach (var conn in _connections.Values)
                conn.Close();
            Console.WriteLine("[Cnoawa] 节点已停止");
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

    public NodeRoom CreateRoom(int roomId, string roomName, int maxPlayers, bool isPrivate, string? password, NodeConnection creator)
    {
        var room = new NodeRoom(roomId, roomName, maxPlayers, isPrivate, password, creator, ApiUrl);
        _rooms[roomId] = room;
        Console.WriteLine($"[Cnoawa] 房间创建: #{roomId} \"{roomName}\" (创建者: userId={creator.UserId})");
        return room;
    }

    public void RemoveRoom(int roomId)
    {
        if (_rooms.TryRemove(roomId, out var room))
        {
            room.Dispose();
            Console.WriteLine($"[Cnoawa] 房间移除: #{roomId}");
        }
    }

    public List<RoomInfo> GetRoomInfos()
    {
        return _rooms.Values.Select(r => r.GetInfo()).ToList();
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

    public int? ValidateToken(string token)
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
                IssuerSigningKey = _jwtPublicKey,
                ValidateIssuerSigningKey = true
            };

            var principal = handler.ValidateToken(token, parameters, out _);
            var userIdClaim = principal.FindFirst("userId")?.Value
                ?? principal.FindFirst("sub")?.Value;

            if (userIdClaim != null && int.TryParse(userIdClaim, out var userId))
                return userId;

            return null;
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
    public int? SelectedLevelId { get; set; }
    public string? SelectedLevelName { get; set; }
}
