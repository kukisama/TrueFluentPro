using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            base.OnClosed(e);

            if (DataContext is FloatingInsightViewModel viewModel)
            {
                viewModel.OnWindowClosed();
            }
        }
    }
}
