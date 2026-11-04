using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.ViewModels.Presentation;

namespace TrueFluentPro.Views;

/// <summary>
/// 独立书册页面，只拥有呈现适配器与本页 UI，不拥有翻译会话。
/// 外壳可以隐藏/卸载本页而继续业务，最终关闭时调用 DisposePresentation。
/// </summary>
public partial class InkStudyTranslationView : UserControl
{
    private readonly ScrollViewer _pageScroll;
    private readonly ScrollViewer _conversationScroll;
    private readonly Grid _canvas;
    private readonly List<Control> _flyoutContents = new();
    private ILiveTranslationPresentationSource? _source;
    private bool _followConversation = true;
    private bool _scrollQueued;

    public InkStudyTranslationView()
    {
        AvaloniaXamlLoader.Load(this);
        _pageScroll = this.FindControl<ScrollViewer>("InkPageScroll")!;
        _conversationScroll = this.FindControl<ScrollViewer>("InkConversationScroll")!;
        _canvas = this.FindControl<Grid>("InkCanvas")!;

        // Popup 不依赖外壳 DataContext；即使换源时弹层已打开，也同步到本页呈现对象。
        foreach (var name in new[]
                 {
                     "InkHistoryButton", "InkInsightOptionsButton", "InkDevicesButton",
                     "InkSubtitleButton", "InkMoreButton"
                 })
        {
            if (this.FindControl<Button>(name)?.Flyout is Flyout { Content: Control content })
                _flyoutContents.Add(content);
        }

        DataContextChanged += OnPresentationContextChanged;
        SizeChanged += (_, _) => UpdateCanvasSize();
        _pageScroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportDelta != default)
                UpdateCanvasSize();
        };
        _conversationScroll.ScrollChanged += OnConversationScrollChanged;
        AttachedToVisualTree += (_, _) =>
        {
            UpdateCanvasSize();
            QueueConversationFollow();
        };
    }

    public InkStudyPresentationViewModel? Presentation { get; private set; }

    public event Action<string>? NavigationRequested;

    /// <summary>把本页实际主题按钮交给外壳作为菜单的 PlacementTarget。</summary>
    public event Action<Control>? ThemeRequested;

    public void BindSession(ILiveTranslationPresentationSource? source)
    {
        if (ReferenceEquals(_source, source) && Presentation is not null)
        {
            DataContext = Presentation;
            return;
        }

        DisposePresentation();
        if (source is null)
            return;

        _source = source;
        Presentation = new InkStudyPresentationViewModel(source);
        _followConversation = true;
        DataContext = Presentation;
        QueueConversationFollow();
    }

    /// <summary>仅释放适配器的订阅；绝不 Dispose/Stop 其 Session 或任何业务子 VM。</summary>
    public void DisposePresentation()
    {
        var previous = Presentation;
        Presentation = null;
        _source = null;
        if (ReferenceEquals(DataContext, previous))
            DataContext = null;
        previous?.Dispose();
    }

    private void OnPresentationContextChanged(object? sender, EventArgs e)
    {
        foreach (var content in _flyoutContents)
            content.DataContext = DataContext;
    }

    private void OnNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            tag is "live" or "media" or "mediav2" or "audiolab" or "taskmonitor" or "settings")
            NavigationRequested?.Invoke(tag);
    }

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            ThemeRequested?.Invoke(button);
    }

    private void OnDisplayModeClick(object? sender, RoutedEventArgs e)
    {
        if (Presentation is not { } presentation || sender is not ToggleButton button || button.Tag is not string tag)
            return;

        // 重按当前段仍保留一种显示模式；不触碰经典页的显示模式/字号。
        switch (tag)
        {
            case "original": presentation.IsOriginalMode = true; break;
            case "translated": presentation.IsTranslatedMode = true; break;
            case "bilingual": presentation.IsBilingualMode = true; break;
        }
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
    }

    private void OnRouteClick(object? sender, RoutedEventArgs e)
    {
        // 这些业务 setter 只接受 true；重按已选段不应让按钮显示为全未选。
        if (sender is ToggleButton button)
            button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
    }

    private void UpdateCanvasSize()
    {
        // 明确提供有限的测量尺寸。小窗溢出交给外层 ScrollViewer，而非压扁洞察结果。
        var width = _pageScroll.Viewport.Width > 0 ? _pageScroll.Viewport.Width : Bounds.Width;
        var height = _pageScroll.Viewport.Height > 0 ? _pageScroll.Viewport.Height : Bounds.Height;
        _canvas.Width = Math.Max(_canvas.MinWidth, width - 24);
        _canvas.Height = Math.Max(_canvas.MinHeight, height - 24);
        var compact = Bounds.Width < 1150;
        Classes.Set("ink-compact", compact);
        _canvas.ColumnDefinitions[0].Width = new GridLength(compact ? 144 : 176);
    }

    private void OnConversationScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y == 0 && e.OffsetDelta.Y != 0)
        {
            var remaining = _conversationScroll.Extent.Height - _conversationScroll.Viewport.Height - _conversationScroll.Offset.Y;
            _followConversation = remaining <= 28;
        }

        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            QueueConversationFollow();
    }

    private void QueueConversationFollow()
    {
        if (_scrollQueued || !_followConversation || Presentation is null)
            return;

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_followConversation && Presentation is not null && VisualRoot is not null)
                _conversationScroll.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    // 不在 OnDetachedFromVisualTree 中解绑、停止或释放会话；关闭责任留给外壳。
}

/// <summary>选项为 int、呈现字号为 double；显式转换以保证默认 16 能正确选中。</summary>
public sealed class InkStudyFontSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double size ? (int)size : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int size ? (double)size : AvaloniaProperty.UnsetValue;
}

/// <summary>全部历史直接绑定业务集合；缺失来源仍显示中性“发言”。</summary>
public sealed class InkStudySpeakerLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            VadGateController.ActiveSource.Mic => "我 · 麦克风",
            VadGateController.ActiveSource.Loopback => "对方 · 系统回环",
            _ => "发言"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}