using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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
        private FloatingSubtitleUpdate? _currentCue;
        // 只保存当前句开始前已在屏幕上的两行，不保存待播历史队列。
        private string _segmentPrefix = "";
        private string _visibleText = "";
        private FloatingSubtitleViewModel? _subscribedViewModel;
        private bool _updatingLayout;
        private double _availableWidth;

        public FloatingSubtitleWindow()
        {
            InitializeComponent();
            // 等实际排版后的可用区域变化再测量，不使用窗口尺寸猜文本宽度。
            SubtitleHost.SizeChanged += OnSubtitleHostSizeChanged;
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
            _segmentPrefix = "";
            _visibleText = "";

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
                RenderSubtitle();
            }
            finally
            {
                _updatingLayout = false;
            }
        }

        private void OnSubtitleReceived(FloatingSubtitleUpdate update)
        {
            if (update.SegmentId < 0)
            {
                // 负编号为显式清空或重连提示，不拼进对话上下文。
                _currentCue = string.IsNullOrWhiteSpace(update.Text) ? null : update;
                _segmentPrefix = "";
                _visibleText = "";
                RenderSubtitle();
                return;
            }
            var sameSentence = _currentCue is { } current && current.SegmentId == update.SegmentId;
            if (!sameSentence)
            {
                // 换句不清屏；同句检查点保持不变，partial/final 全文修订不会重复追加。
                _segmentPrefix = _currentCue is { SegmentId: > 0 } ? _visibleText : "";
            }
            _currentCue = update;
            RenderSubtitle();
        }

        private void RenderSubtitle()
        {
            if (_subscribedViewModel == null || _availableWidth <= 0) return;
            if (_currentCue == null)
            {
                _subscribedViewModel.UpdateFormattedSubtitleText("等待字幕内容...");
                _subscribedViewModel.SetDisplayedSource(VadGateController.ActiveSource.None);
                _visibleText = "";
                return;
            }

            var update = _currentCue;
            var content = _segmentPrefix.Length == 0 ? update.Text : _segmentPrefix + " " + update.Text;
            // 必须测量全部视觉行，再选择末两行；maxLines=2 只会测到全文开头。
            using var layout = MeasureLines(content);
            var rows = new List<string>(2);
            for (var index = Math.Max(0, layout.TextLines.Count - 2); index < layout.TextLines.Count; index++)
            {
                var line = layout.TextLines[index];
                var start = Math.Clamp(line.FirstTextSourceIndex, 0, content.Length);
                var length = Math.Clamp(line.Length - line.NewLineLength, 0, content.Length - start);
                rows.Add(content.Substring(start, length).Trim());
            }
            _visibleText = string.Join("\n", rows);
            _subscribedViewModel.SetDisplayedSource(update.Source);
            _subscribedViewModel.UpdateFormattedSubtitleText(_visibleText);
        }

        private TextLayout MeasureLines(string text)
        {
            var typeface = new Typeface(SubtitleTextBlock.FontFamily, SubtitleTextBlock.FontStyle,
                SubtitleTextBlock.FontWeight, SubtitleTextBlock.FontStretch);
            return new TextLayout(text, typeface, SubtitleTextBlock.FontSize, Brushes.White,
                textWrapping: TextWrapping.Wrap, flowDirection: SubtitleTextBlock.FlowDirection,
                maxWidth: _availableWidth, lineHeight: SubtitleTextBlock.LineHeight);
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
            ApplyClickThrough();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            SubtitleHost.SizeChanged -= OnSubtitleHostSizeChanged;
            _currentCue = null;
            _segmentPrefix = "";
            _visibleText = "";
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

