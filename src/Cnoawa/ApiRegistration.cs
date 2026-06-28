using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cnoawa;

public class ApiRegistration
{
    readonly string _apiUrl;
    readonly string _publicAddress;
    readonly ushort _port;
    readonly string _name;
    readonly string _message;
    readonly GameNode _node;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    int _nodeId;
    string _token = "";

    public ApiRegistration(string apiUrl, string publicAddress, ushort port, string name, string message, GameNode node)
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _publicAddress = publicAddress;
        _port = port;
        _name = name;
        _message = message;
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
                maxRooms = 50
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

            Console.WriteLine($"[注册] 成功! 节点 ID: {_nodeId}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[注册] 异常: {ex.Message}");
            return false;
        }
    }

    public async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct);
            try
            {
                var request = new
                {
                    activeRooms = _node.ActiveRoomCount,
                    activeConnections = _node.ActiveConnectionCount,
                    rooms = _node.GetRoomInfos()
                };

                var msg = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/api/nodes/{_nodeId}/heartbeat")
                {
                    Content = JsonContent.Create(request)
                };
                msg.Headers.Add("X-Node-Token", _token);

                var response = await _http.SendAsync(msg, ct);
                if (!response.IsSuccessStatusCode)
                    Console.WriteLine($"[心跳] 失败: {response.StatusCode}");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[心跳] 异常: {ex.Message}");
            }
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
    }
}
