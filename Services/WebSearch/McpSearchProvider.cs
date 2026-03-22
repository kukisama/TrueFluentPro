using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// MCP 搜索提供商 — 通过 JSON-RPC 2.0 调用 MCP Server 的搜索工具。
/// 兼容任何遵循 MCP 协议的搜索服务（Exa MCP、Tavily MCP、Searxng MCP 等）。
/// </summary>
public sealed class McpSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _toolName;
    private readonly string? _apiKey;

    public string Id => "mcp";
    public string DisplayName => "MCP";

    public McpSearchProvider(HttpClient httpClient, string endpoint, string toolName, string? apiKey = null)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _toolName = toolName;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            return [];

        var request = new JsonRpcRequest
        {
            Method = "tools/call",
            Params = new ToolCallParams
            {
                Name = _toolName,
                Arguments = new Dictionary<string, object>
                {
                    ["query"] = query,
                    ["numResults"] = maxResults
                }
            }
        };

        var json = JsonSerializer.Serialize(request, JsonRpcSerializerOptions.Default);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        httpRequest.Content = content;

        if (!string.IsNullOrWhiteSpace(_apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(responseJson, maxResults);
    }

    private static IReadOnlyList<WebSearchResult> ParseResponse(string json, int maxResults)
    {
        var results = new List<WebSearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // MCP 工具调用结果通常在 result.content 数组中
            if (!root.TryGetProperty("result", out var result))
                return results;

            if (result.TryGetProperty("content", out var contentArray) &&
                contentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentArray.EnumerateArray())
                {
                    if (results.Count >= maxResults) break;

                    // 尝试解析结构化搜索结果
                    var title = item.TryGetString("title") ?? "";
                    var url = item.TryGetString("url") ?? "";
                    var snippet = item.TryGetString("snippet")
                               ?? item.TryGetString("text")
                               ?? item.TryGetString("description")
                               ?? "";

                    // 如果是纯文本内容块，尝试从 text 字段提取
                    if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(url))
                    {
                        var text = item.TryGetString("text") ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            results.Add(new WebSearchResult
                            {
                                Title = text.Length > 80 ? text[..80] + "..." : text,
                                Url = "",
                                Snippet = text
                            });
                        }
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(url))
                    {
                        results.Add(new WebSearchResult
                        {
                            Title = title,
                            Url = url,
                            Snippet = snippet
                        });
                    }
                }
            }
        }
        catch
        {
            // JSON 解析失败静默处理
        }

        return results;
    }

    // ── JSON-RPC 请求类型 ──

    private sealed class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; } = 1;

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public ToolCallParams? Params { get; set; }
    }

    private sealed class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("arguments")]
        public Dictionary<string, object> Arguments { get; set; } = [];
    }

    private static class JsonRpcSerializerOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}

/// <summary>JsonElement 辅助扩展</summary>
internal static class JsonElementExtensions
{
    public static string? TryGetString(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
