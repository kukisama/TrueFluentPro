using System.Text.RegularExpressions;
using System.Web;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>HTML 文本清理工具</summary>
internal static class HtmlHelper
{
    /// <summary>移除 HTML 标签并解码实体</summary>
    public static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"<[^>]+>", "");
        return HttpUtility.HtmlDecode(text);
    }
}
