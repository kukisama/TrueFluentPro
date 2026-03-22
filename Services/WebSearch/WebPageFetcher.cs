using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReverseMarkdown;
using SmartReader;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 网页正文提取：照搬 Cherry Studio fetch.ts fetchWebContent 逻辑。
/// Cherry: Readability.js 提取 HTML → TurndownService 转 Markdown
/// 我们: SmartReader (Readability C# 端口) 提取 HTML → ReverseMarkdown 转 Markdown
/// </summary>
public sealed class WebPageFetcher
{
    /// <summary>
    /// 正文截取上限。Cherry 无硬限制（靠后续 RAG/cutoff 压缩），
    /// 但我们必须防止 token 爆炸。8000 字符约 4000 token，5 条约 20k token。
    /// </summary>
    private const int MaxBodyChars = 8000;

    private readonly HttpClient _http;

    /// <summary>ReverseMarkdown 转换器（等效 Cherry 的 TurndownService）</summary>
    private static readonly Converter MdConverter = new(new Config
    {
        UnknownTags = Config.UnknownTagsOption.Bypass,
        RemoveComments = true,
        SmartHrefHandling = true
    });

    public WebPageFetcher(HttpClient http) => _http = http;

    /// <summary>
    /// 照搬 Cherry fetchWebContent(url, 'markdown')：
    /// 1. HTTP GET 获取 HTML
    /// 2. SmartReader (Readability) 提取正文 HTML
    /// 3. ReverseMarkdown 转 Markdown（保留标题/列表/链接结构）
    /// 失败时返回空字符串，不抛异常。
    /// </summary>
    public async Task<string> FetchTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return "";

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return "";

            var html = await resp.Content.ReadAsStringAsync(cts.Token);

            // Cherry: const article = new Readability(doc).parse()
            var article = Reader.ParseArticle(url, html);
            if (!article.IsReadable) return FallbackExtract(html);

            // Cherry: TurndownService.turndown(article.content) → Markdown
            // 我们: ReverseMarkdown.Convert(article.Content) → Markdown
            var articleHtml = article.Content;
            if (string.IsNullOrWhiteSpace(articleHtml))
            {
                // Content 为空时退化为纯文本
                var text = article.TextContent?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(text)) return FallbackExtract(html);
                return Truncate(text);
            }

            var markdown = MdConverter.Convert(articleHtml).Trim();
            if (string.IsNullOrWhiteSpace(markdown)) return FallbackExtract(html);

            return Truncate(markdown);
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string s) =>
        s.Length > MaxBodyChars ? s[..MaxBodyChars] : s;

    /// <summary>SmartReader 提取失败时的 fallback：用 AngleSharp 粗提取</summary>
    private static string FallbackExtract(string html)
    {
        try
        {
            var context = AngleSharp.BrowsingContext.New(AngleSharp.Configuration.Default);
            var parser = context.GetService<AngleSharp.Html.Parser.IHtmlParser>()!;
            var doc = parser.ParseDocument(html);

            foreach (var tag in new[] { "script", "style", "nav", "header", "footer", "aside", "iframe", "noscript" })
                foreach (var el in doc.QuerySelectorAll(tag))
                    el.Remove();

            var mainEl = doc.QuerySelector("article") ?? doc.QuerySelector("main") ?? doc.Body;
            if (mainEl is null) return "";

            var text = mainEl.TextContent?.Trim() ?? "";
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ");
            return text.Length > MaxBodyChars ? text[..MaxBodyChars] : text;
        }
        catch { return ""; }
    }
}
