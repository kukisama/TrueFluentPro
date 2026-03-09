using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    public partial class FloatingInsightWindow : Window
    {
        public FloatingInsightWindow()
        {
            InitializeComponent();
            SetInitialPosition();
        }

        public FloatingInsightWindow(FloatingInsightViewModel viewModel) : this()
        {
            DataContext = viewModel;
            ApplyThemeDefault(viewModel);
        }

        private void ApplyThemeDefault(FloatingInsightViewModel vm)
        {
            // 浅色主题→白底黑字(mode 2)，深色主题→黑底白字(mode 1)
            var isDark = ActualThemeVariant == ThemeVariant.Dark;
            vm.BackgroundMode = isDark ? 1 : 2;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is FloatingInsightViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                ApplyMarkdownStyle(vm);
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not FloatingInsightViewModel vm) return;

            if (e.PropertyName is nameof(FloatingInsightViewModel.FontSize)
                              or nameof(FloatingInsightViewModel.TextBrush)
                              or nameof(FloatingInsightViewModel.BackgroundMode))
            {
                ApplyMarkdownStyle(vm);
            }
        }

        private void ApplyMarkdownStyle(FloatingInsightViewModel vm)
        {
            var viewer = InsightMarkdownViewer;
            if (viewer == null) return;

            // Markdown.Avalonia 内部 TextBlock 有自己的样式绑定，
            // 直接改 FontSize 或遍历可视树都不可靠。
            // 用 LayoutTransformControl + ScaleTransform 缩放整个内容区域。
            double scale = vm.FontSize / 14.0;
            MarkdownScaleContainer.LayoutTransform = new ScaleTransform(scale, scale);
            viewer.SetValue(TemplatedControl.ForegroundProperty, vm.TextBrush);
            viewer.SetValue(TemplatedControl.BackgroundProperty, Brushes.Transparent);
        }

        private void SetInitialPosition()
        {
            if (Screens.Primary != null)
            {
                var screen = Screens.Primary.WorkingArea;
                var windowWidth = 520;
                var windowHeight = 400;

                Position = new PixelPoint(
                    (int)(screen.X + screen.Width - windowWidth - 40),
                    (int)(screen.Y + (screen.Height - windowHeight) / 2)
                );
            }
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.BeginMoveDrag(e);
                e.Handled = true;
            }
            else if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                if (DataContext is FloatingInsightViewModel vm)
                    vm.ToggleBackground();
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

        private void OnContentPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                if (DataContext is FloatingInsightViewModel vm)
                    vm.ToggleBackground();
                e.Handled = true;
            }
        }

        private void OnIncreaseFontSize(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingInsightViewModel vm)
            {
                vm.IncreaseFontSize();
            }
        }

        private void OnDecreaseFontSize(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingInsightViewModel vm)
            {
                vm.DecreaseFontSize();
            }
        }

        private void OnToggleBackground(object? sender, RoutedEventArgs e)
        {
            if (DataContext is FloatingInsightViewModel vm)
            {
                vm.ToggleBackground();
            }
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is FloatingInsightViewModel viewModel)
            {
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                viewModel.OnWindowClosed();
            }

            base.OnClosed(e);
        }
    }
}
