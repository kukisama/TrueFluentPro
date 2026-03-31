using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using ReverseMarkdown;
using SmartReader;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 网页正文提取：使用 Edge headless 获取 JS 渲染后的完整 HTML，
/// 再用 SmartReader (Readability) 提取正文 + ReverseMarkdown 转 Markdown。
/// </summary>
public sealed class WebPageFetcher
{
    /// <summary>
    /// 正文截取上限。3500 字符约 1750 token，5 条约 8750 token。
    /// </summary>
    private const int MaxBodyChars = 3500;

    /// <summary>ReverseMarkdown 转换器（等效 Cherry 的 TurndownService）</summary>
    private static readonly Converter MdConverter = new(new Config
    {
        UnknownTags = Config.UnknownTagsOption.Drop,
        RemoveComments = true,
        SmartHrefHandling = true
    });

    /// <summary>
    /// 使用 Edge headless 获取 JS 渲染后的完整 HTML，
    /// SmartReader 提取正文 → ReverseMarkdown 转 Markdown。
    /// 失败时返回空字符串，不抛异常。
    /// </summary>
    public async Task<string> FetchTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var html = await EdgeHeadlessBrowser.GetRenderedHtmlAsync(url, ct);
            if (string.IsNullOrWhiteSpace(html)) return "";

            var article = Reader.ParseArticle(url, html);
            if (!article.IsReadable) return FallbackExtract(html);

            var articleHtml = article.Content;
            if (string.IsNullOrWhiteSpace(articleHtml))
            {
                var text = article.TextContent?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(text)) return FallbackExtract(html);
                return Truncate(text);
            }

            var markdown = MdConverter.Convert(articleHtml).Trim();
            if (string.IsNullOrWhiteSpace(markdown)) return FallbackExtract(html);

            return Truncate(CleanMarkdown(markdown));
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string s) =>
        s.Length > MaxBodyChars ? s[..MaxBodyChars] : s;

    /// <summary>
    /// 后处理清理 Markdown：去除图片、多余链接、连续空行等噪音，
    /// 最大化有效正文在字符配额中的占比。
    /// </summary>
    private static string CleanMarkdown(string md)
    {
        // 去除 Markdown 图片 ![alt](url)
        md = Regex.Replace(md, @"!\[[^\]]*\]\([^)]+\)", "");
        // 去除残留的 HTML <img> / <picture> / <figure> / <source> 标签
        md = Regex.Replace(md, @"<(?:img|picture|figure|source|video|audio|svg)[^>]*/?>", "", RegexOptions.IgnoreCase);
        // 去除纯链接行（整行只有一个 Markdown 链接，常见于导航/推荐区）
        md = Regex.Replace(md, @"^\s*\[[^\]]+\]\([^)]+\)\s*$", "", RegexOptions.Multiline);
        // 将 3 个及以上连续空行压缩为 2 个
        md = Regex.Replace(md, @"(\r?\n){3,}", "\n\n");
        return md.Trim();
    }

    /// <summary>SmartReader 提取失败时的 fallback：用 AngleSharp 粗提取纯文本</summary>
    private static string FallbackExtract(string html)
    {
        try
        {
            var context = AngleSharp.BrowsingContext.New(AngleSharp.Configuration.Default);
            var parser = context.GetService<IHtmlParser>()!;
            var doc = parser.ParseDocument(html);

            foreach (var tag in new[] { "script", "style", "nav", "header", "footer", "aside", "iframe", "noscript" })
                foreach (var el in doc.QuerySelectorAll(tag).ToArray())
                    el.Remove();

            var mainEl = doc.QuerySelector("article") ?? doc.QuerySelector("main") ?? doc.Body;
            if (mainEl is null) return "";

            var text = mainEl.TextContent?.Trim() ?? "";
            text = Regex.Replace(text, @"\s{2,}", " ");
            return text.Length > MaxBodyChars ? text[..MaxBodyChars] : text;
        }
        catch { return ""; }
    }
}
