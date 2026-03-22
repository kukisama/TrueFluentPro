using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Html.Parser;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 百度 HTML 搜索提供商（无需 API Key）。
/// 使用 AngleSharp 解析 DOM。百度链接为重定向 URL，直接返回（不额外跟随 302）。
/// </summary>
public sealed class BaiduSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;

    public string Id => "baidu";
    public string DisplayName => "百度";

    public BaiduSearchProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://www.baidu.com/s?wd={encoded}";

        var html = await _httpClient.GetStringAsync(url, ct);
        return ParseResults(html, maxResults);
    }

    private static IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();

        var context = BrowsingContext.New(Configuration.Default);
        var parser = context.GetService<IHtmlParser>()!;
        var document = parser.ParseDocument(html);

        // 百度搜索结果在 #content_left 下的 .result 元素中
        var items = document.QuerySelectorAll("#content_left .result");

        foreach (var item in items)
        {
            if (results.Count >= maxResults) break;

            var linkEl = item.QuerySelector("h3 > a");
            if (linkEl is null) continue;

            var rawUrl = linkEl.GetAttribute("href") ?? "";
            var title = linkEl.TextContent.Trim();

            // 摘要在 .c-abstract 或 .c-gap-top-small 中
            var snippetEl = item.QuerySelector(".c-abstract")
                         ?? item.QuerySelector(".c-gap-top-small");
            var snippet = snippetEl?.TextContent.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(title))
            {
                results.Add(new WebSearchResult
                {
                    Title = title,
                    Snippet = snippet,
                    Url = rawUrl   // 百度重定向 URL，直接返回
                });
            }
        }

        return results;
    }
}
