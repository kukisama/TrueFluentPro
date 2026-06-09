using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{    public partial class FloatingSubtitleWindow : Window
    {
        private const double MinAutoFontSize = 18;
        private const double MaxAutoFontSize = 96;
        private const double LineHeightScale = 1.14;
        private const int LatinTargetDisplayUnits = 34;
        private const int CjkTargetDisplayUnits = 22;
        private readonly TranslateTransform _subtitleTranslate = new();
        private FloatingSubtitleViewModel? _subscribedViewModel;
        // 当前已锁定的字号（本句开始时计算，整句期间不再上调，避免抖动）
        private double _lockedFontSize;
        private int _lockedTextLength;

        public FloatingSubtitleWindow()
        {
            InitializeComponent();

            SubtitleTextBlock.RenderTransform = _subtitleTranslate;

            // 文本横向平移加缓动 → 溢出时丝滑左滑，不再瞬移
            _subtitleTranslate.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = TimeSpan.FromMilliseconds(240),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                }
            };

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
                _subscribedViewModel = null;
            }

            base.OnDataContextChanged(e);

            if (DataContext is FloatingSubtitleViewModel vm)
            {
                _subscribedViewModel = vm;
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }

            UpdateSubtitleLayout();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateSubtitleLayout();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(FloatingSubtitleViewModel.SubtitleText)
                or nameof(FloatingSubtitleViewModel.FontScaleBias))
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
            if (SubtitleTextBlock == null || SubtitleHost == null)
            {
                return;
            }

            if (DataContext is not FloatingSubtitleViewModel viewModel)
            {
                return;
            }

            var text = viewModel.SubtitleText;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var availableWidth = Math.Max(280, SubtitleHost.Bounds.Width - SubtitleHost.Padding.Left - SubtitleHost.Padding.Right);
            var availableHeight = Math.Max(36, SubtitleHost.Bounds.Height - SubtitleHost.Padding.Top - SubtitleHost.Padding.Bottom);

            // 字号策略：单行模式下以高度为基准，按 bias 微调。整段会话只允许"收缩"，文本明显缩短或 bias 变化时才重算 → 避免抖动。
            var singleLineHeightFont = ClampFont(Math.Max(1, availableHeight) * 0.62 / LineHeightScale * viewModel.FontScaleBias);
            var trimmedLen = NormalizeText(text).Length;
            var isNewSentence = trimmedLen == 0
                                || _lockedFontSize <= 0
                                || trimmedLen < _lockedTextLength - 6
                                || Math.Abs(_lockedFontSize - singleLineHeightFont) > 4;

            double singleLineFont;
            if (isNewSentence)
            {
                singleLineFont = singleLineHeightFont;
                _lockedFontSize = singleLineHeightFont;
            }
            else
            {
                singleLineFont = _lockedFontSize;
            }
            _lockedTextLength = trimmedLen;
            var normalizedText = NormalizeText(text);

            viewModel.UpdateFormattedSubtitleText(normalizedText);

            // 永远单行 + 右锚定滑窗：保证流式追加场景下，最新尾部始终可见。
            // （双行 wrap 会把头部固定显示、把新文字截掉，与字幕场景冲突。）
            SubtitleTextBlock.TextWrapping = TextWrapping.NoWrap;
            SubtitleTextBlock.MaxLines = 1;
            SubtitleTextBlock.FontSize = singleLineFont;
            SubtitleTextBlock.LineHeight = Math.Round(singleLineFont * LineHeightScale, MidpointRounding.AwayFromZero);
            SubtitleTextBlock.LetterSpacing = GetLetterSpacing(text, singleLineFont);
            SubtitleTextBlock.Margin = CreateTextMargin(singleLineFont);

            var singleLineTextWidth = MeasureSubtitleTextWidth(availableHeight);
            var singleLineOverflow = Math.Max(0, singleLineTextWidth - availableWidth);
            var isOverflowing = singleLineOverflow > 0.5;

            SubtitleTextBlock.HorizontalAlignment = isOverflowing ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            SubtitleTextBlock.TextAlignment = isOverflowing ? TextAlignment.Left : TextAlignment.Center;
            _subtitleTranslate.X = isOverflowing ? -singleLineOverflow : 0;
        }

        private double MeasureSubtitleTextWidth(double availableHeight)
        {
            SubtitleTextBlock.Measure(new Size(double.PositiveInfinity, Math.Max(availableHeight, SubtitleTextBlock.FontSize * LineHeightScale)));
            return SubtitleTextBlock.DesiredSize.Width + SubtitleTextBlock.Margin.Left + SubtitleTextBlock.Margin.Right;
        }

        private static Thickness CreateTextMargin(double fontSize)
        {
            var horizontal = Math.Round(fontSize * 0.08, MidpointRounding.AwayFromZero);
            return new Thickness(horizontal, -1, horizontal, 1);
        }

        private static string NormalizeText(string text)
        {
            var normalized = text.Replace("\r", " ").Replace("\n", " ");
            return System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        private static double GetFontSizeForSingleLine(double availableHeight, double bias)
        {
            var heightDrivenFont = Math.Max(1, availableHeight) * 0.68 / LineHeightScale;
            return ClampFont(heightDrivenFont * bias);
        }

        private static int GetTargetDisplayUnits(string text)
        {
            var asciiCount = 0;
            var cjkCount = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                if (IsEastAsianWide(ch))
                {
                    cjkCount++;
                }
                else
                {
                    asciiCount++;
                }
            }

            return asciiCount >= cjkCount ? LatinTargetDisplayUnits : CjkTargetDisplayUnits;
        }

        private static double GetStableSingleLineFontSize(string text, double availableWidth, double availableHeight, double bias, int targetDisplayUnits)
        {
            var heightFont = GetFontSizeForSingleLine(availableHeight, bias);
            var averageWidthFactor = GetAverageWidthFactor(text);
            var widthFont = availableWidth / Math.Max(1, targetDisplayUnits * averageWidthFactor * 1.06);
            return ClampFont(Math.Min(heightFont, widthFont * bias));
        }

        private static double GetAverageWidthFactor(string text)
        {
            var units = 0d;
            var count = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                units += GetCharacterWidthFactor(ch);
                count++;
            }

            if (count == 0)
            {
                return 0.58;
            }

            return units / count;
        }

        private static double GetCharacterWidthFactor(char ch)
        {
            if (ch == ' ')
            {
                return 0.32;
            }

            if (IsEastAsianWide(ch))
            {
                return 1.0;
            }

            if (char.IsPunctuation(ch))
            {
                return 0.34;
            }

            if (char.IsDigit(ch))
            {
                return 0.55;
            }

            return 0.58;
        }

        private static bool IsEastAsianWide(char ch)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            return category == UnicodeCategory.OtherLetter && ch > 255;
        }

        private static double ClampFont(double value)
        {
            return Math.Clamp(value, MinAutoFontSize, MaxAutoFontSize);
        }

        private static double GetLetterSpacing(string text, double fontSize)
        {
            return 0;
        }

        private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Button)
            {
                return;
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
            ApplyClickThrough();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _subscribedViewModel.OnWindowClosed();
                _subscribedViewModel = null;
            }

            base.OnClosed(e);
        }
    }
}

