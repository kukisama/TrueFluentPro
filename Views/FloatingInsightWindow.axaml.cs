using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

            if (e.PropertyName is nameof(FloatingInsightViewModel.FontSize))
            {
                ApplyMarkdownStyle(vm);
            }
        }

        private void ApplyMarkdownStyle(FloatingInsightViewModel vm)
        {
            if (MarkdownScaleContainer == null) return;

            double scale = vm.FontSize / 14.0;
            MarkdownScaleContainer.LayoutTransform = new ScaleTransform(scale, scale);
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
