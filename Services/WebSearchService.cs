using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace TrueFluentPro.Services;

/// <summary>
/// 轻量级 Web 搜索服务，使用 DuckDuckGo HTML 搜索（无需 API Key）。
/// 用于为 AI 聊天提供实时网络信息上下文。
/// </summary>
public sealed class WebSearchService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" }
        }
    };

    /// <summary>搜索结果条目</summary>
    public sealed class SearchResult
    {
        public string Title { get; init; } = "";
        public string Snippet { get; init; } = "";
        public string Url { get; init; } = "";
    }

    /// <summary>
    /// 执行 Web 搜索，返回摘要结果列表。
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        var encoded = HttpUtility.UrlEncode(query);
        var url = $"https://html.duckduckgo.com/html/?q={encoded}";

        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);
            return ParseResults(html, maxResults);
        }
        catch
        {
            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>
    /// 将搜索结果格式化为 AI 上下文文本。
    /// </summary>
    public static string FormatAsContext(IReadOnlyList<SearchResult> results, string query)
    {
        if (results.Count == 0)
            return "";

        var lines = new List<string>
        {
            $"[Web 搜索结果: \"{query}\"]",
            ""
        };

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            lines.Add($"{i + 1}. {r.Title}");
            if (!string.IsNullOrEmpty(r.Snippet))
                lines.Add($"   {r.Snippet}");
            if (!string.IsNullOrEmpty(r.Url))
                lines.Add($"   来源: {r.Url}");
            lines.Add("");
        }

        lines.Add("[请基于以上搜索结果回答用户问题，如果搜索结果不足以回答，请说明。]");
        return string.Join("\n", lines);
    }

    private static IReadOnlyList<SearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        // 匹配 DuckDuckGo HTML 搜索结果中的链接和摘要
        var resultPattern = new Regex(
            @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>.*?<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in resultPattern.Matches(html))
        {
            if (results.Count >= maxResults) break;

            var rawUrl = match.Groups[1].Value;
            var title = StripHtml(match.Groups[2].Value);
            var snippet = StripHtml(match.Groups[3].Value);

            // DuckDuckGo 的 href 是重定向 URL，提取实际 URL
            var actualUrl = ExtractActualUrl(rawUrl);

            if (!string.IsNullOrWhiteSpace(title))
            {
                results.Add(new SearchResult
                {
                    Title = title.Trim(),
                    Snippet = snippet.Trim(),
                    Url = actualUrl
                });
            }
        }

        return results;
    }

    private static string ExtractActualUrl(string ddgUrl)
    {
        // DuckDuckGo redirect: //duckduckgo.com/l/?uddg=<encoded_url>&...
        var match = Regex.Match(ddgUrl, @"uddg=([^&]+)");
        if (match.Success)
        {
            return HttpUtility.UrlDecode(match.Groups[1].Value);
        }
        return ddgUrl;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<[^>]+>", "");
        return HttpUtility.HtmlDecode(text);
    }
}
