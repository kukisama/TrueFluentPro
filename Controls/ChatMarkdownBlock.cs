using TrueFluentPro.Controls.Markdown;

namespace TrueFluentPro.Controls;

/// <summary>
/// 向后兼容的薄包装器：将旧的 ChatMarkdownBlock 委托给新的 Controls.Markdown.MarkdownRenderer。
/// 保留原有命名空间和类名，以免需要修改所有 XAML 引用。
/// </summary>
public class ChatMarkdownBlock : MarkdownRenderer
{
}
