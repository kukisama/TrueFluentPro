using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;

namespace TrueFluentPro.ViewModels
{
    public partial class MainWindowViewModel
    {
        public string CurrentOriginal
        {
            get => _currentOriginal;
            set
            {
                if (SetProperty(ref _currentOriginal, value))
                {
                    OnPropertyChanged(nameof(DisplayedText));
                }
            }
        }

        public string CurrentTranslated
        {
            get => _currentTranslated;
            set
            {
                if (SetProperty(ref _currentTranslated, value))
                {
                    OnPropertyChanged(nameof(DisplayedText));
                }
            }
        }

        public EditorDisplayMode EditorDisplayMode
        {
            get => _editorDisplayMode;
            set
            {
                if (!SetProperty(ref _editorDisplayMode, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsOriginalView));
                OnPropertyChanged(nameof(IsTranslatedView));
                OnPropertyChanged(nameof(IsBilingualView));
                OnPropertyChanged(nameof(IsSingleView));
                OnPropertyChanged(nameof(DisplayedText));
                OnPropertyChanged(nameof(DisplayPlaceholder));
            }
        }

        public bool IsOriginalView
        {
            get => _editorDisplayMode == EditorDisplayMode.Original;
            set
            {
                if (value)
                {
                    EditorDisplayMode = EditorDisplayMode.Original;
                }
            }
        }

        public bool IsTranslatedView
        {
            get => _editorDisplayMode == EditorDisplayMode.Translated;
            set
            {
                if (value)
                {
                    EditorDisplayMode = EditorDisplayMode.Translated;
                }
            }
        }

        public bool IsBilingualView
        {
            get => _editorDisplayMode == EditorDisplayMode.Bilingual;
            set
            {
                if (value)
                {
                    EditorDisplayMode = EditorDisplayMode.Bilingual;
                }
            }
        }

        public bool IsSingleView => _editorDisplayMode != EditorDisplayMode.Bilingual;

        public string DisplayedText
        {
            get => _editorDisplayMode == EditorDisplayMode.Original ? CurrentOriginal : CurrentTranslated;
            set
            {
                if (_editorDisplayMode == EditorDisplayMode.Original)
                {
                    CurrentOriginal = value;
                }
                else if (_editorDisplayMode == EditorDisplayMode.Translated)
                {
                    CurrentTranslated = value;
                }
            }
        }

        public string DisplayPlaceholder => _editorDisplayMode == EditorDisplayMode.Original
            ? "原文将在这里显示..."
            : _editorDisplayMode == EditorDisplayMode.Translated
                ? "译文将在这里显示..."
                : "双语将在这里显示...";

        public bool IsFloatingSubtitleOpen
        {
            get => _isFloatingSubtitleOpen;
            private set
            {
                if (!SetProperty(ref _isFloatingSubtitleOpen, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(FloatingSubtitleButtonBackground));
                OnPropertyChanged(nameof(FloatingSubtitleButtonForeground));
            }
        }

        public object? FloatingSubtitleButtonBackground => IsFloatingSubtitleOpen
            ? new SolidColorBrush(Color.Parse("#FF10B981"))
            : new SolidColorBrush(Color.Parse("#FFE5E7EB"));

        public object? FloatingSubtitleButtonForeground => IsFloatingSubtitleOpen
            ? Brushes.White
            : new SolidColorBrush(Color.Parse("#FF111827"));

        public bool IsTranslating
        {
            get => _isTranslating;
            set
            {
                if (!SetProperty(ref _isTranslating, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(TranslationToggleButtonText));
                OnPropertyChanged(nameof(TranslationToggleButtonBackground));
                OnPropertyChanged(nameof(TranslationToggleButtonForeground));
                AudioDevices.NotifyTranslatingChanged();

                if (value)
                {
                    // 翻译进行中允许实时切换输入/输出设备，仅禁用刷新
                    AudioDevices.IsAudioDeviceRefreshEnabled = false;
                    AudioDevices.NotifyTranslatingChanged();
                }
                else
                {
                    AudioDevices.RefreshAudioDevices(persistSelection: false);
                }

                ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
            }
        }

        public string TranslationToggleButtonText => IsTranslating ? "停止翻译" : "开始翻译";

        public IBrush TranslationToggleButtonBackground => IsTranslating ? Brushes.Red : Brushes.Green;

        public IBrush TranslationToggleButtonForeground => Brushes.White;

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string AudioDiagnosticStatus
        {
            get => _audioDiagnosticStatus;
            set => SetProperty(ref _audioDiagnosticStatus, value);
        }

        public ObservableCollection<TranslationItem> History
        {
            get => _history;
            set => SetProperty(ref _history, value);
        }

        public const string NavTagLive = "live";
        public const string NavTagReview = "review";

        private string _selectedNavTag = NavTagLive;

        public string SelectedNavTag
        {
            get => _selectedNavTag;
            set
            {
                if (SetProperty(ref _selectedNavTag, value))
                {
                    _uiModeIndex = value == NavTagReview ? 1 : 0;

                    if (value == NavTagReview && !_isReviewModeViewCreated)
                    {
                        _isReviewModeViewCreated = true;
                    }

                    OnPropertyChanged(nameof(UiModeIndex));
                    OnPropertyChanged(nameof(ReviewModeViewContent));
                    OnPropertyChanged(nameof(IsLiveMode));
                    OnPropertyChanged(nameof(IsReviewMode));
                    OnPropertyChanged(nameof(IsLiveModeSelected));
                    OnPropertyChanged(nameof(IsReviewModeSelected));
                }
            }
        }

        public int UiModeIndex
        {
            get => _uiModeIndex;
            set
            {
                if (SetProperty(ref _uiModeIndex, value))
                {
                    _selectedNavTag = value == 1 ? NavTagReview : NavTagLive;
                    OnPropertyChanged(nameof(SelectedNavTag));

                    if (value == 1 && !_isReviewModeViewCreated)
                    {
                        _isReviewModeViewCreated = true;
                        OnPropertyChanged(nameof(ReviewModeViewContent));
                    }

                    OnPropertyChanged(nameof(IsLiveMode));
                    OnPropertyChanged(nameof(IsReviewMode));
                    OnPropertyChanged(nameof(IsLiveModeSelected));
                    OnPropertyChanged(nameof(IsReviewModeSelected));
                }
            }
        }

        public object? ReviewModeViewContent => _isReviewModeViewCreated ? this : null;

        public bool IsLiveMode => UiModeIndex == 0;

        public bool IsReviewMode => UiModeIndex == 1;

        public bool IsLiveModeSelected
        {
            get => UiModeIndex == 0;
            set
            {
                if (value)
                {
                    UiModeIndex = 0;
                }
            }
        }

        public bool IsReviewModeSelected
        {
            get => UiModeIndex == 1;
            set
            {
                if (value)
                {
                    UiModeIndex = 1;
                }
            }
        }

        public TextEditorType EditorType
        {
            get => _editorType;
            set => SetProperty(ref _editorType, value);
        }

        private async void StartTranslation()
        {
            if (_translationService == null)
            {
                _translationService = new SpeechTranslationService(
                    _config,
                    AppendAudioStreamAuditLog);
                _translationService.OnRealtimeTranslationReceived += OnRealtimeTranslationReceived;
                _translationService.OnFinalTranslationReceived += OnFinalTranslationReceived;
                _translationService.OnStatusChanged += OnStatusChanged;
                _translationService.OnReconnectTriggered += OnReconnectTriggered;
                _translationService.OnAudioLevelUpdated += OnAudioLevelUpdated;
                _translationService.OnDiagnosticsUpdated += OnDiagnosticsUpdated;
            }

            await _translationService.StartTranslationAsync();
            IsTranslating = true;
            ConfigVM.IsConfigurationEnabled = false;
            StatusMessage = "正在翻译...";

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
        }

        private async void StopTranslation()
        {
            if (_translationService != null)
            {
                await _translationService.StopTranslationAsync();
            }

            IsTranslating = false;
            ConfigVM.IsConfigurationEnabled = true;
            StatusMessage = "已停止";
            AudioDiagnosticStatus = "诊断: 已停止";
            AudioDevices.SetAudioLevel(0);
            AudioDevices.ResetAudioLevelHistory();

            if (_floatingSubtitleManager?.IsWindowOpen == true)
            {
                _floatingSubtitleManager.CloseWindow();
                StatusMessage = "已停止，浮动字幕窗口已关闭";
            }

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
        }

        private void OnReconnectTriggered(object? sender, string reason)
        {
            if (!_config.ShowReconnectMarkerInSubtitle)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                var marker = "*触发重连*";
                if (string.IsNullOrWhiteSpace(CurrentTranslated))
                {
                    CurrentTranslated = marker;
                }
                else if (!CurrentTranslated.Contains(marker, StringComparison.Ordinal))
                {
                    CurrentTranslated = $"{CurrentTranslated} {marker}";
                }

                if (_floatingSubtitleManager?.IsWindowOpen == true)
                {
                    _floatingSubtitleManager.UpdateSubtitle(CurrentTranslated);
                }
            });
        }

        private void ClearHistory()
        {
            History.Clear();

            CurrentOriginal = "";
            CurrentTranslated = "";

            ((RelayCommand)ClearHistoryCommand).RaiseCanExecuteChanged();
        }

        private async Task ShowConfig()
        {
            if (_mainWindow == null)
                return;

            var configView = new ConfigCenterView(_config);

            configView.ConfigurationUpdated += OnConfigurationUpdated;

            var result = await configView.ShowDialog<bool>(_mainWindow);

            configView.ConfigurationUpdated -= OnConfigurationUpdated;

            if (result)
            {
                _config = configView.Config;
                ConfigVM.SetConfig(_config);

                try
                {
                    await _configService.SaveConfigAsync(_config);
                    StatusMessage = $"配置已保存到: {ConfigVM.GetConfigFilePath()}";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"保存配置失败: {ex.Message}";
                }

                if (_translationService != null)
                {
                    await _translationService.UpdateConfigAsync(_config);
                }

                AiInsight.UpdateConfig();
                BatchProcessing.RebuildReviewSheets();
                BatchProcessing.RefreshCommandStates();
                ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            }
        }

        private void OpenHistoryFolder()
        {
            try
            {
                var historyDirectory = _config.SessionDirectory;

                if (!System.IO.Directory.Exists(historyDirectory))
                {
                    System.IO.Directory.CreateDirectory(historyDirectory);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = historyDirectory,
                    UseShellExecute = true
                });

                StatusMessage = $"已打开历史记录文件夹: {historyDirectory}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开历史记录文件夹失败: {ex.Message}";
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                StatusMessage = $"已打开链接: {url}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开链接失败: {ex.Message}";
            }
        }

        private static string LoadAppVersion()
        {
            try
            {
                // Try next to executable first, then project root (dev scenario).
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Assets", "RELEASE_NOTES.md"),
                    Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md"),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "RELEASE_NOTES.md"),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RELEASE_NOTES.md")
                };

                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    var firstLine = File.ReadLines(path).FirstOrDefault() ?? "";
                    var match = Regex.Match(firstLine, @"v(\d+\.\d+\.\d+)");
                    if (match.Success)
                        return $"版本 {match.Groups[1].Value}";
                }
            }
            catch
            {
                // ignore
            }

            return "版本 未知";
        }

        private async Task ShowAbout()
        {
            try
            {
                var owner = _mainWindow
                    ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

                var about = new AboutView();
                if (owner != null)
                {
                    await about.ShowDialog(owner);
                }
                else
                {
                    about.Show();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Console.Error.WriteLine(ex);
                StatusMessage = $"打开关于失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private async Task ShowHelp()
        {
            try
            {
                var owner = _mainWindow
                    ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

                var help = new HelpView();
                if (owner != null)
                {
                    await help.ShowDialog(owner);
                }
                else
                {
                    help.Show();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Console.Error.WriteLine(ex);
                StatusMessage = $"打开说明失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private void ShowMediaStudio()
        {
            // Media Studio is now embedded as a page in the main window.
            // This method is kept for backward compatibility with ShowMediaStudioCommand.
        }

        private void ShowFloatingSubtitles()
        {
            try
            {
                if (_floatingSubtitleManager == null)
                {
                    _floatingSubtitleManager = new FloatingSubtitleManager();
                    _floatingSubtitleManager.WindowStateChanged += (_, isOpen) => IsFloatingSubtitleOpen = isOpen;
                }

                _floatingSubtitleManager.ToggleWindow();
                IsFloatingSubtitleOpen = _floatingSubtitleManager.IsWindowOpen;

                if (_floatingSubtitleManager.IsWindowOpen)
                {
                    StatusMessage = "浮动字幕窗口已打开";

                    if (!string.IsNullOrEmpty(CurrentTranslated))
                    {
                        _floatingSubtitleManager.UpdateSubtitle(CurrentTranslated);
                    }
                }
                else
                {
                    StatusMessage = "浮动字幕窗口已关闭";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"浮动字幕窗口操作失败: {ex.Message}";
            }
        }

        private void ToggleEditorType()
        {
            EditorType = EditorType == TextEditorType.Simple
                ? TextEditorType.Advanced
                : TextEditorType.Simple;

            StatusMessage = $"已切换到 {(EditorType == TextEditorType.Simple ? "简单" : "高级")} 编辑器";
        }

        private void OnRealtimeTranslationReceived(object? sender, TranslationItem item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                CurrentOriginal = item.OriginalText ?? "";
                CurrentTranslated = item.TranslatedText ?? "";

                if (_floatingSubtitleManager?.IsWindowOpen == true && !string.IsNullOrEmpty(CurrentTranslated))
                {
                    _floatingSubtitleManager.UpdateSubtitle(CurrentTranslated);
                }
            });
        }

        private void OnFinalTranslationReceived(object? sender, TranslationItem item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                History.Insert(0, item);

                while (History.Count > _config.MaxHistoryItems)
                {
                    History.RemoveAt(History.Count - 1);
                }

                ((RelayCommand)ClearHistoryCommand).RaiseCanExecuteChanged();

                AiInsight.OnNewDataAutoInsight();
            });
        }

        private void OnStatusChanged(object? sender, string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusMessage = message;
            });
        }

        private void OnDiagnosticsUpdated(object? sender, string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AudioDiagnosticStatus = message;
            });
        }

        private void OnAudioLevelUpdated(object? sender, double level)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AudioDevices.SetAudioLevel(level);
            });
        }

        private void AppendAudioStreamAuditLog(string message)
        {
            var eventName = message.StartsWith("[翻译流]", StringComparison.Ordinal)
                ? "翻译流"
                : message.StartsWith("[录制流]", StringComparison.Ordinal)
                    ? "录制流"
                    : "音频流";

            AppLogService.Instance.LogAudit(eventName, message);
        }
    }
}
