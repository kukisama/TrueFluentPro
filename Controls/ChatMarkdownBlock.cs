using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace TrueFluentPro.Controls;

/// <summary>
/// 轻量 Markdown 渲染控件，支持文本选中 + 代码块复制。
/// </summary>
public class ChatMarkdownBlock : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ChatMarkdownBlock, string?>(nameof(Markdown));

    public static readonly StyledProperty<double> MaxContentWidthProperty =
        AvaloniaProperty.Register<ChatMarkdownBlock, double>(nameof(MaxContentWidth), 500);

    public static readonly StyledProperty<bool> IsReasoningProperty =
        AvaloniaProperty.Register<ChatMarkdownBlock, bool>(nameof(IsReasoning));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public double MaxContentWidth
    {
        get => GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    public bool IsReasoning
    {
        get => GetValue(IsReasoningProperty);
        set => SetValue(IsReasoningProperty, value);
    }

    static ChatMarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<ChatMarkdownBlock>((x, _) => x.RebuildContent());
    }

    public ChatMarkdownBlock()
    {
        Spacing = 2;
    }

    // ── 渲染入口 ──────────────────────────────────────────

    private void RebuildContent()
    {
        Children.Clear();

        var md = Markdown;
        if (string.IsNullOrEmpty(md)) return;

        var blocks = ParseBlocks(md);
        foreach (var block in blocks)
        {
            switch (block.Type)
            {
                case BlockType.Code:
                    Children.Add(BuildCodeBlock(block));
                    break;
                case BlockType.HorizontalRule:
                    Children.Add(BuildHorizontalRule());
                    break;
                default:
                    Children.Add(BuildTextBlock(block));
                    break;
            }
        }
    }

    // ── 段落渲染 ──────────────────────────────────────────

    private Control BuildTextBlock(MdBlock block)
    {
        var stb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxContentWidth,
            ContextFlyout = null,
            LineHeight = IsReasoning ? 18 : 22,
            Padding = new Thickness(0, 1, 0, 1),
        };
        if (IsReasoning)
        {
            stb.FontSize = 12;
            stb[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");
        }

        PopulateInlines(stb.Inlines!, block.Content);
        return stb;
    }

    // ── 代码块渲染 ────────────────────────────────────────

    private Control BuildCodeBlock(MdBlock block)
    {
        var codeText = block.Content;

        var codeStb = new SelectableTextBlock
        {
            Text = codeText,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace"),
            FontSize = 12.5,
            LineHeight = 19,
            ContextFlyout = null,
        };

        var langLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(block.Language) ? "代码" : block.Language,
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copyBtn = new Button
        {
            Content = "📋 复制",
            Padding = new Thickness(6, 2),
            FontSize = 11,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = codeText,
        };
        copyBtn.Click += CopyCodeBlock_Click;

        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 2),
        };
        Grid.SetColumn(langLabel, 0);
        Grid.SetColumn(copyBtn, 1);
        headerGrid.Children.Add(langLabel);
        headerGrid.Children.Add(copyBtn);

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(codeStb);

        var border = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = MaxContentWidth,
        };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension("CodeBlockBackgroundBrush");

        return border;
    }

    // ── 分隔线渲染 ────────────────────────────────────────

    private Control BuildHorizontalRule()
    {
        return new Rectangle
        {
            Height = 1,
            MaxWidth = MaxContentWidth,
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Fill = Brushes.Gray,
            Opacity = 0.4,
        };
    }

    private static async void CopyCodeBlock_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var text = btn.Tag as string;
        if (string.IsNullOrEmpty(text)) return;

        var topLevel = TopLevel.GetTopLevel(btn);
        if (topLevel?.Clipboard == null) return;
        await topLevel.Clipboard.SetTextAsync(text);

        var original = btn.Content;
        btn.Content = "✅ 已复制";
        await System.Threading.Tasks.Task.Delay(1200);
        btn.Content = original;
    }

    // ── Markdown 块解析 ───────────────────────────────────

    private enum BlockType { Text, Code, HorizontalRule }

    private sealed class MdBlock
    {
        public BlockType Type { get; init; }
        public string Content { get; init; } = "";
        public string? Language { get; init; }
    }

    private static readonly Regex CodeBlockRegex = new(
        @"^(?<fence>`{3,}|~{3,})(?<lang>[^\r\n]*)\r?\n(?<code>[\s\S]*?)^\k<fence>[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex HrRegex = new(
        @"^[ \t]{0,3}(?:[-]{3,}|[*]{3,}|[_]{3,})[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new(
        @"\uFFFECODE(\d+)\uFFFE", RegexOptions.Compiled);

    private static List<MdBlock> ParseBlocks(string markdown)
    {
        var blocks = new List<MdBlock>();

        // 统一换行符，避免 CRLF 导致正则不匹配
        markdown = markdown.Replace("\r\n", "\n");

        // 先把代码块替换为占位符，避免在代码内部误识别 HR
        var placeholders = new List<MdBlock>();
        var withPlaceholders = CodeBlockRegex.Replace(markdown, m =>
        {
            var idx = placeholders.Count;
            placeholders.Add(new MdBlock
            {
                Type = BlockType.Code,
                Content = m.Groups["code"].Value.TrimEnd(),
                Language = m.Groups["lang"].Value.Trim(),
            });
            return $"\uFFFECODE{idx}\uFFFE";
        });

        // 按 HR 分段
        var parts = HrRegex.Split(withPlaceholders);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                blocks.Add(new MdBlock { Type = BlockType.HorizontalRule });

            var part = parts[i].Trim();
            if (string.IsNullOrEmpty(part)) continue;

            // 恢复代码块占位符
            int lastEnd = 0;
            foreach (Match m in PlaceholderRegex.Matches(part))
            {
                if (m.Index > lastEnd)
                {
                    var text = part[lastEnd..m.Index].Trim();
                    if (!string.IsNullOrEmpty(text))
                        blocks.Add(new MdBlock { Type = BlockType.Text, Content = text });
                }
                blocks.Add(placeholders[int.Parse(m.Groups[1].Value)]);
                lastEnd = m.Index + m.Length;
            }

            if (lastEnd < part.Length)
            {
                var text = part[lastEnd..].Trim();
                if (!string.IsNullOrEmpty(text))
                    blocks.Add(new MdBlock { Type = BlockType.Text, Content = text });
            }
        }

        return blocks;
    }

    // ── 内联 Markdown 解析 ────────────────────────────────

    private static void PopulateInlines(InlineCollection inlines, string text)
    {
        var lines = text.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];

            // 空行 → 段落间距（用两个 LineBreak 模拟）
            if (string.IsNullOrWhiteSpace(line))
            {
                inlines.Add(new LineBreak());
                continue;
            }

            if (li > 0 && !string.IsNullOrWhiteSpace(lines[li - 1]))
                inlines.Add(new LineBreak());

            // 标题行 ##
            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var fontSize = level switch { 1 => 20.0, 2 => 17.0, 3 => 15.0, _ => 14.0 };
                var span = new Span { FontWeight = FontWeight.Bold, FontSize = fontSize };
                AddFormattedText(span.Inlines, headingMatch.Groups[2].Value);
                inlines.Add(span);
                continue;
            }

            // 无序列表 - / * / +
            var listMatch = Regex.Match(line, @"^(\s*)([-*+])\s+(.+)$");
            if (listMatch.Success)
            {
                var indent = listMatch.Groups[1].Value.Length / 2;
                inlines.Add(new Run(new string(' ', indent * 3) + "  • "));
                AddFormattedText(inlines, listMatch.Groups[3].Value);
                continue;
            }

            // 有序列表
            var orderedMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.+)$");
            if (orderedMatch.Success)
            {
                var indent = orderedMatch.Groups[1].Value.Length / 2;
                inlines.Add(new Run(new string(' ', indent * 3) + "  " + orderedMatch.Groups[2].Value + ". "));
                AddFormattedText(inlines, orderedMatch.Groups[3].Value);
                continue;
            }

            // 引用 >
            var quoteMatch = Regex.Match(line, @"^>\s?(.*)$");
            if (quoteMatch.Success)
            {
                inlines.Add(new Run("  │ ") { Foreground = Brushes.Gray });
                var span = new Span { FontStyle = FontStyle.Italic, Foreground = Brushes.Gray };
                AddFormattedText(span.Inlines, quoteMatch.Groups[1].Value);
                inlines.Add(span);
                continue;
            }

            // 普通文本行
            AddFormattedText(inlines, line);
        }
    }

    // ── 行内格式：**bold**, *italic*, `code`, ***bolditalic*** ──

    private static readonly Regex InlinePattern = new(
        @"(\*{3})(.+?)\1" +       // group 1,2: ***bolditalic***
        @"|(\*{2})(.+?)\3" +       // group 3,4: **bold**
        @"|(\*{1})(.+?)\5" +       // group 5,6: *italic*
        @"|(`+)(.+?)\7",           // group 7,8: `code`
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static void AddFormattedText(InlineCollection inlines, string text)
    {
        int lastEnd = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > lastEnd)
                inlines.Add(new Run(text[lastEnd..m.Index]));

            if (m.Groups[1].Success) // ***bolditalic***
            {
                inlines.Add(new Span
                {
                    FontWeight = FontWeight.Bold,
                    FontStyle = FontStyle.Italic,
                    Inlines = { new Run(m.Groups[2].Value) }
                });
            }
            else if (m.Groups[3].Success) // **bold**
            {
                inlines.Add(new Span
                {
                    FontWeight = FontWeight.Bold,
                    Inlines = { new Run(m.Groups[4].Value) }
                });
            }
            else if (m.Groups[5].Success) // *italic*
            {
                inlines.Add(new Span
                {
                    FontStyle = FontStyle.Italic,
                    Inlines = { new Run(m.Groups[6].Value) }
                });
            }
            else if (m.Groups[7].Success) // `code`
            {
                inlines.Add(new Span
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace"),
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.Parse("#D63384")),
                    Inlines = { new Run(m.Groups[8].Value) }
                });
            }

            lastEnd = m.Index + m.Length;
        }

        if (lastEnd < text.Length)
            inlines.Add(new Run(text[lastEnd..]));
    }
}
