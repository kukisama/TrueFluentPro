using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;
using IOPath = System.IO.Path;

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

    public static readonly StyledProperty<bool> ShowCodeLineNumbersProperty =
        AvaloniaProperty.Register<MarkdownRenderer, bool>(nameof(ShowCodeLineNumbers), false);

    public static readonly StyledProperty<IReadOnlyDictionary<int, string>?> CitationUrlsProperty =
        AvaloniaProperty.Register<MarkdownRenderer, IReadOnlyDictionary<int, string>?>(nameof(CitationUrls));

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

    /// <summary>是否在代码块中显示行号</summary>
    public bool ShowCodeLineNumbers
    {
        get => GetValue(ShowCodeLineNumbersProperty);
        set => SetValue(ShowCodeLineNumbersProperty, value);
    }

    /// <summary>引用编号 → URL 映射，用于 [N] 角标点击跳转</summary>
    public IReadOnlyDictionary<int, string>? CitationUrls
    {
        get => GetValue(CitationUrlsProperty);
        set => SetValue(CitationUrlsProperty, value);
    }

    // ── 解析管线 ─────────────────────────────────────────

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseMathematics()
        .Build();

    /// <summary>
    /// 预处理：将 [N]（N=1~99）转义为 \[N\]，防止 Markdig 把相邻的 [2][3] 解析为引用链接。
    /// 转义后变成 LiteralInline，由 RenderLiteralWithCitations() 正则统一处理。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_citationEscape =
        new(@"(?<!\\)\[(\d{1,2})\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string EscapeCitationBrackets(string md)
        => s_citationEscape.Replace(md, @"\[$1\]");

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

        md = EscapeCitationBrackets(md);
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
        MathBlock mb => RenderMathBlock(mb),
        FencedCodeBlock fc => RenderFencedCodeBlock(fc),
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
        // 检查段落是否包含图片（Markdown 图片语法 ![alt](url)）
        if (para.Inline != null && ContainsImage(para.Inline))
        {
            return RenderParagraphWithImages(para);
        }

        var stb = CreateBaseTextBlock();
        stb.Margin = new Thickness(0, 4, 0, 6);
        if (para.Inline != null)
            RenderInlines(stb.Inlines!, para.Inline);
        return stb;
    }

    /// <summary>检查内联序列中是否包含图片</summary>
    private static bool ContainsImage(MdInline? inline)
    {
        while (inline != null)
        {
            if (inline is LinkInline { IsImage: true })
                return true;
            inline = inline.NextSibling;
        }
        return false;
    }

    /// <summary>渲染包含图片的段落：图片独立显示，文本照常渲染</summary>
    private Control RenderParagraphWithImages(ParagraphBlock para)
    {
        var container = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 2) };

        var currentInline = para.Inline?.FirstChild;
        var textStb = CreateBaseTextBlock();
        bool hasText = false;

        while (currentInline != null)
        {
            if (currentInline is LinkInline imgLink && imgLink.IsImage)
            {
                // 先把积累的文本添加到容器
                if (hasText)
                {
                    container.Children.Add(textStb);
                    textStb = CreateBaseTextBlock();
                    hasText = false;
                }
                // 添加图片控件
                container.Children.Add(RenderMarkdownImage(imgLink));
            }
            else
            {
                // 渲染到当前文本块
                RenderInlines(textStb.Inlines!, currentInline);
                hasText = true;
                // 跳过 NextSibling 因为 RenderInlines 只处理单个 inline
                currentInline = currentInline.NextSibling;
                continue;
            }
            currentInline = currentInline.NextSibling;
        }

        // 剩余文本
        if (hasText)
            container.Children.Add(textStb);

        return container.Children.Count == 1 ? container.Children[0] : container;
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

    // ── Fenced 代码块分发 ───────────────────────────────

    private Control RenderFencedCodeBlock(FencedCodeBlock fc)
    {
        var code = fc.Lines.ToString().TrimEnd();
        var language = fc.Info?.Trim();
        var normalizedLang = (language ?? "").ToLowerInvariant().Trim();

        // Mermaid 特殊处理：显示源码 + "在浏览器中查看"按钮
        if (normalizedLang == "mermaid")
            return RenderMermaidBlock(code);

        return RenderCodeBlock(code, language);
    }

    // ── 代码块（语法高亮 + 行号） ────────────────────────

    private Control RenderCodeBlock(string code, string? language)
    {
        var normalizedLang = (language ?? "").ToLowerInvariant().Trim();

        // 代码内容区：根据是否启用行号决定布局
        Control codeContent;
        if (ShowCodeLineNumbers)
        {
            codeContent = BuildCodeWithLineNumbers(code, normalizedLang);
        }
        else
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
            if (CodeHighlighter.CanHighlight(normalizedLang))
                CodeHighlighter.Highlight(codeStb.Inlines!, code, normalizedLang);
            else
                codeStb.Text = code;
            codeContent = codeStb;
        }

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
        stack.Children.Add(codeContent);

        var border = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 8, 0, 8),
            MaxWidth = MaxContentWidth,
        };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeBlockBackgroundKey);
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension(MarkdownTheme.BorderSubtleKey);
        border.BorderThickness = new Thickness(1);

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
                    RenderLiteralWithCitations(target, literal.Content.ToString());
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

                case LinkInline link when link.IsImage:
                {
                    // Markdown 图片嵌入：![alt](url)
                    var altText = link.FirstChild is LiteralInline lit
                        ? lit.Content.ToString()
                        : (link.Url ?? "🖼");
                    var imgSpan = new Span
                    {
                        Foreground = MarkdownTheme.LinkForeground,
                    };
                    imgSpan.Inlines.Add(new Run($"🖼 {altText}"));
                    target.Add(imgSpan);
                    break;
                }

                case LinkInline link:
                {
                    var linkUrl = link.Url;
                    // 提取纯文本（链接内可能有嵌套格式，这里取纯文本即可）
                    var linkText = link.FirstChild is LiteralInline lit
                        ? lit.Content.ToString()
                        : (linkUrl ?? "");

                    var tb = new TextBlock
                    {
                        Text = linkText,
                        Foreground = MarkdownTheme.LinkForeground,
                        TextDecorations = TextDecorations.Underline,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    if (!string.IsNullOrEmpty(linkUrl))
                    {
                        tb.PointerPressed += (_, _) =>
                        {
                            try { Process.Start(new ProcessStartInfo(linkUrl) { UseShellExecute = true }); }
                            catch { }
                        };
                    }
                    target.Add(new InlineUIContainer { Child = tb });
                    break;
                }

                case AutolinkInline autolink:
                {
                    var autoUrl = autolink.Url;
                    var autoTb = new TextBlock
                    {
                        Text = autoUrl,
                        Foreground = MarkdownTheme.LinkForeground,
                        TextDecorations = TextDecorations.Underline,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    autoTb.PointerPressed += (_, _) =>
                    {
                        try { Process.Start(new ProcessStartInfo(autoUrl) { UseShellExecute = true }); }
                        catch { }
                    };
                    target.Add(new InlineUIContainer { Child = autoTb });
                    break;
                }

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;

                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;

                case MathInline math:
                {
                    // 行内 LaTeX 公式：$...$
                    var mathSpan = new Span
                    {
                        FontFamily = MarkdownTheme.MonoFont,
                        FontStyle = FontStyle.Italic,
                    };
                    mathSpan.Inlines.Add(new Run("\u2009"));
                    mathSpan.Inlines.Add(new Run(math.Content.ToString()));
                    mathSpan.Inlines.Add(new Run("\u2009"));
                    mathSpan[!Span.ForegroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeInlineForegroundKey);
                    target.Add(mathSpan);
                    break;
                }

                case ContainerInline container:
                    RenderInlines(target, container.FirstChild);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    // ── 引用角标渲染 ─────────────────────────────────

    /// <summary>
    /// 创建圆形可点击引用角标 Button（与来源面板风格一致）。
    /// 用 Button.Click（路由事件）代替 Border.PointerPressed，
    /// 避免被 SelectableTextBlock 拦截。
    /// </summary>
    private Button CreateCitationButton(int citNum)
    {
        var btn = new Button
        {
            Content = citNum.ToString(),
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 16,
            Height = 16,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(1, 0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        btn[!Button.BackgroundProperty] =
            new DynamicResourceExtension("AccentFillColorDefaultBrush");
        btn[!Button.ForegroundProperty] =
            new DynamicResourceExtension("TextOnAccentFillColorPrimaryBrush");

        btn.Click += (_, _) =>
        {
            var urls = CitationUrls;
            if (urls != null && urls.TryGetValue(citNum, out var url) && !string.IsNullOrEmpty(url))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            }
        };
        return btn;
    }

    /// <summary>渲染可点击的引用角标</summary>
    private void RenderCitationBadge(InlineCollection target, int citNum)
    {
        target.Add(new InlineUIContainer { Child = CreateCitationButton(citNum) });
    }

    /// <summary>匹配文本中的 [N] 引用标记（1-99）— 处理 Markdig 未解析为 LinkInline 的情况</summary>
    private static readonly System.Text.RegularExpressions.Regex s_citationRegex =
        new(@"\[(\d{1,2})\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 渲染文本，将 [N] 引用标记替换为可点击的彩色角标。
    /// 所有角标统一渲染为按钮样式，点击时动态查找 CitationUrls（解决绑定时序问题）。
    /// </summary>
    private void RenderLiteralWithCitations(InlineCollection target, string text)
    {
        var matches = s_citationRegex.Matches(text);
        if (matches.Count == 0)
        {
            target.Add(new Run(text));
            return;
        }

        int lastEnd = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            // 前缀文本
            if (m.Index > lastEnd)
                target.Add(new Run(text[lastEnd..m.Index]));

            int citNum = int.Parse(m.Groups[1].Value);
            target.Add(new InlineUIContainer { Child = CreateCitationButton(citNum) });

            lastEnd = m.Index + m.Length;
        }

        // 尾部文本
        if (lastEnd < text.Length)
            target.Add(new Run(text[lastEnd..]));
    }

    // ── LaTeX 块级公式 ───────────────────────────────────

    /// <summary>渲染块级 LaTeX 数学公式 $$...$$</summary>
    private Control RenderMathBlock(MathBlock mathBlock)
    {
        var formula = mathBlock.Lines.ToString().Trim();

        var stb = new SelectableTextBlock
        {
            Text = formula,
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = _theme.BodyFontSize + 1,
            FontStyle = FontStyle.Italic,
            LineHeight = _theme.BodyLineHeight + 2,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxContentWidth,
            Padding = new Thickness(16, 10),
            ContextFlyout = null,
        };

        var mathLabel = new TextBlock
        {
            Text = "LaTeX",
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = 10,
            Opacity = 0.45,
            Margin = new Thickness(12, 4, 0, 0),
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(mathLabel);
        stack.Children.Add(stb);

        var border = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = MaxContentWidth,
            BorderThickness = new Thickness(1),
        };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeBlockBackgroundKey);
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension(MarkdownTheme.BorderSubtleKey);

        return border;
    }

    // ── 代码行号 ─────────────────────────────────────────

    /// <summary>构建带行号的代码区域（Grid：左列行号 + 右列代码）</summary>
    private Control BuildCodeWithLineNumbers(string code, string normalizedLang)
    {
        var lines = code.Split('\n');
        var lineCount = lines.Length;
        var gutterWidth = lineCount >= 100 ? 42 : (lineCount >= 10 ? 32 : 24);

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse($"{gutterWidth},*"),
        };

        // 左侧行号列
        var lineNumStb = new SelectableTextBlock
        {
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = _theme.CodeFontSize,
            LineHeight = _theme.CodeLineHeight,
            TextAlignment = TextAlignment.Right,
            Opacity = 0.35,
            Padding = new Thickness(0, 0, 8, 0),
            ContextFlyout = null,
        };
        for (int i = 1; i <= lineCount; i++)
        {
            if (i > 1)
                lineNumStb.Inlines!.Add(new LineBreak());
            lineNumStb.Inlines!.Add(new Run(i.ToString()));
        }

        // 右侧代码列
        var codeStb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = _theme.CodeFontSize,
            LineHeight = _theme.CodeLineHeight,
            ContextFlyout = null,
            LetterSpacing = 0,
        };
        if (CodeHighlighter.CanHighlight(normalizedLang))
            CodeHighlighter.Highlight(codeStb.Inlines!, code, normalizedLang);
        else
            codeStb.Text = code;

        // 行号与代码之间加分隔线
        var gutterSep = new Rectangle
        {
            Width = 1,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0.12,
        };
        gutterSep[!Shape.FillProperty] = new DynamicResourceExtension(MarkdownTheme.TextPrimaryKey);

        var gutterPanel = new Panel();
        gutterPanel.Children.Add(lineNumStb);
        gutterPanel.Children.Add(gutterSep);

        Grid.SetColumn(gutterPanel, 0);
        Grid.SetColumn(codeStb, 1);
        grid.Children.Add(gutterPanel);
        grid.Children.Add(codeStb);

        return grid;
    }

    // ── Mermaid 图表 ─────────────────────────────────────

    /// <summary>渲染 Mermaid 代码块：显示源码 + "在浏览器中查看"按钮</summary>
    private Control RenderMermaidBlock(string mermaidCode)
    {
        var codeStb = new SelectableTextBlock
        {
            Text = mermaidCode,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = _theme.CodeFontSize,
            LineHeight = _theme.CodeLineHeight,
            ContextFlyout = null,
            Opacity = 0.8,
        };

        // Header：Mermaid 标签 + 复制按钮 + 浏览器预览按钮
        var langLabel = new TextBlock
        {
            Text = "mermaid",
            FontFamily = MarkdownTheme.MonoFont,
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copyBtn = new Button
        {
            Content = "复制",
            Padding = new Thickness(8, 3),
            FontSize = 11,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = mermaidCode,
            Opacity = 0.7,
        };
        copyBtn.Click += CopyCodeBlock_Click;

        var previewBtn = new Button
        {
            Content = "📊 在浏览器中查看",
            Padding = new Thickness(8, 3),
            FontSize = 11,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = mermaidCode,
            Opacity = 0.85,
        };
        previewBtn.Click += OpenMermaidInBrowser_Click;

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonPanel.Children.Add(copyBtn);
        buttonPanel.Children.Add(previewBtn);

        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetColumn(langLabel, 0);
        Grid.SetColumn(buttonPanel, 1);
        headerGrid.Children.Add(langLabel);
        headerGrid.Children.Add(buttonPanel);

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
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 8, 0, 8),
            MaxWidth = MaxContentWidth,
        };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension(MarkdownTheme.CodeBlockBackgroundKey);
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension(MarkdownTheme.BorderSubtleKey);
        border.BorderThickness = new Thickness(1);

        return border;
    }

    /// <summary>将 Mermaid 代码生成临时 HTML 并在浏览器中打开</summary>
    private static void OpenMermaidInBrowser_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var code = btn.Tag as string;
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            var encodedCode = System.Net.WebUtility.HtmlEncode(code);
            var html = "<!DOCTYPE html>\n<html><head>\n" +
                "<meta charset=\"utf-8\">\n" +
                "<title>Mermaid Diagram</title>\n" +
                "<script src=\"https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js\"></script>\n" +
                "<style>body { display:flex; justify-content:center; padding:40px; background:#1e1e1e; color:#d4d4d4; }</style>\n" +
                "</head><body>\n" +
                "<div class=\"mermaid\">\n" + encodedCode + "\n</div>\n" +
                "<script>mermaid.initialize({ startOnLoad:true, theme:'dark' });</script>\n" +
                "</body></html>";

            var tempPath = IOPath.Combine(IOPath.GetTempPath(), $"mermaid_{Guid.NewGuid():N}.html");
            System.IO.File.WriteAllText(tempPath, html);
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
        }
        catch
        {
            // 静默忽略浏览器打开失败
        }
    }

    // ── Markdown 图片嵌入 ────────────────────────────────

    /// <summary>渲染 Markdown 图片 ![alt](url) 为实际图片控件</summary>
    private Control RenderMarkdownImage(LinkInline imgLink)
    {
        var url = imgLink.Url;
        var altText = imgLink.FirstChild is LiteralInline lit
            ? lit.Content.ToString()
            : "";

        var container = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };

        // 图片控件（异步加载）
        var image = new Image
        {
            MaxWidth = Math.Min(MaxContentWidth, 600),
            MaxHeight = 400,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // 加载指示器
        var loadingText = new TextBlock
        {
            Text = "🖼 加载中...",
            FontSize = 12,
            Opacity = 0.5,
        };
        container.Children.Add(loadingText);

        // 异步加载图片
        if (!string.IsNullOrEmpty(url))
        {
            _ = LoadImageAsync(image, loadingText, container, url);
        }
        else
        {
            loadingText.Text = "⚠ 无效的图片链接";
        }

        // alt 文本
        if (!string.IsNullOrEmpty(altText))
        {
            var altBlock = new TextBlock
            {
                Text = altText,
                FontSize = 11,
                Opacity = 0.5,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MaxContentWidth,
            };
            container.Children.Add(altBlock);
        }

        return container;
    }

    /// <summary>异步加载图片（支持 HTTP URL 和本地路径）</summary>
    private static async Task LoadImageAsync(Image imageControl, TextBlock loadingText, StackPanel container, string url)
    {
        try
        {
            Bitmap? bitmap = null;

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var data = await client.GetByteArrayAsync(url);
                using var ms = new System.IO.MemoryStream(data);
                bitmap = new Bitmap(ms);
            }
            else if (System.IO.File.Exists(url))
            {
                bitmap = new Bitmap(url);
            }

            if (bitmap != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    imageControl.Source = bitmap;
                    // 替换加载指示器为实际图片
                    var idx = container.Children.IndexOf(loadingText);
                    if (idx >= 0)
                    {
                        container.Children[idx] = imageControl;
                    }
                    else
                    {
                        container.Children.Insert(0, imageControl);
                    }
                });
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    loadingText.Text = "⚠ 图片加载失败";
                });
            }
        }
        catch
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                loadingText.Text = "⚠ 图片加载失败";
            });
        }
    }
}
