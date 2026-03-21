using System;
using System.Collections.Generic;
using System.Linq;
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

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 高质量 Markdown 渲染控件，基于 Markdig AST + 增量渲染 + 语法高亮 + 跨块选择。
/// 
/// 相比旧版 ChatMarkdownBlock 的改进：
/// 1. 模块化：CodeHighlighter / MarkdownTheme / SelectionManager 各自独立文件
/// 2. 跨块选择 (P1)：SelectionManager 在 StackPanel 层拦截拖选事件，
///    实现类似浏览器的跨段落/跨代码块文本选择
/// 3. 字符级平滑流式 (P2)：SmoothStreamingAnimator 提供打字机动画缓冲
/// 4. 可复用：不仅用于聊天，也可替代 About/Help/Insight 等页面的 Markdown.Avalonia
/// </summary>
public class MarkdownRenderer : StackPanel
{
    // ── 依赖属性 ─────────────────────────────────────────

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownRenderer, string?>(nameof(Markdown));

    public static readonly StyledProperty<double> MaxContentWidthProperty =
        AvaloniaProperty.Register<MarkdownRenderer, double>(nameof(MaxContentWidth), double.PositiveInfinity);

    public static readonly StyledProperty<bool> IsReasoningProperty =
        AvaloniaProperty.Register<MarkdownRenderer, bool>(nameof(IsReasoning));

    public static readonly StyledProperty<bool> EnableCrossBlockSelectionProperty =
        AvaloniaProperty.Register<MarkdownRenderer, bool>(nameof(EnableCrossBlockSelection), true);

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

    /// <summary>是否启用跨块文本选择</summary>
    public bool EnableCrossBlockSelection
    {
        get => GetValue(EnableCrossBlockSelectionProperty);
        set => SetValue(EnableCrossBlockSelectionProperty, value);
    }

    // ── 解析管线 ─────────────────────────────────────────

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    // ── 主题 ─────────────────────────────────────────────

    private readonly MarkdownTheme _theme = MarkdownTheme.Default;

    // ── 增量渲染状态 ─────────────────────────────────────

    /// <summary>每个已渲染块的源文本，用于增量比较</summary>
    private readonly List<string> _blockSources = new();

    // ── 跨块选择 ─────────────────────────────────────────

    private SelectionManager? _selectionManager;

    /// <summary>获取跨块选中的文本（可用于复制）</summary>
    public string GetSelectedText() => _selectionManager?.GetSelectedText() ?? string.Empty;

    /// <summary>是否有跨块选择</summary>
    public bool HasCrossBlockSelection => _selectionManager?.HasCrossBlockSelection ?? false;

    /// <summary>清除所有选区</summary>
    public void ClearAllSelection() => _selectionManager?.ClearSelection();

    // ── 构造与生命周期 ───────────────────────────────────

    static MarkdownRenderer()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownRenderer>((x, _) => x.UpdateContent());
        MaxContentWidthProperty.Changed.AddClassHandler<MarkdownRenderer>((x, _) => x.ForceRerender());
        EnableCrossBlockSelectionProperty.Changed.AddClassHandler<MarkdownRenderer>((x, _) => x.UpdateSelectionManager());
    }

    public MarkdownRenderer()
    {
        Spacing = _theme.BlockSpacing;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSelectionManager();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _selectionManager?.Detach();
        _selectionManager = null;
    }

    private void UpdateSelectionManager()
    {
        if (EnableCrossBlockSelection && _selectionManager == null)
        {
            _selectionManager = new SelectionManager(this);
        }
        else if (!EnableCrossBlockSelection && _selectionManager != null)
        {
            _selectionManager.Detach();
            _selectionManager = null;
        }
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
            FontFamily = MarkdownTheme.BodyFont,
            FontSize = IsReasoning ? _theme.ReasoningFontSize : _theme.BodyFontSize,
            LineHeight = IsReasoning ? _theme.ReasoningLineHeight : _theme.BodyLineHeight,
            Padding = new Thickness(0, 1, 0, 1),
            LetterSpacing = _theme.LetterSpacing,
        };
        if (IsReasoning)
        {
            stb[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension(MarkdownTheme.TextMutedKey);
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
        var (fontSize, topMargin, bottomMargin) = _theme.GetHeadingMetrics(heading.Level);
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
            stack.Children[1][!Shape.FillProperty] = new DynamicResourceExtension(MarkdownTheme.TextPrimaryKey);
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
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = _theme.CodeFontSize,
            LineHeight = _theme.CodeLineHeight,
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
            FontFamily = MarkdownTheme.MonoFont,
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
        separator[!Shape.FillProperty] = new DynamicResourceExtension(MarkdownTheme.TextPrimaryKey);

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
        border[!Border.BackgroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeBlockBackgroundKey);

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
                stb[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension(MarkdownTheme.TextMutedKey);
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
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension(MarkdownTheme.PrimaryKey);
        border.BorderThickness = new Thickness(3, 0, 0, 0);
        border[!Border.BackgroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeBlockBackgroundKey);

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
                    FontFamily = MarkdownTheme.BodyFont,
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
                cellBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension(MarkdownTheme.BorderSubtleKey);

                if (row.IsHeader)
                    cellBorder[!Border.BackgroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeBlockBackgroundKey);
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
        rect[!Shape.FillProperty] = new DynamicResourceExtension(MarkdownTheme.TextPrimaryKey);
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
                        FontFamily = MarkdownTheme.MonoFont,
                        FontSize = 12.5,
                    };
                    var span = new Span();
                    span.Inlines.Add(new Run("\u2009")); // thin space padding
                    span.Inlines.Add(run);
                    span.Inlines.Add(new Run("\u2009"));
                    span.FontFamily = MarkdownTheme.MonoFont;
                    span[!Span.ForegroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeInlineForegroundKey);
                    target.Add(span);
                    break;
                }

                case LinkInline link:
                {
                    var span = new Span
                    {
                        Foreground = MarkdownTheme.LinkForeground,
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
                        Foreground = MarkdownTheme.LinkForeground,
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
