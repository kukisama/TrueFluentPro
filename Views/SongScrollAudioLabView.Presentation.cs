using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

/// <summary>原 Markdown 控件内部正文为 14；局部布局缩放到长卷的 28，不改共享主题。</summary>
public sealed class SongScrollAudioLabViewMarkdown : LayoutTransformControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<SongScrollAudioLabViewMarkdown, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public SongScrollAudioLabViewMarkdown()
    {
        LayoutTransform = new ScaleTransform(2, 2);
        // 仅覆盖此原子控件消费的 Markdown 资源，不触碰应用或 Shell 字典。
        Resources["TextMutedBrush"] = new SolidColorBrush(Color.Parse("#74736A"));
        Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.Parse("#27332F"));
        Resources["CodeBlockBackgroundBrush"] = new SolidColorBrush(Color.Parse("#F3EEE3"));
        Resources["CodeInlineForegroundBrush"] = new SolidColorBrush(Color.Parse("#4D6059"));
        Resources["BorderSubtleBrush"] = new SolidColorBrush(Color.Parse("#CDBFA4"));
        Resources["PrimaryBrush"] = new SolidColorBrush(Color.Parse("#537674"));
        var renderer = new ChatMarkdownBlock();
        renderer[!TextElement.ForegroundProperty] = new DynamicResourceExtension("ScrollInk");
        renderer.Bind(ChatMarkdownBlock.MarkdownProperty, new Binding(nameof(Markdown)) { Source = this });
        Child = renderer;
    }
}

/// <summary>保留原导图缩放/平移/复位手势，将节点文字从 13/15 放大为 26/30。</summary>
public sealed class SongScrollAudioLabViewMindMap : LayoutTransformControl
{
    public static readonly StyledProperty<MindMapNode?> RootProperty =
        AvaloniaProperty.Register<SongScrollAudioLabViewMindMap, MindMapNode?>(nameof(Root));

    public MindMapNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public SongScrollAudioLabViewMindMap()
    {
        LayoutTransform = new ScaleTransform(2, 2);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        var canvas = new MindMapCanvas();
        canvas.Bind(MindMapCanvas.RootProperty, new Binding(nameof(Root)) { Source = this });
        Child = canvas;
    }
}

public sealed class SongScrollAudioLabViewCustomTabConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count == 3 && values[0] is AudioLabTabKind.Custom
            && values[1] is string current && values[2] is string stage && current == stage;
}

public sealed class SongScrollAudioLabViewStageStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool complete ? (complete ? "已完成" : "待完成")
            : value?.ToString() switch { "Ready" => "已完成", "Processing" => "处理中", _ => "待处理" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}