using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TrueFluentPro.Services
{
    /// <summary>从总结的一级标题生成录音显示名称，不修改实际文件名。</summary>
    public static class AudioRecordingTitle
    {
        public static string Build(string fileName, DateTime createdAt, string? summaryMarkdown)
        {
            var title = ExtractSummaryTitle(summaryMarkdown);
            if (string.IsNullOrWhiteSpace(title)) return fileName;

            // 录音名称中的时间比导入数据库时的创建时间更准确。
            var recordedAt = DateTime.TryParseExact(
                Path.GetFileNameWithoutExtension(fileName), "'Audio_'yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
                ? timestamp : createdAt;
            return $"{recordedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {title}";
        }

        /// <summary>
        /// 提取首个顶层一级 Markdown 标题；忽略代码围栏和普通正文，无标题返回 null。
        /// Markdown 解析器同时处理 ATX / Setext、尾部 # 和强调标记。
        /// </summary>
        public static string? ExtractSummaryTitle(string? summaryMarkdown)
        {
            if (string.IsNullOrWhiteSpace(summaryMarkdown)) return null;

            var heading = Markdown.Parse(summaryMarkdown).OfType<HeadingBlock>()
                .FirstOrDefault(block => block.Level == 1);
            if (heading?.Inline == null) return null;

            var text = new StringBuilder();
            AppendInlineText(heading.Inline, text);
            var title = text.ToString().Trim();
            return title.Length == 0 ? null : title;
        }

        private static void AppendInlineText(ContainerInline container, StringBuilder text)
        {
            for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        text.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        text.Append(code.Content);
                        break;
                    case LineBreakInline:
                        text.Append(' ');
                        break;
                    case ContainerInline nested:
                        AppendInlineText(nested, text);
                        break;
                }
            }
        }
    }
}