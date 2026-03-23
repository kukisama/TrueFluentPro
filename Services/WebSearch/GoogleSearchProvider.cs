using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// Google HTML 搜索提供商（无需 API Key）。
/// 适合能访问 Google 的网络环境；若页面结构变化或返回验证页，会自动回退到普通 HTTP 抓取结果。
/// </summary>
public sealed class GoogleSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;

    public string Id => "google";
    public string DisplayName => "Google";

    public GoogleSearchProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var encoded = HttpUtility.UrlEncode(query);
        var count = Math.Clamp(maxResults, 1, 10);
        var url = $"https://www.google.com/search?q={encoded}&hl=zh-CN&num={count}";

        if (EdgeHeadlessBrowser.IsAvailable)
        {
            var rendered = await EdgeHeadlessBrowser.GetRenderedHtmlAsync(url, ct);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                var edgeResults = ParseResults(rendered, count);
                if (edgeResults.Count > 0)
                    return edgeResults;
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.5");
        using var resp = await _httpClient.SendAsync(req, ct);
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ParseResults(html, count);
    }

    private static IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var context = BrowsingContext.New(Configuration.Default);
        var parser = context.GetService<IHtmlParser>()!;
        var document = parser.ParseDocument(html);

        foreach (var heading in document.QuerySelectorAll("h3"))
        {
            if (results.Count >= maxResults)
                break;

            var title = heading.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var linkEl = FindAncestorLink(heading);
            if (linkEl is null)
                continue;

            var rawUrl = linkEl.GetAttribute("href") ?? "";
            var actualUrl = ExtractActualUrl(rawUrl);
            if (!Uri.TryCreate(actualUrl, UriKind.Absolute, out var actualUri))
                continue;

            if (actualUri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(actualUrl))
                continue;

            results.Add(new WebSearchResult
            {
                Title = title,
                Snippet = ExtractSnippet(heading),
                Url = actualUrl
            });
        }

        return results;
    }

    private static IElement? FindAncestorLink(IElement element)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            if (string.Equals(current.TagName, "A", StringComparison.OrdinalIgnoreCase))
                return current;

            current = current.ParentElement;
        }

        return null;
    }

    private static string ExtractSnippet(IElement heading)
    {
        var current = heading.ParentElement;
        while (current is not null)
        {
            var snippet = current.QuerySelector("div.VwiC3b, div[data-sncf='1'], span.aCOpRe, .MUxGbd")
                ?.TextContent
                .Trim();

            if (!string.IsNullOrWhiteSpace(snippet))
                return snippet;

            current = current.ParentElement;
        }

        return "";
    }

    private static string ExtractActualUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return rawUrl;

        if (rawUrl.StartsWith("/url?", StringComparison.OrdinalIgnoreCase))
            rawUrl = "https://www.google.com" + rawUrl;

        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            if (uri.AbsolutePath.Equals("/url", StringComparison.OrdinalIgnoreCase))
            {
                var query = HttpUtility.ParseQueryString(uri.Query);
                var actualUrl = query["q"] ?? query["url"];
                if (!string.IsNullOrWhiteSpace(actualUrl))
                    return actualUrl;
            }

            return rawUrl;
        }

        return rawUrl;
    }
}