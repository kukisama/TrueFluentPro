using Avalonia.Media;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// Markdown 渲染主题配置：集中管理字体、字号、行高、间距等排版参数。
/// 分为正常模式和 Reasoning（思考过程）模式两套参数。
/// </summary>
public sealed class MarkdownTheme
{
    // ── 字体 ─────────────────────────────────────────────
    public static readonly FontFamily BodyFont = new("Microsoft YaHei UI, Segoe UI, Inter, sans-serif");
    public static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New, monospace");

    // ── 固定色（主题无关） ───────────────────────────────
    public static readonly IBrush LinkForeground = new SolidColorBrush(Color.Parse("#0969DA"));

    // ── 正常模式排版参数 ─────────────────────────────────
    public double BodyFontSize { get; init; } = 14;
    public double BodyLineHeight { get; init; } = 24;
    public double LetterSpacing { get; init; } = 0.2;
    public double CodeFontSize { get; init; } = 13;
    public double CodeLineHeight { get; init; } = 20;
    public double BlockSpacing { get; init; } = 6;

    // ── Reasoning 模式排版参数 ───────────────────────────
    public double ReasoningFontSize { get; init; } = 12.5;
    public double ReasoningLineHeight { get; init; } = 20;

    // ── 标题字号映射 ─────────────────────────────────────
    public (double fontSize, double topMargin, double bottomMargin) GetHeadingMetrics(int level) => level switch
    {
        1 => (22.0, 12.0, 6.0),
        2 => (19.0, 10.0, 5.0),
        3 => (16.0, 8.0, 4.0),
        _ => (14.5, 6.0, 3.0),
    };

    // ── 动态资源 Key ─────────────────────────────────────
    public const string CodeBlockBackgroundKey = "CodeBlockBackgroundBrush";
    public const string CodeInlineForegroundKey = "CodeInlineForegroundBrush";
    public const string TextPrimaryKey = "TextPrimaryBrush";
    public const string TextMutedKey = "TextMutedBrush";
    public const string BorderSubtleKey = "BorderSubtleBrush";
    public const string PrimaryKey = "PrimaryBrush";

    // ── 预设实例 ─────────────────────────────────────────
    public static MarkdownTheme Default { get; } = new();
}
