using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public partial class MainWindowViewModel
    {
        public void SetMainWindow(Window mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public void NotifyMainWindowShown()
        {
            _isMainWindowShown = true;
            TryStartPostShowInitialization();
        }

        private void RegisterPostShowInitializationAction(string name, Func<Task> action)
        {
            _postShowInitActions.Add((name, action));
        }

        private void MarkConfigLoaded()
        {
            _isConfigLoaded = true;
            TryStartPostShowInitialization();
        }

        private void TryStartPostShowInitialization()
        {
            if (!_isMainWindowShown || !_isConfigLoaded)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _postShowInitStarted, 1, 0) != 0)
            {
                return;
            }

            _ = RunPostShowInitializationAsync();
        }

        private async Task RunPostShowInitializationAsync()
        {
            var actions = _postShowInitActions.ToArray();
            if (actions.Length == 0)
            {
                return;
            }

            var tasks = actions.Select(action => RunPostShowInitializationActionAsync(action.Name, action.Action));
            await Task.WhenAll(tasks);
        }

        private async Task RunPostShowInitializationActionAsync(string actionName, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogAudit("PostShowInitFailed", $"action='{actionName}' error='{ex.Message}'", isSuccess: false);
            }
        }

        public void Dispose()
        {
            if (_translationService != null)
            {
                DetachTranslationService(_translationService);
            }

            _floatingSubtitleManager?.Dispose();
            _floatingMicSubtitleManager?.Dispose();
            _floatingLoopbackSubtitleManager?.Dispose();
            _floatingInsightManager?.Dispose();

            Playback?.Dispose();
            AiInsight?.Dispose();
            Settings?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
