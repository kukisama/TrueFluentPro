using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// DuckDuckGo HTML 搜索提供商（无需 API Key）。
/// 迁移自原 WebSearchService 的逻辑。
/// </summary>
public sealed class DuckDuckGoSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;

    public string Id => "duckduckgo";
    public string DisplayName => "DuckDuckGo";

    public DuckDuckGoSearchProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var encoded = HttpUtility.UrlEncode(query);
        var url = $"https://html.duckduckgo.com/html/?q={encoded}&kl=cn-zh";

        var html = await _httpClient.GetStringAsync(url, ct);
        return ParseResults(html, maxResults);
    }

    private static IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var pattern = new Regex(
            @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>.*?<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in pattern.Matches(html))
        {
            if (results.Count >= maxResults) break;

            var rawUrl = match.Groups[1].Value;
            var title = HtmlHelper.StripHtml(match.Groups[2].Value);
            var snippet = HtmlHelper.StripHtml(match.Groups[3].Value);
            var actualUrl = ExtractActualUrl(rawUrl);

            if (!string.IsNullOrWhiteSpace(title))
            {
                results.Add(new WebSearchResult
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
        var match = Regex.Match(ddgUrl, @"uddg=([^&]+)");
        return match.Success ? HttpUtility.UrlDecode(match.Groups[1].Value) : ddgUrl;
    }
}
