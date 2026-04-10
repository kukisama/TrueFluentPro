using System;
using TrueFluentPro.Views;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Services
{
    public class FloatingSubtitleManager
    {
        private FloatingSubtitleWindow? _window;
        private FloatingSubtitleViewModel? _viewModel;
        private SubtitleSyncService? _syncService;
        private bool _isWindowOpen = false;
        private readonly VadGateController.ActiveSource _sourceFilter;

        public bool IsWindowOpen => _isWindowOpen;

        /// <summary>该管理器关注的音频来源（None=不筛选）。</summary>
        public VadGateController.ActiveSource SourceFilter => _sourceFilter;

        public event EventHandler<bool>? WindowStateChanged;

        public FloatingSubtitleManager(
            VadGateController.ActiveSource sourceFilter = VadGateController.ActiveSource.None)
        {
            _sourceFilter = sourceFilter;
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
                _syncService = new SubtitleSyncService();

                _viewModel = new FloatingSubtitleViewModel(_syncService, _sourceFilter);

                _window = new FloatingSubtitleWindow(_viewModel);

                _window.Closed += OnWindowClosed;

                _window.Show();
                _isWindowOpen = true;
                WindowStateChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open floating subtitle window: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"Failed to close floating subtitle window: {ex.Message}");
            }
        }

        public void UpdateSubtitle(string subtitle)
        {
            if (_isWindowOpen && _syncService != null)
            {
                _syncService.UpdateSubtitle(subtitle);
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
            _syncService = null;
        }

        public void Dispose()
        {
            CloseWindow();
        }
    }
}

