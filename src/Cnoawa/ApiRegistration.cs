using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Cnoawa;

public class ApiRegistration
{
    readonly string _apiUrl;
    readonly string _publicAddress;
    readonly ushort _port;
    readonly string _name;
    readonly string _message;
    readonly string _registrationKey;
    readonly GameNode _node;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    int _nodeId;
    string _token = "";

    public ApiRegistration(string apiUrl, string publicAddress, ushort port, string name, string message, string registrationKey, GameNode node)
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _publicAddress = publicAddress;
        _port = port;
        _name = name;
        _message = message;
        _registrationKey = registrationKey;
        _node = node;
    }

    public async Task<bool> RegisterAsync()
    {
        try
        {
            var request = new
            {
                address = _publicAddress,
                port = _port,
                name = _name,
                message = _message,
                maxRooms = 50,
                registrationKey = _registrationKey
            };

            var response = await _http.PostAsJsonAsync($"{_apiUrl}/api/nodes/register", request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[注册] 失败: {response.StatusCode} - {error}");
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<RegisterResponse>();
            if (result == null) return false;

            _nodeId = result.Id;
            _token = result.Token;

            _node.ConfigureJwt(result.JwtPublicKey, result.JwtIssuer, result.JwtAudience);

            Console.WriteLine($"[注册] 成功! 节点 ID: {_nodeId}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[注册] 异常: {ex.Message}");
            return false;
        }
    }

    CancellationTokenSource? _heartbeatDelayCts;
    long _lastHeartbeatTime;
    volatile bool _heartbeatPending;

    public async Task HeartbeatLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_heartbeatPending)
                {
                    _heartbeatDelayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        await Task.Delay(30_000, _heartbeatDelayCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                    }
                    _heartbeatDelayCts = null;
                }

                _heartbeatPending = false;

                if (ct.IsCancellationRequested) break;

                var now = Environment.TickCount64;
                var elapsed = now - _lastHeartbeatTime;
                if (elapsed < 1000)
                    await Task.Delay((int)(1000 - elapsed), ct);

                _lastHeartbeatTime = Environment.TickCount64;
                await SendHeartbeatAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void TriggerHeartbeat()
    {
        _heartbeatPending = true;
        Console.WriteLine("[心跳] 触发即时更新");
        _heartbeatDelayCts?.Cancel();
    }

    public async Task SendHeartbeatAsync()
    {
        try
        {
            var rooms = _node.GetRoomInfos();
            var request = new
            {
                activeRooms = _node.ActiveRoomCount,
                activeConnections = _node.ActiveConnectionCount,
                rooms
            };

            var msg = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/api/nodes/{_nodeId}/heartbeat")
            {
                Content = JsonContent.Create(request)
            };
            msg.Headers.Add("X-Node-Token", _token);

            var response = await _http.SendAsync(msg);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>();
                if (body?.RoomsToClose != null)
                {
                    foreach (var roomId in body.RoomsToClose)
                    {
                        Console.WriteLine($"[心跳] 收到管理员指令：关闭房间 #{roomId}");
                        _node.RemoveRoom(roomId);
                    }
                }
                Console.WriteLine($"[心跳] 已发送 (房间:{rooms.Count}, 连接:{_node.ActiveConnectionCount})");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine("[心跳] 收到 404，尝试重新注册...");
                var reRegistered = await RegisterAsync();
                if (reRegistered)
                    Console.WriteLine("[心跳] 重新注册成功");
                else
                    Console.WriteLine("[心跳] 重新注册失败");
            }
            else if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[心跳] 失败: {response.StatusCode}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[心跳] 异常: {ex.Message}");
        }
    }

    public async Task UnregisterAsync()
    {
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Delete, $"{_apiUrl}/api/nodes/{_nodeId}");
            msg.Headers.Add("X-Node-Token", _token);
            await _http.SendAsync(msg);
            Console.WriteLine("[注册] 已注销");
        }
        catch { }
    }

    class RegisterResponse
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("jwtPublicKey")] public string JwtPublicKey { get; set; } = "";
        [JsonPropertyName("jwtIssuer")] public string JwtIssuer { get; set; } = "";
        [JsonPropertyName("jwtAudience")] public string JwtAudience { get; set; } = "";
    }

    class HeartbeatResponse
    {
        [JsonPropertyName("roomsToClose")] public List<int>? RoomsToClose { get; set; }
    }
}
