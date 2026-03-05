using System;
using TrueFluentPro.Views;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Services
{
    public class FloatingInsightManager
    {
        private FloatingInsightWindow? _window;
        private FloatingInsightViewModel? _viewModel;
        private readonly AiInsightViewModel _aiInsight;
        private bool _isWindowOpen;

        public bool IsWindowOpen => _isWindowOpen;

        public event EventHandler<bool>? WindowStateChanged;

        public FloatingInsightManager(AiInsightViewModel aiInsight)
        {
            _aiInsight = aiInsight;
        }

        public void ToggleWindow()
        {
            if (_isWindowOpen)
            {
                CloseWindow();
            }
            else
            {
                OpenWindow();
            }
        }

        public void OpenWindow()
        {
            if (_isWindowOpen) return;

            try
            {
                _viewModel = new FloatingInsightViewModel(_aiInsight);
                _window = new FloatingInsightWindow(_viewModel);
                _window.Closed += OnWindowClosed;
                _window.Show();
                _isWindowOpen = true;
                WindowStateChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open floating insight window: {ex.Message}");
            }
        }

        public void CloseWindow()
        {
            if (!_isWindowOpen || _window == null) return;

            try
            {
                _window.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to close floating insight window: {ex.Message}");
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _isWindowOpen = false;
            WindowStateChanged?.Invoke(this, false);

            if (_window != null)
            {
                _window.Closed -= OnWindowClosed;
                _window = null;
            }

            _viewModel = null;
        }

        public void Dispose()
        {
            CloseWindow();
        }
    }
}
