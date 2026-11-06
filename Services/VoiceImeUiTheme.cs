using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace TrueFluentPro.Services;

/// <summary>三个独立输入法窗口的实际资源依赖；不加载 App.axaml、主主题、主 DI 或业务配置。</summary>
internal static class VoiceImeUiTheme
{
    internal static void Apply(Application app)
    {
        app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = Palette("#FFFFFF", "#F8FAFC", "#E2E8F0", "#888888");
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette("#1D242D", "#222B35", "#313A45", "#9CA3AF");
        // VoiceImeSection 的唯一 StaticResource；ControlTheme 不能用普通 Style 代替。
        app.Resources["SubtitleTextBlockStyle"] = new ControlTheme(typeof(TextBlock))
        {
            Setters = { new Setter(TextBlock.FontSizeProperty, 20d), new Setter(TextBlock.FontWeightProperty, FontWeight.SemiBold) }
        };
        app.Styles.Add(new Style(s => s.OfType<Border>().Class("app-card"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("CardBackgroundBrush")) }
        });
        app.Styles.Add(new Style(s => s.OfType<Border>().Class("sub-card"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SubCardBackgroundBrush")) }
        });
        foreach (var (name, size) in new[] { ("field-note", 11d), ("hint-text", 10d) })
            app.Styles.Add(new Style(s => s.OfType<TextBlock>().Class(name))
            {
                Setters =
                {
                    new Setter(TextBlock.FontSizeProperty, size),
                    new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                    new Setter(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextSubtleBrush"))
                }
            });
    }

    private static ResourceDictionary Palette(string card, string sub, string border, string text) => new()
    {
        ["CardBackgroundBrush"] = new SolidColorBrush(Color.Parse(card)),
        ["SubCardBackgroundBrush"] = new SolidColorBrush(Color.Parse(sub)),
        ["CardStrokeColorDefaultBrush"] = new SolidColorBrush(Color.Parse(border)),
        ["TextSubtleBrush"] = new SolidColorBrush(Color.Parse(text))
    };
}