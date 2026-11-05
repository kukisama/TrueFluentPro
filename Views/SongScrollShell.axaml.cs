using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace TrueFluentPro.Views;

public interface ISongScrollPage
{
    SongScrollShell Shell { get; }
}

/// <summary>长卷各业务页共用的原生书册；只管理外壳，不接管内容 DataContext。</summary>
public partial class SongScrollShell : UserControl
{
    public static readonly StyledProperty<Control?> LeftPageProperty = AvaloniaProperty.Register<SongScrollShell, Control?>(nameof(LeftPage));
    public static readonly StyledProperty<Control?> RightPageProperty = AvaloniaProperty.Register<SongScrollShell, Control?>(nameof(RightPage));
    public static readonly StyledProperty<string> PageKeyProperty = AvaloniaProperty.Register<SongScrollShell, string>(nameof(PageKey), "");
    public static readonly StyledProperty<string> PageTitleProperty = AvaloniaProperty.Register<SongScrollShell, string>(nameof(PageTitle), "");
    public static readonly StyledProperty<string> PageSubtitleProperty = AvaloniaProperty.Register<SongScrollShell, string>(nameof(PageSubtitle), "");
    public static readonly StyledProperty<double> DefaultLeftRatioProperty = AvaloniaProperty.Register<SongScrollShell, double>(nameof(DefaultLeftRatio), .3, coerce: (_, value) => ClampRatio(value));
    public static readonly StyledProperty<double> LeftRatioProperty = AvaloniaProperty.Register<SongScrollShell, double>(nameof(LeftRatio), .3, defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => ClampRatio(value));
    public static readonly DirectProperty<SongScrollShell, double> SpinePositionProperty =
        AvaloniaProperty.RegisterDirect<SongScrollShell, double>(nameof(SpinePosition), owner => owner.SpinePosition);

    public Control? LeftPage { get => GetValue(LeftPageProperty); set => SetValue(LeftPageProperty, value); }
    public Control? RightPage { get => GetValue(RightPageProperty); set => SetValue(RightPageProperty, value); }
    public string PageKey { get => GetValue(PageKeyProperty); set => SetValue(PageKeyProperty, value); }
    public string PageTitle { get => GetValue(PageTitleProperty); set => SetValue(PageTitleProperty, value); }
    public string PageSubtitle { get => GetValue(PageSubtitleProperty); set => SetValue(PageSubtitleProperty, value); }
    public double DefaultLeftRatio { get => GetValue(DefaultLeftRatioProperty); set => SetValue(DefaultLeftRatioProperty, value); }
    public double LeftRatio { get => GetValue(LeftRatioProperty); set => SetValue(LeftRatioProperty, value); }
    public double SpinePosition => _spinePosition;
    public event Action<string>? NavigationRequested;
    public event Action<Control>? ThemeRequested;

    private const double DesignWidth = 2560;
    private const double SceneTrim = 64;
    private double _spinePosition = SongScrollBookSurface.PaperLeft + SongScrollBookSurface.PaperWidth * .3;
    private bool _ready, _applyingDefault, _hasOwnRatio;
    private Window? _window;
    private IPointer? _dragPointer;
    private double _dragStart, _dragOffset, _keyboardStart;

    public SongScrollShell()
    {
        AvaloniaXamlLoader.Load(this);
        _ready = true;
        UpdatePage();
        UpdateBookLayout();
        SizeChanged += (_, _) => UpdateBookLayout();
    }

    public void SetLeftRatio(double ratio)
    {
        _hasOwnRatio = true;
        // 不拆除页面将来可能提供的绑定；每个 Shell 实例独立保留布局。
        SetCurrentValue(LeftRatioProperty, ClampRatio(ratio));
    }

    private static double ClampRatio(double ratio) => double.IsNaN(ratio) ? .3 : Math.Clamp(ratio, .25, .55);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LeftRatioProperty)
        {
            if (!_applyingDefault) _hasOwnRatio = true;
            SetAndRaise(SpinePositionProperty, ref _spinePosition,
                SongScrollBookSurface.PaperLeft + SongScrollBookSurface.PaperWidth * LeftRatio);
            if (_ready) UpdateBookLayout();
        }
        else if (change.Property == DefaultLeftRatioProperty && !_hasOwnRatio)
        {
            _applyingDefault = true;
            try { SetCurrentValue(LeftRatioProperty, DefaultLeftRatio); }
            finally { _applyingDefault = false; }
        }
        else if (_ready && change.Property == PageKeyProperty) UpdatePage();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null)
        {
            _window.PropertyChanged += WindowChanged;
            _window.Deactivated += WindowDeactivated;
        }
        UpdateBookLayout();
        UpdateCaption();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FinishDrag();
        if (_window != null)
        {
            _window.PropertyChanged -= WindowChanged;
            _window.Deactivated -= WindowDeactivated;
            _window = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private T Part<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void UpdateBookLayout()
    {
        if (!_ready) return;
        var stageHeight = Bounds.Width > 0 && Bounds.Height > 0 ? Bounds.Height * DesignWidth / Bounds.Width : 1440;
        if (!double.IsFinite(stageHeight)) return;
        var sceneHeight = stageHeight + SceneTrim;
        var extra = sceneHeight - 1440;
        Part<Canvas>("ShellStage").Height = stageHeight;
        Part<Canvas>("ShellScene").Height = sceneHeight;
        var surface = Part<SongScrollBookSurface>("ShellBookSurface");
        surface.Height = sceneHeight;
        surface.SceneHeight = sceneHeight;

        var left = Part<ContentControl>("ShellLeftPage");
        left.Width = Math.Max(0, SpinePosition - 24 - 406);
        left.Height = Math.Max(0, 1074 + extra);
        var right = Part<ContentControl>("ShellRightPage");
        Canvas.SetLeft(right, SpinePosition + 47);
        right.Width = Math.Max(0, 2463 - SpinePosition - 47);
        right.Height = left.Height;
        var heading = Part<Grid>("ShellHeading");
        heading.Width = Math.Max(0, SpinePosition - 38 - 456);
        Part<TextBlock>("ShellSubtitle").IsVisible = heading.Width >= 650;
        var motto = Part<TextBlock>("ShellRightMotto");
        Canvas.SetLeft(motto, SpinePosition + 100);
        motto.Width = Math.Max(0, 2191 - SpinePosition - 100);

        var middle = SpinePosition - 430;
        Part<Path>("ShellLeftRule").Data = Geometry.Parse(FormattableString.Invariant($"M0 9 Q{middle - 160} 6 {middle - 26} 28"));
        Part<Path>("ShellRightRule").Data = Geometry.Parse(FormattableString.Invariant($"M{middle + 26} 28 Q{middle + 120} 2 2032 10"));
        var landscape = Part<Canvas>("ShellLandscapeClip");
        landscape.Width = Math.Min(950, left.Width);
        Canvas.SetTop(landscape, 940 + extra);
        Canvas.SetTop(Part<Image>("ShellInkstone"), 1270 + extra);
        Part<Border>("ShellSidebar").Height = Math.Max(0, 1165 + extra);
        var footer = Part<Border>("ShellSidebarFooter");
        Canvas.SetTop(footer, 1133 + Math.Max(0, extra));
        footer.Height = Math.Max(0, 262 + Math.Min(0, extra));

        var scale = Bounds.Width > 0 ? Bounds.Width / DesignWidth : 1;
        var handle = Part<Border>("ShellSpineHandle");
        handle.Width = Math.Max(56, 36 / scale);
        handle.Height = Math.Max(0, 1232 + extra);
        Canvas.SetLeft(handle, SpinePosition - handle.Width / 2);
        // overlay 在客户区坐标中，不得遮住缩放后的窗口动作按钮。
        Part<Border>("ShellTitleDrag").Width = Math.Max(0, 2223 * scale);
    }

    private void UpdatePage()
    {
        foreach (var control in Part<StackPanel>("ShellNavigation").Children)
        {
            if (control is not Button button) continue;
            var selected = button.Tag is string key && key == PageKey;
            button.Classes.Set("selected", selected);
            if (button.Content is StackPanel row && row.Children[0] is SongScrollIcon icon)
                icon.Ink = new SolidColorBrush(Color.Parse(selected ? "#9E3A21" : "#655B49"));
        }
        Part<Button>("ShellSettings").Classes.Set("selected", PageKey == "settings");
        Part<TextBlock>("ShellRightMotto").Text = PageKey switch
        {
            "media" => "片言生万象，落笔自成章。",
            "mediav2" => "心有丘壑，眼见山河。",
            "audiolab" => "静听弦外意，细读纸间声。",
            "taskmonitor" => "诸事有序，行稳致远。",
            "settings" => "工欲善其事，必先利其器。",
            _ => "海内存知己，天涯若比邻。"
        };
    }

    private void Navigate(object? sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string key }) NavigationRequested?.Invoke(key); }
    private void ThemeClick(object? sender, RoutedEventArgs e)
    { if (sender is Control anchor) ThemeRequested?.Invoke(anchor); }

    private void SpinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragPointer != null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var handle = Part<Border>("ShellSpineHandle");
        handle.Focus(NavigationMethod.Pointer);
        if (e.ClickCount == 2)
        {
            SetLeftRatio(DefaultLeftRatio);
            _keyboardStart = LeftRatio;
        }
        else
        {
            _dragStart = LeftRatio;
            _dragOffset = e.GetPosition(Part<Canvas>("ShellScene")).X - SpinePosition;
            _dragPointer = e.Pointer;
            handle.Classes.Set("dragging", true);
            e.Pointer.Capture(handle);
        }
        e.Handled = true;
    }

    private void SpineMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_dragPointer, e.Pointer)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { FinishDrag(); return; }
        var x = e.GetPosition(Part<Canvas>("ShellScene")).X - _dragOffset;
        SetLeftRatio((x - SongScrollBookSurface.PaperLeft) / SongScrollBookSurface.PaperWidth);
        e.Handled = true;
    }

    private void SpineReleased(object? sender, PointerReleasedEventArgs e)
    { if (ReferenceEquals(_dragPointer, e.Pointer)) { FinishDrag(); _keyboardStart = LeftRatio; e.Handled = true; } }
    private void SpineCaptureLost(object? sender, PointerCaptureLostEventArgs e) => FinishDrag(releaseCapture: false);
    private void WindowDeactivated(object? sender, EventArgs e) => FinishDrag();
    private void SpineGotFocus(object? sender, GotFocusEventArgs e) => _keyboardStart = LeftRatio;
    private void SpineLostFocus(object? sender, RoutedEventArgs e) => FinishDrag();

    private void FinishDrag(bool cancel = false, bool releaseCapture = true)
    {
        var pointer = _dragPointer;
        _dragPointer = null; // 先清状态，释放捕获触发的 CaptureLost 不会重入。
        if (cancel && pointer != null) SetLeftRatio(_dragStart);
        _dragOffset = 0;
        if (_ready) Part<Border>("ShellSpineHandle").Classes.Set("dragging", false);
        // CaptureLost 可能表示别的控件已取得捕获，不能再清除它的新捕获。
        if (releaseCapture) pointer?.Capture(null);
    }

    private void SpineKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? .05 : .01;
        if (e.Key == Key.Escape)
        {
            if (_dragPointer != null) FinishDrag(cancel: true);
            else SetLeftRatio(_keyboardStart);
        }
        else
        {
            var next = e.Key switch
            {
                Key.Left => LeftRatio - step, Key.Right => LeftRatio + step,
                Key.Home => .25, Key.End => .55, Key.Enter => DefaultLeftRatio,
                _ => double.NaN
            };
            if (double.IsNaN(next)) return;
            FinishDrag();
            SetLeftRatio(next);
            if (e.Key == Key.Enter) _keyboardStart = LeftRatio;
        }
        e.Handled = true;
    }

    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.CanMaximizeProperty || e.Property == Window.CanMinimizeProperty)
            UpdateCaption();
    }

    private void UpdateCaption()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        var maximize = Part<Button>("ShellMaximize");
        maximize.IsEnabled = _window?.CanMaximize ?? true;
        Part<Button>("ShellMinimize").IsEnabled = _window?.CanMinimize ?? true;
        AutomationProperties.SetName(maximize, maximized ? "还原" : "最大化");
        Part<Path>("ShellMaximizeMark").Data = Geometry.Parse(maximized ? "M6 2 H22 V18 M2 6 H18 V22 H2 Z" : "M2 2 H22 V22 H2 Z");
    }

    private void TitleDrag(object? sender, PointerPressedEventArgs e)
    {
        if (_window == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else _window.BeginMoveDrag(e);
        e.Handled = true;
    }
    private void ToggleMaximize()
    { if (_window?.CanMaximize == true) _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void Minimize(object? sender, RoutedEventArgs e)
    { if (_window?.CanMinimize == true) _window.WindowState = WindowState.Minimized; }
    private void Maximize(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindow(object? sender, RoutedEventArgs e) => _window?.Close();
}