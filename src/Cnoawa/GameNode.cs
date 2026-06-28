using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AnoawaProtocol;

namespace Cnoawa;

public class GameNode
{
    readonly ushort _port;
    readonly ConcurrentDictionary<int, NodeConnection> _connections = new();
    readonly ConcurrentDictionary<int, NodeRoom> _rooms = new();
    TcpListener _listener = null!;
    CancellationTokenSource _cts = new();
    int _nextConnId;

    public string Name { get; set; } = "";
    public string Message { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public int ActiveRoomCount => _rooms.Count;
    public int ActiveConnectionCount => _connections.Count;

    readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public GameNode(ushort port)
    {
        _port = port;
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
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                tcp.NoDelay = true;
                tcp.SendBufferSize = 256 * 1024;
                tcp.ReceiveBufferSize = 256 * 1024;

                var connId = Interlocked.Increment(ref _nextConnId);
                var conn = new NodeConnection(connId, tcp, this);
                _connections[connId] = conn;
                _ = conn.RunAsync(_cts.Token);

                Console.WriteLine($"[Cnoawa] 新连接: #{connId} ({tcp.Client.RemoteEndPoint})");
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
            // 从房间中移除
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
        var room = new NodeRoom(roomId, roomName, maxPlayers, isPrivate, password, creator);
        _rooms[roomId] = room;
        Console.WriteLine($"[Cnoawa] 房间创建: #{roomId} \"{roomName}\" (创建者: {creator.Nickname})");
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

    public bool HandleProbe(TcpClient tcp)
    {
        // 探测在 NodeConnection 的读循环中处理
        return false;
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

    public async Task<UserValidation?> ValidateTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(ApiUrl)) return null;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/api/auth/profile");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new UserValidation
            {
                UserId = root.GetProperty("id").GetInt32(),
                Nickname = root.GetProperty("nickname").GetString() ?? "",
                AvatarUrl = root.TryGetProperty("avatarUrl", out var av) ? av.GetString() ?? "" : ""
            };
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
    public string HostNickname { get; set; } = "";
    public int CurrentPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public string Status { get; set; } = "Lobby";
    public int? SelectedLevelId { get; set; }
    public string? SelectedLevelName { get; set; }
}

public class UserValidation
{
    public int UserId { get; set; }
    public string Nickname { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
}
