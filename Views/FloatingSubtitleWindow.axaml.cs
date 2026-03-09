using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{    public partial class FloatingSubtitleWindow : Window
    {
        private const double MinAutoFontSize = 26;
        private const double MaxAutoFontSize = 52;
        private const double LineHeightScale = 1.14;
        private const int LatinTargetDisplayUnits = 34;
        private const int CjkTargetDisplayUnits = 22;
        private readonly TranslateTransform _subtitleTranslate = new();
        private FloatingSubtitleViewModel? _subscribedViewModel;

        public FloatingSubtitleWindow()
        {
            InitializeComponent();

            SubtitleTextBlock.RenderTransform = _subtitleTranslate;
            
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

            var availableWidth = Math.Max(420, SubtitleHost.Bounds.Width - SubtitleHost.Padding.Left - SubtitleHost.Padding.Right);
            var availableHeight = Math.Max(48, SubtitleHost.Bounds.Height - SubtitleHost.Padding.Top - SubtitleHost.Padding.Bottom);
            var targetDisplayUnits = GetTargetDisplayUnits(text);
            var fontSize = GetStableSingleLineFontSize(text, availableWidth, availableHeight, viewModel.FontScaleBias, targetDisplayUnits);
            var normalizedText = NormalizeText(text);

            viewModel.UpdateFormattedSubtitleText(normalizedText);
            SubtitleTextBlock.FontSize = fontSize;
            SubtitleTextBlock.LineHeight = Math.Round(fontSize * LineHeightScale, MidpointRounding.AwayFromZero);
            SubtitleTextBlock.LetterSpacing = GetLetterSpacing(text, fontSize);
            SubtitleTextBlock.Margin = CreateTextMargin(fontSize);

            var textWidth = MeasureSubtitleTextWidth(availableHeight);
            var overflowWidth = Math.Max(0, textWidth - availableWidth);
            var isOverflowing = overflowWidth > 0.5;

            SubtitleTextBlock.HorizontalAlignment = isOverflowing ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            SubtitleTextBlock.TextAlignment = isOverflowing ? TextAlignment.Left : TextAlignment.Center;
            _subtitleTranslate.X = isOverflowing ? -overflowWidth : 0;
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

