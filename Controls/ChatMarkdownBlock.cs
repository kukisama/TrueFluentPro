using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace TrueFluentPro.Controls;

/// <summary>
/// Markdown 渲染控件，基于 Markdig AST + 增量渲染 + 语法高亮。
/// 流式更新时只重建变化的尾部块，已完成的块保留选区不闪烁。
/// </summary>
public class ChatMarkdownBlock : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ChatMarkdownBlock, string?>(nameof(Markdown));

    public static readonly StyledProperty<double> MaxContentWidthProperty =
        AvaloniaProperty.Register<ChatMarkdownBlock, double>(nameof(MaxContentWidth), double.PositiveInfinity);

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

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    // ── 字体 ─────────────────────────────────────────────
    private static readonly FontFamily s_monoFont = new("Cascadia Code, Consolas, Courier New, monospace");
    private static readonly FontFamily s_bodyFont = new("Microsoft YaHei UI, Segoe UI, Inter, sans-serif");

    // ── 固定色（主题无关） ───────────────────────────────
    private static readonly IBrush s_linkFg = new SolidColorBrush(Color.Parse("#0969DA"));

    /// <summary>每个已渲染块的源文本，用于增量比较</summary>
    private readonly List<string> _blockSources = new();

    static ChatMarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<ChatMarkdownBlock>((x, _) => x.UpdateContent());
        MaxContentWidthProperty.Changed.AddClassHandler<ChatMarkdownBlock>((x, _) => x.ForceRerender());
    }

    public ChatMarkdownBlock()
    {
        Spacing = 6;
    }

    /// <summary>MaxContentWidth 变化时强制全量重建（宽度影响排版）</summary>
    private void ForceRerender()
    {
        _blockSources.Clear();
        UpdateContent();
    }

    // ── 增量渲染入口 ─────────────────────────────────────

    private void UpdateContent()
    {
        var md = Markdown;
        if (string.IsNullOrEmpty(md))
        {
            Children.Clear();
            _blockSources.Clear();
            return;
        }

        var doc = Markdig.Markdown.Parse(md, s_pipeline);

        var newSources = new List<string>(doc.Count);
        foreach (var b in doc)
            newSources.Add(ExtractSource(md, b));

        int match = 0;
        int min = Math.Min(_blockSources.Count, newSources.Count);
        while (match < min && _blockSources[match] == newSources[match])
            match++;

        while (Children.Count > match)
            Children.RemoveAt(Children.Count - 1);
        while (_blockSources.Count > match)
            _blockSources.RemoveAt(_blockSources.Count - 1);

        for (int i = match; i < doc.Count; i++)
        {
            Children.Add(RenderBlock(doc[i]));
            _blockSources.Add(newSources[i]);
        }
    }

    private static string ExtractSource(string md, Block block)
    {
        var start = block.Span.Start;
        if (start < 0 || start >= md.Length) return "";
        var end = Math.Min(block.Span.End, md.Length - 1);
        if (end < start) return "";
        return md[start..(end + 1)];
    }

    // ── 块级渲染分发 ─────────────────────────────────────

    private Control RenderBlock(Block block) => block switch
    {
        HeadingBlock h => RenderHeading(h),
        ParagraphBlock p => RenderParagraph(p),
        FencedCodeBlock fc => RenderCodeBlock(fc.Lines.ToString().TrimEnd(), fc.Info?.Trim()),
        CodeBlock cb => RenderCodeBlock(cb.Lines.ToString().TrimEnd(), null),
        ListBlock l => RenderList(l, 0),
        ThematicBreakBlock => RenderHorizontalRule(),
        QuoteBlock q => RenderQuote(q),
        Table t => RenderTable(t),
        _ => RenderFallback(block),
    };

    // ── 排版基础 ─────────────────────────────────────────

    private SelectableTextBlock CreateBaseTextBlock()
    {
        var stb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxContentWidth,
            ContextFlyout = null,
            FontFamily = s_bodyFont,
            FontSize = IsReasoning ? 12.5 : 14,
            LineHeight = IsReasoning ? 20 : 24,
            Padding = new Thickness(0, 1, 0, 1),
            // LetterSpacing 提升阅读舒适度
            LetterSpacing = 0.2,
        };
        if (IsReasoning)
        {
            stb[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");
        }
        return stb;
    }

    // ── 段落 ─────────────────────────────────────────────

    private Control RenderParagraph(ParagraphBlock para)
    {
        var stb = CreateBaseTextBlock();
        stb.Margin = new Thickness(0, 2, 0, 2);
        if (para.Inline != null)
            RenderInlines(stb.Inlines!, para.Inline);
        return stb;
    }

    // ── 标题 ─────────────────────────────────────────────

    private Control RenderHeading(HeadingBlock heading)
    {
        var (fontSize, topMargin, bottomMargin) = heading.Level switch
        {
            1 => (22.0, 12.0, 6.0),
            2 => (19.0, 10.0, 5.0),
            3 => (16.0, 8.0, 4.0),
            _ => (14.5, 6.0, 3.0),
        };
        var stb = CreateBaseTextBlock();
        stb.FontWeight = FontWeight.SemiBold;
        stb.FontSize = fontSize;
        stb.LineHeight = fontSize * 1.35;
        stb.LetterSpacing = -0.2;
        stb.Margin = new Thickness(0, topMargin, 0, bottomMargin);
        if (heading.Inline != null)
            RenderInlines(stb.Inlines!, heading.Inline);

        // H1/H2 加底部分隔线
        if (heading.Level <= 2)
        {
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(stb);
            stack.Children.Add(new Rectangle
            {
                Height = 1,
                MaxWidth = MaxContentWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Opacity = 0.15,
            });
            stack.Children[1][!Shape.FillProperty] = new DynamicResourceExtension("TextPrimaryBrush");
            return stack;
        }
        return stb;
    }

    // ── 代码块（语法高亮） ───────────────────────────────

    private Control RenderCodeBlock(string code, string? language)
    {
        var codeStb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = s_monoFont,
            FontSize = 13,
            LineHeight = 20,
            ContextFlyout = null,
            LetterSpacing = 0,
        };

        // 语法高亮 or 纯文本
        var normalizedLang = (language ?? "").ToLowerInvariant().Trim();
        if (CodeHighlighter.CanHighlight(normalizedLang))
            CodeHighlighter.Highlight(codeStb.Inlines!, code, normalizedLang);
        else
            codeStb.Text = code;

        // 语言标签
        var langLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(language) ? "code" : language,
            FontFamily = s_monoFont,
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 复制按钮
        var copyBtn = new Button
        {
            Content = "复制",
            Padding = new Thickness(8, 3),
            FontSize = 11,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = code,
            Opacity = 0.7,
        };
        copyBtn.Click += CopyCodeBlock_Click;

        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetColumn(langLabel, 0);
        Grid.SetColumn(copyBtn, 1);
        headerGrid.Children.Add(langLabel);
        headerGrid.Children.Add(copyBtn);

        // 代码区 + header 之间加分隔线
        var separator = new Rectangle
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Opacity = 0.1,
            Margin = new Thickness(-12, 0, -12, 4),
        };
        separator[!Shape.FillProperty] = new DynamicResourceExtension("TextPrimaryBrush");

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(separator);
        stack.Children.Add(codeStb);

        var border = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = MaxContentWidth,
        };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension("CodeBlockBackgroundBrush");

        return border;
    }

    // ── 列表 ─────────────────────────────────────────────

    private Control RenderList(ListBlock list, int depth)
    {
        var stack = new StackPanel { Spacing = 3 };
        int orderNum = 1;
        if (list.IsOrdered && !string.IsNullOrEmpty(list.OrderedStart))
            int.TryParse(list.OrderedStart, out orderNum);

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var prefix = list.IsOrdered
                ? $"{orderNum++}. "
                : "•  ";

            bool first = true;
            foreach (var child in item)
            {
                if (child is ParagraphBlock para)
                {
                    var stb = CreateBaseTextBlock();
                    stb.Margin = new Thickness(depth * 16, 1, 0, 1);
                    if (first)
                    {
                        var bullet = new Run(prefix);
                        if (!list.IsOrdered)
                            bullet.Foreground = Brushes.Gray;
                        stb.Inlines!.Add(bullet);
                        first = false;
                    }
                    if (para.Inline != null)
                        RenderInlines(stb.Inlines!, para.Inline);
                    stack.Children.Add(stb);
                }
                else if (child is ListBlock nested)
                {
                    var nestedCtrl = RenderList(nested, depth + 1);
                    stack.Children.Add(nestedCtrl);
                }
                else
                {
                    stack.Children.Add(RenderBlock(child));
                }
            }
        }
        return stack;
    }

    // ── 引用 ─────────────────────────────────────────────

    private Control RenderQuote(QuoteBlock quote)
    {
        var inner = new StackPanel { Spacing = 4 };
        foreach (var child in quote)
        {
            var ctrl = RenderBlock(child);
            // 引用内文字用稍弱的颜色
            if (ctrl is SelectableTextBlock stb)
            {
                stb[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");
                stb.FontStyle = FontStyle.Italic;
            }
            inner.Children.Add(ctrl);
        }

        var border = new Border
        {
            Child = inner,
            Padding = new Thickness(14, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = MaxContentWidth,
            CornerRadius = new CornerRadius(0, 4, 4, 0),
        };
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension("PrimaryBrush");
        border.BorderThickness = new Thickness(3, 0, 0, 0);
        border[!Border.BackgroundProperty] = new DynamicResourceExtension("CodeBlockBackgroundBrush");

        return border;
    }

    // ── 表格 ─────────────────────────────────────────────

    private Control RenderTable(Table table)
    {
        var grid = new Grid { MaxWidth = MaxContentWidth };
        int colCount = table.OfType<TableRow>().FirstOrDefault()?.Count ?? 0;
        if (colCount == 0) return new Border();

        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        int rowIdx = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int colIdx = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                if (colIdx >= colCount) break;

                var stb = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = s_bodyFont,
                    FontSize = 13,
                    LineHeight = 20,
                    Padding = new Thickness(10, 6),
                    FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal,
                    ContextFlyout = null,
                };
                foreach (var para in cell.OfType<ParagraphBlock>())
                    if (para.Inline != null) RenderInlines(stb.Inlines!, para.Inline);

                // 对齐
                if (table.ColumnDefinitions != null && colIdx < table.ColumnDefinitions.Count)
                {
                    stb.TextAlignment = table.ColumnDefinitions[colIdx].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    };
                }

                var cellBorder = new Border
                {
                    Child = stb,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                };
                cellBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("BorderSubtleBrush");

                if (row.IsHeader)
                    cellBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("CodeBlockBackgroundBrush");
                else if (rowIdx % 2 == 0)
                {
                    // 偶数行淡色交替
                    cellBorder.Opacity = 0.95;
                }

                Grid.SetRow(cellBorder, rowIdx);
                Grid.SetColumn(cellBorder, colIdx);
                grid.Children.Add(cellBorder);
                colIdx++;
            }
            rowIdx++;
        }

        return new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 6, 0, 6),
            BorderThickness = new Thickness(1),
        };
    }

    // ── 分隔线 ───────────────────────────────────────────

    private Control RenderHorizontalRule()
    {
        var rect = new Rectangle
        {
            Height = 1,
            MaxWidth = MaxContentWidth,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Opacity = 0.2,
        };
        rect[!Shape.FillProperty] = new DynamicResourceExtension("TextPrimaryBrush");
        return rect;
    }

    // ── 降级渲染 ─────────────────────────────────────────

    private Control RenderFallback(Block block)
    {
        var stb = CreateBaseTextBlock();
        stb.Text = ExtractSource(Markdown ?? "", block);
        return stb;
    }

    // ── 代码块复制 ───────────────────────────────────────

    private static async void CopyCodeBlock_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var text = btn.Tag as string;
        if (string.IsNullOrEmpty(text)) return;

        var topLevel = TopLevel.GetTopLevel(btn);
        if (topLevel?.Clipboard == null) return;
        await topLevel.Clipboard.SetTextAsync(text);

        var original = btn.Content;
        btn.Content = "✓ 已复制";
        await System.Threading.Tasks.Task.Delay(1500);
        btn.Content = original;
    }

    // ── 内联渲染（Markdig AST） ──────────────────────────

    private void RenderInlines(InlineCollection target, MdInline? inline)
    {
        while (inline != null)
        {
            switch (inline)
            {
                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ ") { FontSize = 15 });
                    break;

                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterChar is '*' or '_')
                    {
                        if (emphasis.DelimiterCount >= 2)
                            span.FontWeight = FontWeight.SemiBold;
                        if (emphasis.DelimiterCount is 1 or >= 3)
                            span.FontStyle = FontStyle.Italic;
                    }
                    else if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 2)
                    {
                        span.TextDecorations = TextDecorations.Strikethrough;
                    }
                    RenderInlines(span.Inlines, emphasis.FirstChild);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                {
                    // 行内代码 pill 样式
                    var run = new Run(code.Content)
                    {
                        FontFamily = s_monoFont,
                        FontSize = 12.5,
                    };
                    var span = new Span();
                    span.Inlines.Add(new Run("\u2009")); // thin space padding
                    span.Inlines.Add(run);
                    span.Inlines.Add(new Run("\u2009"));
                    span.FontFamily = s_monoFont;
                    span[!Span.ForegroundProperty] = new DynamicResourceExtension("CodeInlineForegroundBrush");
                    target.Add(span);
                    break;
                }

                case LinkInline link:
                {
                    var span = new Span
                    {
                        Foreground = s_linkFg,
                        TextDecorations = TextDecorations.Underline,
                    };
                    if (link.FirstChild != null)
                        RenderInlines(span.Inlines, link.FirstChild);
                    else
                        span.Inlines.Add(new Run(link.Url ?? ""));
                    target.Add(span);
                    break;
                }

                case AutolinkInline autolink:
                    target.Add(new Span
                    {
                        Foreground = s_linkFg,
                        Inlines = { new Run(autolink.Url) },
                    });
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;

                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;

                case ContainerInline container:
                    RenderInlines(target, container.FirstChild);
                    break;
            }

            inline = inline.NextSibling;
        }
    }
}

// ══════════════════════════════════════════════════════════
//  轻量代码语法高亮器 —— 关键字着色，无外部依赖
// ══════════════════════════════════════════════════════════

internal static class CodeHighlighter
{
    // ── 颜色 token（VS Code Dark+ 风格，亮暗主题通用性好） ──
    private static readonly IBrush KeywordBrush   = Brush("#569CD6"); // blue
    private static readonly IBrush StringBrush    = Brush("#CE9178"); // orange
    private static readonly IBrush CommentBrush   = Brush("#6A9955"); // green
    private static readonly IBrush NumberBrush    = Brush("#B5CEA8"); // light green
    private static readonly IBrush TypeBrush      = Brush("#4EC9B0"); // teal
    private static readonly IBrush FuncBrush      = Brush("#DCDCAA"); // yellow
    private static readonly IBrush OperatorBrush  = Brush("#D4D4D4"); // light gray
    private static readonly IBrush PuncBrush      = Brush("#808080"); // gray
    private static readonly IBrush PropKeyBrush   = Brush("#9CDCFE"); // light blue (JSON keys)
    private static readonly IBrush BoolNullBrush  = Brush("#569CD6"); // same as keyword
    private static readonly IBrush TagBrush       = Brush("#569CD6"); // XML tags
    private static readonly IBrush AttrNameBrush  = Brush("#9CDCFE"); // XML attr name
    private static readonly IBrush AttrValueBrush = Brush("#CE9178"); // XML attr value
    private static readonly IBrush DecoratorBrush = Brush("#DCDCAA"); // decorators/attributes

    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Courier New, monospace");

    // ── 语言支持检查 ─────────────────────────────────────

    private static readonly HashSet<string> s_supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "py", "csharp", "cs", "c#",
        "javascript", "js", "typescript", "ts",
        "json", "jsonc",
        "xml", "html", "xaml", "axaml", "svg",
        "bash", "sh", "shell", "powershell", "ps1", "pwsh",
        "sql",
        "css", "scss",
        "yaml", "yml",
        "rust", "rs",
        "go", "golang",
        "java", "kotlin", "kt",
        "cpp", "c++", "c", "h",
    };

    public static bool CanHighlight(string lang) => s_supported.Contains(lang);

    // ── 主入口 ───────────────────────────────────────────

    public static void Highlight(InlineCollection inlines, string code, string lang)
    {
        var tokens = Tokenize(code, lang);
        foreach (var (text, brush) in tokens)
        {
            var run = new Run(text) { FontFamily = Mono, FontSize = 13 };
            if (brush != null) run.Foreground = brush;
            inlines.Add(run);
        }
    }

    // ── 分词 ─────────────────────────────────────────────

    private static List<(string Text, IBrush? Brush)> Tokenize(string code, string lang)
    {
        if (IsJsonLang(lang)) return TokenizeJson(code);
        if (IsXmlLang(lang)) return TokenizeXml(code);

        var keywords = GetKeywords(lang);
        var types = GetBuiltinTypes(lang);
        var lineComment = GetLineComment(lang);
        var blockStart = GetBlockCommentStart(lang);
        var blockEnd = GetBlockCommentEnd(lang);
        var hasDecorators = lang is "python" or "py" or "csharp" or "cs" or "c#" or "java" or "kotlin" or "kt" or "typescript" or "ts";

        var result = new List<(string, IBrush?)>();
        int i = 0;

        while (i < code.Length)
        {
            // Block comment
            if (blockStart != null && code.AsSpan(i).StartsWith(blockStart))
            {
                int end = code.IndexOf(blockEnd!, i + blockStart.Length, StringComparison.Ordinal);
                if (end < 0) end = code.Length - blockEnd!.Length;
                end += blockEnd!.Length;
                result.Add((code[i..end], CommentBrush));
                i = end;
                continue;
            }

            // Line comment
            if (lineComment != null && code.AsSpan(i).StartsWith(lineComment))
            {
                int end = code.IndexOf('\n', i);
                if (end < 0) end = code.Length;
                result.Add((code[i..end], CommentBrush));
                i = end;
                continue;
            }

            // String (double or single quote, with escape support)
            if (code[i] is '"' or '\'')
            {
                int end = ScanString(code, i);
                result.Add((code[i..end], StringBrush));
                i = end;
                continue;
            }

            // Backtick template string (JS/TS)
            if (code[i] == '`' && lang is "javascript" or "js" or "typescript" or "ts")
            {
                int end = code.IndexOf('`', i + 1);
                if (end < 0) end = code.Length - 1;
                end++;
                result.Add((code[i..end], StringBrush));
                i = end;
                continue;
            }

            // Number
            if (char.IsDigit(code[i]) || (code[i] == '.' && i + 1 < code.Length && char.IsDigit(code[i + 1])))
            {
                int end = i;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] is '.' or 'x' or 'X' or '_'))
                    end++;
                result.Add((code[i..end], NumberBrush));
                i = end;
                continue;
            }

            // Decorator (@xxx or [xxx])
            if (hasDecorators && code[i] == '@' && i + 1 < code.Length && char.IsLetter(code[i + 1]))
            {
                int end = i + 1;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '.'))
                    end++;
                result.Add((code[i..end], DecoratorBrush));
                i = end;
                continue;
            }

            // Word (identifier / keyword)
            if (char.IsLetter(code[i]) || code[i] == '_')
            {
                int end = i;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_'))
                    end++;
                var word = code[i..end];

                IBrush? brush = null;
                if (keywords.Contains(word))
                    brush = KeywordBrush;
                else if (types.Contains(word))
                    brush = TypeBrush;
                else if (word is "true" or "false" or "True" or "False" or "null" or "None" or "nil")
                    brush = BoolNullBrush;
                else if (end < code.Length && code[end] == '(')
                    brush = FuncBrush;
                else if (word.Length > 1 && char.IsUpper(word[0]))
                    brush = TypeBrush; // PascalCase → likely type

                result.Add((word, brush));
                i = end;
                continue;
            }

            // Operators & punctuation
            if (code[i] is '=' or '+' or '-' or '*' or '/' or '%' or '!' or '<' or '>' or '&' or '|' or '^' or '~' or '?')
            {
                result.Add((code[i].ToString(), OperatorBrush));
                i++;
                continue;
            }

            if (code[i] is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',' or ':' or '.')
            {
                result.Add((code[i].ToString(), PuncBrush));
                i++;
                continue;
            }

            // Whitespace / other
            result.Add((code[i].ToString(), null));
            i++;
        }

        return result;
    }

    // ── JSON 分词 ────────────────────────────────────────

    private static List<(string, IBrush?)> TokenizeJson(string code)
    {
        var result = new List<(string, IBrush?)>();
        // Simple line-comment aware JSON (jsonc)
        var regex = new Regex(
            @"(//[^\n]*)|" +                             // line comment
            @"(""(?:[^""\\]|\\.)*"")\s*(:)|" +           // key: "xxx":
            @"(""(?:[^""\\]|\\.)*"")|" +                 // string value
            @"(\b(?:true|false|null)\b)|" +              // bool/null
            @"(-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)|" + // number
            @"([{}\[\]:,])",                             // punctuation
            RegexOptions.Compiled);

        int last = 0;
        foreach (Match m in regex.Matches(code))
        {
            if (m.Index > last) result.Add((code[last..m.Index], null));

            if (m.Groups[1].Success) result.Add((m.Value, CommentBrush));
            else if (m.Groups[2].Success)
            {
                result.Add((m.Groups[2].Value, PropKeyBrush));
                result.Add((":", PuncBrush));
                // skip trailing whitespace between key and ':'
                int colonPos = code.IndexOf(':', m.Groups[2].Index + m.Groups[2].Length);
                last = colonPos + 1;
                continue;
            }
            else if (m.Groups[4].Success) result.Add((m.Value, StringBrush));
            else if (m.Groups[5].Success) result.Add((m.Value, BoolNullBrush));
            else if (m.Groups[6].Success) result.Add((m.Value, NumberBrush));
            else if (m.Groups[7].Success) result.Add((m.Value, PuncBrush));
            else result.Add((m.Value, null));

            last = m.Index + m.Length;
        }
        if (last < code.Length) result.Add((code[last..], null));
        return result;
    }

    // ── XML/HTML 分词 ────────────────────────────────────

    private static List<(string, IBrush?)> TokenizeXml(string code)
    {
        var result = new List<(string, IBrush?)>();
        var regex = new Regex(
            @"(<!--[\s\S]*?-->)|" +                        // comment
            @"(</?)([\w:.-]+)|" +                          // tag open: <tag or </tag
            @"([\w:.-]+)\s*(=)\s*(""[^""]*""|'[^']*')|" + // attr="val"
            @"(/?>)|" +                                    // close bracket
            @"(""[^""]*""|'[^']*')",                       // standalone strings
            RegexOptions.Compiled);

        int last = 0;
        foreach (Match m in regex.Matches(code))
        {
            if (m.Index > last) result.Add((code[last..m.Index], null));

            if (m.Groups[1].Success) result.Add((m.Value, CommentBrush));
            else if (m.Groups[2].Success)
            {
                result.Add((m.Groups[2].Value, PuncBrush));   // < or </
                result.Add((m.Groups[3].Value, TagBrush));     // tag name
            }
            else if (m.Groups[4].Success)
            {
                result.Add((m.Groups[4].Value, AttrNameBrush));
                result.Add((m.Groups[5].Value, PuncBrush));    // =
                result.Add((m.Groups[6].Value, AttrValueBrush));
            }
            else if (m.Groups[7].Success) result.Add((m.Value, PuncBrush));
            else if (m.Groups[8].Success) result.Add((m.Value, StringBrush));
            else result.Add((m.Value, null));

            last = m.Index + m.Length;
        }
        if (last < code.Length) result.Add((code[last..], null));
        return result;
    }

    // ── 字符串扫描 ───────────────────────────────────────

    private static int ScanString(string code, int start)
    {
        char quote = code[start];
        int i = start + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\') { i += 2; continue; }
            if (code[i] == quote) { i++; break; }
            if (code[i] == '\n') break; // single-line strings
            i++;
        }
        return i;
    }

    // ── 语言分类辅助 ────────────────────────────────────

    private static bool IsJsonLang(string lang) => lang is "json" or "jsonc";
    private static bool IsXmlLang(string lang) => lang is "xml" or "html" or "xaml" or "axaml" or "svg";

    private static string? GetLineComment(string lang) => lang switch
    {
        "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "pwsh" or "yaml" or "yml" => "#",
        "sql" => "--",
        _ => "//",
    };

    private static string? GetBlockCommentStart(string lang) => lang switch
    {
        "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "pwsh" or "yaml" or "yml" => null,
        "html" or "xml" or "xaml" or "axaml" or "svg" => "<!--",
        _ => "/*",
    };

    private static string? GetBlockCommentEnd(string lang) => lang switch
    {
        "html" or "xml" or "xaml" or "axaml" or "svg" => "-->",
        "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "pwsh" or "yaml" or "yml" => null,
        _ => "*/",
    };

    private static HashSet<string> GetKeywords(string lang) => lang switch
    {
        "python" or "py" => s_pythonKw,
        "csharp" or "cs" or "c#" => s_csharpKw,
        "javascript" or "js" => s_jsKw,
        "typescript" or "ts" => s_tsKw,
        "bash" or "sh" or "shell" => s_bashKw,
        "powershell" or "ps1" or "pwsh" => s_pwshKw,
        "sql" => s_sqlKw,
        "css" or "scss" => s_cssKw,
        "rust" or "rs" => s_rustKw,
        "go" or "golang" => s_goKw,
        "java" => s_javaKw,
        "kotlin" or "kt" => s_kotlinKw,
        "cpp" or "c++" or "c" or "h" => s_cppKw,
        _ => new(),
    };

    private static HashSet<string> GetBuiltinTypes(string lang) => lang switch
    {
        "csharp" or "cs" or "c#" => s_csharpTypes,
        "typescript" or "ts" => s_tsTypes,
        "java" => s_javaTypes,
        "rust" or "rs" => s_rustTypes,
        "go" or "golang" => s_goTypes,
        "cpp" or "c++" or "c" or "h" => s_cppTypes,
        "python" or "py" => s_pythonTypes,
        _ => new(),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    // ── 关键字集合 ───────────────────────────────────────

    private static readonly HashSet<string> s_pythonKw =
    [ "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield" ];

    private static readonly HashSet<string> s_pythonTypes =
    [ "int", "float", "str", "bool", "list", "dict", "tuple", "set", "bytes", "object", "type", "range", "print", "len", "enumerate", "zip", "map", "filter", "sorted", "reversed", "isinstance", "super", "property", "classmethod", "staticmethod" ];

    private static readonly HashSet<string> s_csharpKw =
    [ "abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "finally", "fixed", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "interface", "internal", "is", "lock", "namespace", "new", "operator", "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "required", "return", "sealed", "set", "sizeof", "static", "struct", "switch", "this", "throw", "try", "typeof", "unchecked", "unsafe", "using", "value", "var", "virtual", "void", "volatile", "when", "where", "while", "yield" ];

    private static readonly HashSet<string> s_csharpTypes =
    [ "bool", "byte", "char", "decimal", "double", "float", "int", "long", "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "dynamic", "Task", "List", "Dictionary", "HashSet", "IEnumerable", "Action", "Func", "Span", "Memory" ];

    private static readonly HashSet<string> s_jsKw =
    [ "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "export", "extends", "finally", "for", "from", "function", "if", "import", "in", "instanceof", "let", "new", "of", "return", "static", "super", "switch", "this", "throw", "try", "typeof", "var", "void", "while", "with", "yield" ];

    private static readonly HashSet<string> s_tsKw =
    [ "abstract", "any", "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "declare", "default", "delete", "do", "else", "enum", "export", "extends", "finally", "for", "from", "function", "get", "if", "implements", "import", "in", "instanceof", "interface", "is", "keyof", "let", "module", "namespace", "new", "of", "override", "readonly", "return", "set", "static", "super", "switch", "this", "throw", "try", "type", "typeof", "var", "void", "while", "with", "yield" ];

    private static readonly HashSet<string> s_tsTypes =
    [ "string", "number", "boolean", "object", "symbol", "bigint", "unknown", "never", "void", "undefined", "null", "Array", "Promise", "Record", "Partial", "Required", "Readonly", "Pick", "Omit", "Map", "Set" ];

    private static readonly HashSet<string> s_bashKw =
    [ "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "in", "function", "return", "local", "export", "source", "echo", "exit", "set", "unset", "read", "shift", "trap" ];

    private static readonly HashSet<string> s_pwshKw =
    [ "Begin", "Break", "Catch", "Class", "Continue", "Data", "Define", "Do", "DynamicParam", "Else", "ElseIf", "End", "Exit", "Filter", "Finally", "For", "ForEach", "From", "Function", "If", "In", "Param", "Process", "Return", "Switch", "Throw", "Trap", "Try", "Until", "Using", "While", "Workflow" ];

    private static readonly HashSet<string> s_sqlKw =
    [ "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "TABLE", "ALTER", "DROP", "INDEX", "ON", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "ALL", "DISTINCT", "AS", "IS", "NULL", "LIKE", "IN", "BETWEEN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "COUNT", "SUM", "AVG", "MIN", "MAX", "CAST", "COALESCE", "NULLIF",
      "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter", "drop", "index", "on", "join", "left", "right", "inner", "outer", "cross", "full", "group", "by", "order", "having", "limit", "offset", "union", "all", "distinct", "as", "is", "null", "like", "in", "between", "exists", "case", "when", "then", "else", "end", "count", "sum", "avg", "min", "max", "cast", "coalesce", "nullif" ];

    private static readonly HashSet<string> s_cssKw =
    [ "import", "media", "keyframes", "font-face", "supports", "charset", "namespace", "page" ];

    private static readonly HashSet<string> s_rustKw =
    [ "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "type", "unsafe", "use", "where", "while", "yield" ];

    private static readonly HashSet<string> s_rustTypes =
    [ "i8", "i16", "i32", "i64", "i128", "isize", "u8", "u16", "u32", "u64", "u128", "usize", "f32", "f64", "bool", "char", "str", "String", "Vec", "Box", "Option", "Result", "HashMap", "HashSet", "Arc", "Rc", "Mutex" ];

    private static readonly HashSet<string> s_goKw =
    [ "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var" ];

    private static readonly HashSet<string> s_goTypes =
    [ "bool", "byte", "complex64", "complex128", "error", "float32", "float64", "int", "int8", "int16", "int32", "int64", "rune", "string", "uint", "uint8", "uint16", "uint32", "uint64", "uintptr" ];

    private static readonly HashSet<string> s_javaKw =
    [ "abstract", "assert", "break", "case", "catch", "class", "const", "continue", "default", "do", "else", "enum", "extends", "final", "finally", "for", "goto", "if", "implements", "import", "instanceof", "interface", "native", "new", "package", "private", "protected", "public", "return", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while", "yield", "sealed", "permits", "record", "var" ];

    private static readonly HashSet<string> s_javaTypes =
    [ "boolean", "byte", "char", "double", "float", "int", "long", "short", "String", "Object", "Integer", "Long", "Double", "Float", "Boolean", "Character", "Byte", "Short", "List", "Map", "Set", "ArrayList", "HashMap", "HashSet", "Optional" ];

    private static readonly HashSet<string> s_kotlinKw =
    [ "abstract", "actual", "annotation", "as", "break", "by", "catch", "class", "companion", "const", "constructor", "continue", "crossinline", "data", "do", "else", "enum", "expect", "external", "final", "finally", "for", "fun", "get", "if", "import", "in", "infix", "init", "inline", "inner", "interface", "internal", "is", "it", "lateinit", "noinline", "object", "open", "operator", "out", "override", "package", "private", "protected", "public", "reified", "return", "sealed", "set", "super", "suspend", "this", "throw", "try", "typealias", "typeof", "val", "var", "vararg", "when", "where", "while" ];

    private static readonly HashSet<string> s_cppKw =
    [ "alignas", "alignof", "asm", "auto", "break", "case", "catch", "class", "const", "constexpr", "continue", "co_await", "co_return", "co_yield", "decltype", "default", "delete", "do", "else", "enum", "explicit", "export", "extern", "for", "friend", "goto", "if", "inline", "mutable", "namespace", "new", "noexcept", "operator", "private", "protected", "public", "register", "return", "sizeof", "static", "static_assert", "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "try", "typedef", "typeid", "typename", "union", "using", "virtual", "volatile", "while",
      "#include", "#define", "#ifdef", "#ifndef", "#endif", "#pragma", "#if", "#else", "#elif" ];

    private static readonly HashSet<string> s_cppTypes =
    [ "void", "bool", "char", "wchar_t", "char8_t", "char16_t", "char32_t", "short", "int", "long", "float", "double", "signed", "unsigned", "size_t", "ptrdiff_t", "nullptr_t", "string", "vector", "map", "set", "array", "unique_ptr", "shared_ptr", "optional", "variant", "tuple", "pair" ];
}
