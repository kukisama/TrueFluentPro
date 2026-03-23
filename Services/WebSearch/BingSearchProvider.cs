using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Html.Parser;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// Bing HTML 搜索提供商（无需 API Key）。
/// 照搬 Cherry Studio LocalBingProvider + LocalSearchProvider 逻辑：
/// 1. 搜索页用隐藏浏览器 / HttpClient 获取 HTML
/// 2. 只从 #b_results h2 a 提取 title + url
/// 3. URL 通过 Base64 解码还原真实地址
/// 4. Cherry 给 Bing 查询追加 lang:zh（applyLanguageFilter）
/// </summary>
public sealed class BingSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly bool _international;

    public string Id { get; }
    public string DisplayName { get; }

    /// <param name="international">true = 国际版 (www.bing.com)；false = 中国版 (cn.bing.com)</param>
    public BingSearchProvider(HttpClient httpClient, bool international = true)
    {
        _httpClient = httpClient;
        _international = international;
        Id = international ? "bing" : "bing-cn";
        DisplayName = international ? "Bing 国际版" : "Bing 中国版";
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(query);

        // 国际版用 ensearch=1 确保返回国际结果（中国网络也能用）
        var url = _international
            ? $"https://www.bing.com/search?q={encoded}&ensearch=1"
            : $"https://cn.bing.com/search?q={encoded}";

        // 策略：先 Edge headless 解析，若 0 条结果则 fallback HttpClient 重新解析。
        // Edge --dump-dom 在 JS 未完成时可能输出空容器，仅检查容器存在不够可靠。

        // 1) Edge headless
        if (EdgeHeadlessBrowser.IsAvailable)
        {
            var rendered = await EdgeHeadlessBrowser.GetRenderedHtmlAsync(url, ct);
            if (!string.IsNullOrEmpty(rendered))
            {
                var edgeResults = ParseResults(rendered, maxResults);
                if (edgeResults.Count > 0) return edgeResults;
            }
        }

        // 2) Fallback: HttpClient
        var html = await GetSearchPageHtmlAsync(url, ct);
        return ParseResults(html, maxResults);
    }

    /// <summary>HttpClient 获取搜索页 HTML</summary>
    private async Task<string> GetSearchPageHtmlAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.5");
        using var resp = await _httpClient.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// 照搬 Cherry LocalBingProvider.parseValidUrls：
    /// 只从 #b_results h2 a 提取 title + url。
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();

        var context = BrowsingContext.New(Configuration.Default);
        var parser = context.GetService<IHtmlParser>()!;
        var document = parser.ParseDocument(html);

        // Cherry: doc.querySelectorAll('#b_results h2')
        var items = document.QuerySelectorAll("#b_results h2");

        foreach (var item in items)
        {
            if (results.Count >= maxResults) break;

            var linkEl = item.QuerySelector("a");
            if (linkEl is null) continue;

            var rawUrl = linkEl.GetAttribute("href") ?? "";
            var title = linkEl.TextContent.Trim();
            var decodedUrl = DecodeBingUrl(rawUrl);

            // Cherry: filter: item.url.startsWith('http') || item.url.startsWith('https')
            if (!string.IsNullOrWhiteSpace(title) &&
                (decodedUrl.StartsWith("http://") || decodedUrl.StartsWith("https://")))
            {
                // 跳过仍然是 bing.com 内部链接的结果
                if (Uri.TryCreate(decodedUrl, UriKind.Absolute, out var decodedUri) &&
                    decodedUri.Host.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new WebSearchResult
                {
                    Title = title,
                    Snippet = "",
                    Url = decodedUrl
                });
            }
        }

        return results;
    }

    /// <summary>
    /// 照搬 Cherry LocalBingProvider.decodeBingUrl：
    /// 解码 Bing 重定向 URL（u 参数 = "a1" + Base64 编码的真实 URL）。
    /// 
    /// 关键差异：JS atob() 自动处理 URL-safe Base64 字符，
    /// C# Convert.FromBase64String 不支持，必须手动替换 -→+ _→/ 并补 padding。
    /// </summary>
    private static string DecodeBingUrl(string bingUrl)
    {
        if (!Uri.TryCreate(bingUrl, UriKind.Absolute, out var uri))
            return bingUrl;

        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var encoded = queryParams["u"];
        if (string.IsNullOrEmpty(encoded) || encoded.Length < 3)
            return bingUrl;

        try
        {
            // Cherry: const base64Part = encodedUrl.substring(2); const decoded = atob(base64Part)
            var base64Part = encoded[2..];

            // JS atob() 容忍 URL-safe Base64，C# 必须手动转换
            base64Part = base64Part.Replace('-', '+').Replace('_', '/');
            // 补齐 Base64 padding
            switch (base64Part.Length % 4)
            {
                case 2: base64Part += "=="; break;
                case 3: base64Part += "="; break;
            }

            var decodedUrl = Encoding.UTF8.GetString(Convert.FromBase64String(base64Part));
            return decodedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decodedUrl : bingUrl;
        }
        catch
        {
            return bingUrl;
        }
    }

    /// <summary>
    /// Cherry fetch.ts fetchRedirectUrl 的 fallback：
    /// 如果 Base64 解码失败，用 HEAD 请求跟随重定向获取真实 URL。
    /// </summary>
    internal static async Task<string> ResolveRedirectAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString();
            return !string.IsNullOrEmpty(finalUrl) ? finalUrl : url;
        }
        catch
        {
            return url;
        }
    }
}
