using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace TrueFluentPro.Views;

public partial class SongScrollTranslationView
{
    private const double DefaultSpinePosition = 1378;
    private const double DefaultSpineRatio = (DefaultSpinePosition - SongScrollBookSurface.PaperLeft) / SongScrollBookSurface.PaperWidth;
    public static readonly DirectProperty<SongScrollTranslationView, double> SpinePositionProperty =
        AvaloniaProperty.RegisterDirect<SongScrollTranslationView, double>(nameof(SpinePosition), owner => owner.SpinePosition);

    private double _spineRatio = DefaultSpineRatio;
    private double _spinePosition = DefaultSpinePosition;
    private IPointer? _spinePointer;
    private double _spineDragStart, _spineDragOffset, _spineKeyboardStart = DefaultSpineRatio;
    private Canvas? _landscapeClip;
    private bool _spineFloatClampPending;

    /// <summary>343..2521 纸面内的设计坐标；不混用屏幕像素或客户区 DIP。</summary>
    public double SpinePosition => _spinePosition;

    /// <summary>仅本页面实例保留书脊比例，不读写业务配置。</summary>
    public void SetSpineRatio(double ratio)
    {
        _spineRatio = double.IsNaN(ratio) ? DefaultSpineRatio : Math.Clamp(ratio, .25, .55);
        SetAndRaise(SpinePositionProperty, ref _spinePosition,
            SongScrollBookSurface.PaperLeft + SongScrollBookSurface.PaperWidth * _spineRatio);
        UpdateSpineLayout();
    }

    private T SpinePart<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void UpdateSpineLayout()
    {
        // 默认值原样对应 paper=970、conversation=878、right=(1425,1038)、Markdown=960。
        SpinePart<Border>("ScrollConversationPaper").Width = Math.Max(0, SpinePosition - 2 - 406);
        var conversation = SpinePart<ScrollViewer>("ScrollConversation");
        conversation.Width = Math.Max(0, SpinePosition - 47 - 453);
        var segments = SpinePart<Border>("ScrollDisplaySegments");
        segments.Width = Math.Clamp(335 * conversation.Width / 878, 240, 335);
        Canvas.SetLeft(segments, SpinePosition - 51 - segments.Width);

        var heading = SpinePart<Grid>("ScrollHeadingRow");
        heading.Width = Math.Max(0, SpinePosition - 38 - 456);
        SpinePart<TextBlock>("ScrollHeadingSubtitle").IsVisible = heading.Width >= 650;
        SpinePart<TextBlock>("ScrollColophon").Width = Math.Max(0, SpinePosition - 38 - 457);
        var motto = SpinePart<TextBlock>("ScrollRightMotto");
        Canvas.SetLeft(motto, SpinePosition + 100);
        motto.Width = Math.Max(0, 2191 - SpinePosition - 100);

        var middle = SpinePosition - 430;
        SpinePart<Path>("ScrollBookRule").Data = Geometry.Parse(FormattableString.Invariant(
            $"M0 9 Q{middle - 50} 6 {middle} 28 Q{middle + 100} 2 2032 10"));
        var right = SpinePart<Grid>("ScrollRightPage");
        Canvas.SetLeft(right, SpinePosition + 47);
        right.Width = Math.Max(0, 2463 - SpinePosition - 47);
        SpinePart<SongScrollMarkdownBlock>("ScrollMarkdown").MaxContentWidth = Math.Max(40, right.Width - 78);
        if (_landscapeClip != null)
            _landscapeClip.Width = Math.Min(950, Math.Max(0, SpinePosition - 22 - 406));

        var scale = Bounds.Width > 0 ? Bounds.Width / 2560 : 1;
        var handle = SpinePart<Border>("ScrollSpineHandle");
        handle.Width = Math.Max(56, 36 / scale);
        handle.Height = Math.Max(0, 1232 + SpinePart<Canvas>("ScrollScene").Height - 1440);
        Canvas.SetLeft(handle, SpinePosition - handle.Width / 2);

        // 浮纸仍是客户区 overlay；布局完成后仅夹紧一次，不在每次 PointerMoved 排满回调。
        if (!_spineFloatClampPending && SpinePart<Border>("ScrollFloatingPaper").IsVisible)
        {
            _spineFloatClampPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _spineFloatClampPending = false;
                if (_window != null && SpinePart<Border>("ScrollFloatingPaper").IsVisible) ClampFloat();
            }, DispatcherPriority.Loaded);
        }
    }

    private void SpinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _spinePointer != null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var handle = SpinePart<Border>("ScrollSpineHandle");
        handle.Focus(NavigationMethod.Pointer);
        if (e.ClickCount == 2)
        {
            SetSpineRatio(DefaultSpineRatio);
            _spineKeyboardStart = _spineRatio;
        }
        else
        {
            _spineDragStart = _spineRatio;
            _spineDragOffset = e.GetPosition(SpinePart<Canvas>("ScrollScene")).X - SpinePosition;
            _spinePointer = e.Pointer;
            handle.Classes.Set("dragging", true);
            e.Pointer.Capture(handle);
        }
        e.Handled = true;
    }

    private void SpineMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_spinePointer, e.Pointer)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { FinishSpineDrag(); return; }
        var x = e.GetPosition(SpinePart<Canvas>("ScrollScene")).X - _spineDragOffset;
        SetSpineRatio((x - SongScrollBookSurface.PaperLeft) / SongScrollBookSurface.PaperWidth);
        e.Handled = true;
    }

    private void SpineReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_spinePointer, e.Pointer) || e.InitialPressMouseButton != MouseButton.Left) return;
        FinishSpineDrag();
        _spineKeyboardStart = _spineRatio;
        e.Handled = true;
    }

    // Avalonia 11.3.11 的 TouchCancel / 平台取消均通过 Capture(null) 派发 CaptureLost。
    private void SpineCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    { if (ReferenceEquals(_spinePointer, e.Pointer)) FinishSpineDrag(releaseCapture: false); }
    private void SpineWindowDeactivated(object? sender, EventArgs e) => FinishSpineDrag();
    private void SpineGotFocus(object? sender, GotFocusEventArgs e) => _spineKeyboardStart = _spineRatio;
    private void SpineLostFocus(object? sender, RoutedEventArgs e) => FinishSpineDrag();

    private void FinishSpineDrag(bool cancel = false, bool releaseCapture = true)
    {
        var pointer = _spinePointer;
        _spinePointer = null; // 先清状态，释放捕获时同步触发的 CaptureLost 不会重入。
        _spineDragOffset = 0;
        if (cancel && pointer != null) SetSpineRatio(_spineDragStart);
        var handle = SpinePart<Border>("ScrollSpineHandle");
        handle.Classes.Set("dragging", false);
        // 捕获可能已转交别的控件，不能清除新 owner 的捕获。
        if (releaseCapture && ReferenceEquals(pointer?.Captured, handle)) pointer!.Capture(null);
    }

    private void SpineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? .05 : .01;
        if (e.Key == Key.Escape)
        {
            if (_spinePointer != null) FinishSpineDrag(cancel: true);
            else SetSpineRatio(_spineKeyboardStart);
        }
        else
        {
            var ratio = e.Key switch
            {
                Key.Left => _spineRatio - step, Key.Right => _spineRatio + step,
                Key.Home => .25, Key.End => .55, Key.Enter => DefaultSpineRatio,
                _ => double.NaN
            };
            if (double.IsNaN(ratio)) return;
            FinishSpineDrag();
            SetSpineRatio(ratio);
            if (e.Key == Key.Enter) _spineKeyboardStart = _spineRatio;
        }
        // 不让页面原有 Esc 浮纸逻辑或左右标签导航再处理一次。
        e.Handled = true;
    }
}