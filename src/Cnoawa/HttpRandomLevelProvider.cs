using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Cnoawa.Host;

namespace Cnoawa;

/// <summary>
/// 服务端随机选曲实现。原 NodeRoom.FinalizeVoteRandomAsync 的 HttpClient 逻辑，
/// 调主 API /api/nodes/random-levels 拿候选谱面。
/// </summary>
public class HttpRandomLevelProvider : IRandomLevelProvider
{
    readonly string _apiUrl;
    readonly string _nodeToken;
    static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public HttpRandomLevelProvider(string apiUrl, string nodeToken)
    {
        _apiUrl = apiUrl;
        _nodeToken = nodeToken;
    }

    public async Task<RandomLevelItem[]?> GetRandomLevelsAsync(int count)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}/api/nodes/random-levels?count={count}");
            request.Headers.Add("X-Node-Token", _nodeToken);
            var httpResponse = await s_http.SendAsync(request);
            if (!httpResponse.IsSuccessStatusCode)
                return null;

            var response = await httpResponse.Content.ReadFromJsonAsync<RandomLevelApiResponse>();
            if (response?.Items == null || response.Items.Length == 0)
                return null;

            var items = new RandomLevelItem[response.Items.Length];
            for (int i = 0; i < response.Items.Length; i++)
            {
                items[i] = new RandomLevelItem
                {
                    Id = response.Items[i].Id,
                    LevelName = response.Items[i].LevelName ?? ""
                };
            }
            return items;
        }
        catch
        {
            return null;
        }
    }

    // 原 NodeRoom 里的私有 record，服务端解析主 API 响应用。
    record RandomLevelApiResponseItem(int Id, string? LevelName);
    record RandomLevelApiResponse(RandomLevelApiResponseItem[]? Items);
}
