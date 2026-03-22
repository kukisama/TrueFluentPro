using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Html.Parser;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// Bing 新闻搜索提供商（无需 API Key）。
/// 使用 Bing News (bing.com/news/search) 获取新闻结果，
/// 适合时事、资讯类查询。
/// </summary>
public sealed class BingNewsSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;

    public string Id => "bing-news";
    public string DisplayName => "Bing 新闻";

    public BingNewsSearchProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://www.bing.com/news/search?q={encoded}&ensearch=1";

        var html = await _httpClient.GetStringAsync(url, ct);
        return ParseResults(html, maxResults);
    }

    private static IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();

        var context = BrowsingContext.New(Configuration.Default);
        var parser = context.GetService<IHtmlParser>()!;
        var document = parser.ParseDocument(html);

        // Bing News 结果在 .news-card 或 .newsitem 元素中
        var items = document.QuerySelectorAll(".news-card");
        if (items.Length == 0)
            items = document.QuerySelectorAll("[data-id]");

        foreach (var item in items)
        {
            if (results.Count >= maxResults) break;

            // 标题链接
            var linkEl = item.QuerySelector("a.title")
                      ?? item.QuerySelector("a[href]");
            if (linkEl is null) continue;

            var rawUrl = linkEl.GetAttribute("href") ?? "";
            var title = linkEl.TextContent.Trim();

            // 摘要
            var snippetEl = item.QuerySelector(".snippet")
                         ?? item.QuerySelector("p");
            var snippet = snippetEl?.TextContent.Trim() ?? "";

            // 来源 + 时间
            var sourceEl = item.QuerySelector(".source");
            var source = sourceEl?.TextContent.Trim() ?? "";
            if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(snippet))
                snippet = $"[{source}] {snippet}";

            if (!string.IsNullOrWhiteSpace(title) && rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new WebSearchResult
                {
                    Title = title,
                    Snippet = snippet,
                    Url = rawUrl
                });
            }
        }

        return results;
    }
}
