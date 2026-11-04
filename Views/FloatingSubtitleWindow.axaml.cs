using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{    public partial class FloatingSubtitleWindow : Window
    {
        private const double MinAutoFontSize = 18;
        private const double MaxAutoFontSize = 96;
        private const double LineHeightScale = 1.25;
        // 外边距 20 + 紧凑控制栏 28 + 主体上下边距 12 + Padding 2。
        private const double VerticalChrome = 62;
        private readonly DispatcherTimer _pageTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private FloatingSubtitleUpdate? _currentCue;
        // 每个来源浮窗独立前进，已被新句替代的旧终稿不能再抢回显示权。
        private long _latestSegmentId;
        private FloatingSubtitleViewModel? _subscribedViewModel;
        private bool _updatingLayout;
        private double _availableWidth;
        private int _pageStart;
        private int _pageEnd;
        private string _pageText = "";
        private double _readUntil;

        public FloatingSubtitleWindow()
        {
            InitializeComponent();
            // 等实际排版后的可用区域变化再测量，不使用窗口尺寸猜文本宽度。
            SubtitleHost.SizeChanged += OnSubtitleHostSizeChanged;
            _pageTimer.Tick += OnPageTimerTick;
            SetInitialPosition();
            UpdateSubtitleLayout();
        }

        public FloatingSubtitleWindow(FloatingSubtitleViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _subscribedViewModel.SubtitleReceived -= OnSubtitleReceived;
                _subscribedViewModel = null;
            }

            base.OnDataContextChanged(e);
            _currentCue = null;
            _latestSegmentId = 0;
            _pageStart = 0;
            _pageText = "";

            if (DataContext is FloatingSubtitleViewModel vm)
            {
                _subscribedViewModel = vm;
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.SubtitleReceived += OnSubtitleReceived;
                if (vm.CurrentUpdate is { } update) OnSubtitleReceived(update);
            }

            UpdateSubtitleLayout();
            ApplyClickThrough();
        }

        private void OnSubtitleHostSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateSubtitleLayout();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(FloatingSubtitleViewModel.FontScaleBias)
                or nameof(FloatingSubtitleViewModel.IsFontSizeLocked)
                or nameof(FloatingSubtitleViewModel.FixedFontSize))
            {
                UpdateSubtitleLayout();
            }
            else if (e.PropertyName == nameof(FloatingSubtitleViewModel.IsClickThrough))
            {
                ApplyClickThrough();
            }
        }

        private void SetInitialPosition()
        {
            if (Screens.Primary != null)
            {
                var screen = Screens.Primary.WorkingArea;
                var windowWidth = (int)Math.Round(Width);
                var windowHeight = (int)Math.Round(Height);
                
                Position = new PixelPoint(
                    (int)(screen.X + (screen.Width - windowWidth) / 2),
                    (int)(screen.Y + screen.Height - windowHeight - 50)
                );
            }
        }

        private void UpdateSubtitleLayout()
        {
            if (_updatingLayout || SubtitleTextBlock == null || SubtitleHost == null
                || DataContext is not FloatingSubtitleViewModel viewModel)
            {
                return;
            }

            _updatingLayout = true;
            try
            {
                // 固定字号时保证两行完整可见；自动模式只按高度计算，绝不因句子变长缩字。
                MinHeight = viewModel.IsFontSizeLocked
                    ? Math.Max(120, Math.Ceiling(VerticalChrome + 2 * viewModel.FixedFontSize * LineHeightScale + 2))
                    : 120;
                _availableWidth = SubtitleHost.Bounds.Width - SubtitleHost.Padding.Left - SubtitleHost.Padding.Right;
                var height = SubtitleHost.Bounds.Height - SubtitleHost.Padding.Top - SubtitleHost.Padding.Bottom;
                if (_availableWidth <= 0 || height <= 0) return;

                var maxFittingFont = Math.Max(1, (height - 2) / (2 * LineHeightScale));
                var fontSize = viewModel.IsFontSizeLocked
                    ? viewModel.FixedFontSize
                    : Math.Min(maxFittingFont, Math.Clamp(maxFittingFont * 0.68 * viewModel.FontScaleBias,
                        MinAutoFontSize, MaxAutoFontSize));
                SubtitleTextBlock.FontSize = fontSize;
                SubtitleTextBlock.LineHeight = fontSize * LineHeightScale;
                RenderPage(resetTiming: false);
            }
            finally
            {
                _updatingLayout = false;
            }
        }

        private void OnSubtitleReceived(FloatingSubtitleUpdate update)
        {
            if (string.IsNullOrWhiteSpace(update.Text))
            {
                _currentCue = null;
                _pageStart = 0;
                RenderPage(resetTiming: true);
                return;
            }
            // 正编号为主程序按句分配的进度；负编号为重连等独立状态提示。
            if (update.SegmentId > 0 && update.SegmentId < _latestSegmentId) return;
            if (update.SegmentId > 0) _latestSegmentId = update.SegmentId;

            var sameSentence = _currentCue is { } current && current.SegmentId == update.SegmentId;
            var restartPage = !sameSentence;
            if (sameSentence)
            {
                _pageStart = RemapPageStart(_currentCue!.Text, update.Text, _pageStart, out restartPage);
            }
            else
            {
                // 实时字幕不排队补播旧句：新译文立即显示，同句才保留两行分页的阅读时间。
                _pageStart = 0;
            }
            _currentCue = update;
            RenderPage(resetTiming: restartPage);
        }

        private void OnPageTimerTick(object? sender, EventArgs e)
        {
            if (_currentCue == null || _availableWidth <= 0) return;
            var now = _clock.Elapsed.TotalSeconds;
            if (now < _readUntil) return;

            if (_pageEnd < _currentCue.Text.Length)
            {
                _pageStart = _pageEnd;
                RenderPage(resetTiming: true);
            }
            // 没有后续内容时保留末页，不闪空、不反复重播。
        }

        private void RenderPage(bool resetTiming)
        {
            if (_subscribedViewModel == null || _availableWidth <= 0) return;
            if (_currentCue == null)
            {
                _subscribedViewModel.UpdateFormattedSubtitleText("等待字幕内容...");
                _subscribedViewModel.SetDisplayedSource(VadGateController.ActiveSource.None);
                _pageText = "";
                _pageEnd = 0;
                return;
            }

            var update = _currentCue;
            if (_pageStart >= update.Text.Length) _pageStart = 0;
            while (_pageStart < update.Text.Length - 1 && char.IsWhiteSpace(update.Text[_pageStart])) _pageStart++;
            var remaining = update.Text[_pageStart..];
            using var layout = MeasurePage(remaining);
            var length = 0;
            foreach (var line in layout.TextLines) length += line.Length;
            length = Math.Min(remaining.Length, length);
            if (length == 0) length = StringInfo.GetNextTextElement(remaining).Length;

            // 页面溢出时优先在后半页的标点处切开，避免为一个短尾巴重排整页。
            if (length < remaining.Length)
            {
                for (var i = length - 1; i >= length / 2; i--)
                {
                    var ch = remaining[i];
                    if ("。！？；，、!?;".Contains(ch)
                        || ((ch == '.' || ch == ',') && i + 1 < remaining.Length && char.IsWhiteSpace(remaining[i + 1])))
                    {
                        length = i + 1;
                        break;
                    }
                }
            }

            _pageEnd = _pageStart + length;
            var page = remaining[..length].TrimEnd();
            using var pageLayout = MeasurePage(page);
            if (pageLayout.TextLines.Count > 1)
            {
                var firstLineLength = pageLayout.TextLines[0].Length;
                page = page[..firstLineLength].TrimEnd() + "\n" + page[firstLineLength..].TrimStart();
            }

            var now = _clock.Elapsed.TotalSeconds;
            // 这是实时阅读节奏的初始值，不套用预制影片的帧间隔规则。
            var readingSeconds = Math.Clamp(1 + StringInfo.ParseCombiningCharacters(page).Length / 8.0, 1.5, 6);
            if (resetTiming || _pageText.Length == 0)
            {
                _readUntil = now + readingSeconds;
            }
            else if (_pageText != page)
            {
                // 按新增/替换的可见文字补阅读预算；仅重新换行不重复计时。
                var before = _pageText.Replace("\n", " ");
                var after = page.Replace("\n", " ");
                var common = 0;
                while (common < before.Length && common < after.Length && before[common] == after[common]) common++;
                var changedCharacters = StringInfo.ParseCombiningCharacters(after[common..]).Length;
                if (changedCharacters > 0)
                {
                    _readUntil = Math.Min(now + 6, Math.Max(now, _readUntil) + changedCharacters / 8.0);
                    var minimumReadUntil = now + Math.Min(1.5, Math.Max(0.8, changedCharacters / 8.0));
                    _readUntil = Math.Max(_readUntil, minimumReadUntil);
                }
            }
            _pageText = page;
            _subscribedViewModel.SetDisplayedSource(update.Source);
            _subscribedViewModel.UpdateFormattedSubtitleText(page);
        }

        private TextLayout MeasurePage(string text)
        {
            var typeface = new Typeface(SubtitleTextBlock.FontFamily, SubtitleTextBlock.FontStyle,
                SubtitleTextBlock.FontWeight, SubtitleTextBlock.FontStretch);
            return new TextLayout(text, typeface, SubtitleTextBlock.FontSize, Brushes.White,
                textWrapping: TextWrapping.Wrap, flowDirection: SubtitleTextBlock.FlowDirection,
                maxWidth: _availableWidth, lineHeight: SubtitleTextBlock.LineHeight, maxLines: 2);
        }

        private static int RemapPageStart(string previous, string current, int start, out bool restartPage)
        {
            restartPage = false;
            // 前缀追加保持已读边界；前文修订且后缀仍可对齐时随修订长度移动边界。
            var prefix = 0;
            while (prefix < previous.Length && prefix < current.Length && previous[prefix] == current[prefix]) prefix++;
            var suffix = 0;
            while (suffix < previous.Length - prefix && suffix < current.Length - prefix
                && previous[previous.Length - suffix - 1] == current[current.Length - suffix - 1]) suffix++;
            var mapped = start <= prefix
                ? start
                : start >= previous.Length - suffix && suffix > 0
                    ? start + current.Length - previous.Length
                    : -1;
            // 前文被整体改写而无法对齐时，从完整新句重读，不能压到末字或标点。
            if (mapped < 0 || mapped >= current.Length)
            {
                restartPage = true;
                return 0;
            }
            // 所有映射分支都要校正字素边界，不能从组合重音或代理对中间开始。
            var boundaries = StringInfo.ParseCombiningCharacters(current);
            var result = 0;
            foreach (var boundary in boundaries)
            {
                if (boundary > mapped) break;
                result = boundary;
            }
            return result;
        }

        private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            for (var visual = e.Source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Button || visual == ResizeGrip) return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.BeginMoveDrag(e);
                e.Handled = true;
            }

            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                var viewModel = DataContext as FloatingSubtitleViewModel;
                viewModel?.ToggleTransparency();
                e.Handled = true;
            }
        }

        private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
        }

        private void OnIncreaseFontSize(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingSubtitleViewModel vm)
            {
                vm.IncreaseFontSize();
            }
        }

        private void OnDecreaseFontSize(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingSubtitleViewModel vm)
            {
                vm.DecreaseFontSize();
            }
        }

        private void OnResetFontSize(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingSubtitleViewModel vm)
            {
                vm.ResetFontSize();
            }
        }

        private void OnToggleFontSizeLock(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingSubtitleViewModel vm)
            {
                vm.ToggleFontSizeLock(SubtitleTextBlock.FontSize);
            }
        }

        private void OnToggleBackground(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingSubtitleViewModel vm)
            {
                vm.ToggleTransparency();
            }
        }

        private void OnToggleClickThrough(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingSubtitleViewModel vm)
            {
                vm.ToggleClickThrough();
            }
        }

        public void RestoreInteraction()
        {
            if (DataContext is FloatingSubtitleViewModel vm) vm.IsClickThrough = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || DataContext is not FloatingSubtitleViewModel vm) return;
            if (e.Key == Key.Escape && vm.IsClickThrough)
            {
                RestoreInteraction();
                e.Handled = true;
                return;
            }
            if ((e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.Control)
            {
                switch (e.Key)
                {
                    case Key.Add:
                    case Key.OemPlus: vm.IncreaseFontSize(); break;
                    case Key.Subtract:
                    case Key.OemMinus: vm.DecreaseFontSize(); break;
                    case Key.D0: vm.ResetFontSize(); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }
            // Alt+Shift 会被部分系统输入语言切换处理吞掉 Shift；缩放用独立组合避免误移动。
            var resize = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt);
            if (e.KeyModifiers != KeyModifiers.Alt && !resize) return;
            var dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
            var dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            if (dx == 0 && dy == 0) return;
            if (resize)
            {
                Width = Math.Clamp(Width + dx * 20, MinWidth, MaxWidth);
                Height = Math.Clamp(Height + dy * 10, MinHeight, MaxHeight);
            }
            else
            {
                Position = new PixelPoint(Position.X + dx * 10, Position.Y + dy * 10);
            }
            e.Handled = true;
        }

        // ============== Windows 点击穿透（WS_EX_TRANSPARENT） ==============
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private void ApplyClickThrough()
        {
            if (!OperatingSystem.IsWindows()) return;
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            var enabled = (DataContext as FloatingSubtitleViewModel)?.IsClickThrough == true;
            var ex = GetWindowLong(handle, GWL_EXSTYLE);
            if (enabled)
            {
                ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            }
            else
            {
                ex &= ~WS_EX_TRANSPARENT;
            }
            SetWindowLong(handle, GWL_EXSTYLE, ex);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            UpdateSubtitleLayout();
            _pageTimer.Start();
            ApplyClickThrough();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _pageTimer.Stop();
            _pageTimer.Tick -= OnPageTimerTick;
            SubtitleHost.SizeChanged -= OnSubtitleHostSizeChanged;
            _currentCue = null;
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _subscribedViewModel.SubtitleReceived -= OnSubtitleReceived;
                _subscribedViewModel.OnWindowClosed();
                _subscribedViewModel = null;
            }

            base.OnClosed(e);
        }
    }
}

