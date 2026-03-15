using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
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

        public ICommand CycleThemeModeCommand { get; }

        public event Action<ThemeModePreference>? ThemeModeChanged;
        public event Action<bool>? MainNavPaneStateChanged;

        public ThemeModePreference CurrentThemeMode
        {
            get => _currentThemeMode;
            private set
            {
                if (!SetProperty(ref _currentThemeMode, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(ThemeToggleIconValue));
                OnPropertyChanged(nameof(ThemeToggleToolTip));
                ThemeModeChanged?.Invoke(value);
            }
        }

        public bool IsMainNavPaneOpen
        {
            get => _isMainNavPaneOpen;
            private set
            {
                if (!SetProperty(ref _isMainNavPaneOpen, value))
                {
                    return;
                }

                MainNavPaneStateChanged?.Invoke(value);
            }
        }

        public string ThemeToggleIconValue => CurrentThemeMode switch
        {
            ThemeModePreference.Light => "fa-solid fa-sun",
            ThemeModePreference.Dark => "fa-solid fa-moon",
            _ => "fa-solid fa-circle-half-stroke"
        };

        public string ThemeToggleToolTip
        {
            get
            {
                var nextMode = GetNextThemeMode(CurrentThemeMode);
                return $"当前主题：{GetThemeModeDisplayName(CurrentThemeMode)}，点击切换到{GetThemeModeDisplayName(nextMode)}";
            }
        }

        public bool IsSettingsPageSelected => SelectedNavTag == NavTagSettings;

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

        public int LiveSidePanelTabIndex
        {
            get => _liveSidePanelTabIndex;
            set
            {
                if (!SetProperty(ref _liveSidePanelTabIndex, value))
                {
                    return;
                }

                if (value == 1 && !_isLiveInsightPanelLoaded)
                {
                    _isLiveInsightPanelLoaded = true;
                    OnPropertyChanged(nameof(IsLiveInsightPanelLoaded));
                    OnPropertyChanged(nameof(LiveInsightTabContent));
                }
            }
        }

        public bool IsLiveInsightPanelLoaded => _isLiveInsightPanelLoaded;

        public object? LiveInsightTabContent => _isLiveInsightPanelLoaded ? this : null;

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

        public bool IsFloatingInsightOpen
        {
            get => _isFloatingInsightOpen;
            private set
            {
                if (!SetProperty(ref _isFloatingInsightOpen, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(FloatingInsightButtonBackground));
                OnPropertyChanged(nameof(FloatingInsightButtonForeground));
            }
        }

        public object? FloatingInsightButtonBackground => IsFloatingInsightOpen
            ? new SolidColorBrush(Color.Parse("#FF8B5CF6"))
            : new SolidColorBrush(Color.Parse("#FFE5E7EB"));

        public object? FloatingInsightButtonForeground => IsFloatingInsightOpen
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

                if (!value)
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
            set
            {
                if (SetProperty(ref _statusMessage, value))
                {
                    // 同步到 InfoBar 通知
                    ShowInfoBar(value);
                }
            }
        }

        public string InfoBarMessage
        {
            get => _infoBarMessage;
            set => SetProperty(ref _infoBarMessage, value);
        }

        public bool IsInfoBarOpen
        {
            get => _isInfoBarOpen;
            set => SetProperty(ref _isInfoBarOpen, value);
        }

        public int InfoBarSeverity
        {
            get => _infoBarSeverity;
            set => SetProperty(ref _infoBarSeverity, value);
        }

        private System.Threading.CancellationTokenSource? _infoBarCts;

        private void ShowInfoBar(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            InfoBarMessage = message;

            // 根据消息内容推断严重级别（中文关键词匹配）
            if (message.Contains("失败") || message.Contains("错误") || message.Contains("异常"))
                InfoBarSeverity = 3; // Error
            else if (message.Contains("警告") || message.Contains("注意"))
                InfoBarSeverity = 2; // Warning
            else if (message.Contains("成功") || message.Contains("已完成") || message.Contains("已加载") || message.Contains("已打开") || message.Contains("已切换") || message.Contains("已停止") || message.Contains("已恢复") || message.Contains("已生效") || message.Contains("继续进行") || message.Contains("已导出") || message.Contains("已导入") || message.Contains("已复制"))
                InfoBarSeverity = 1; // Success
            else
                InfoBarSeverity = 0; // Informational

            IsInfoBarOpen = true;

            // 自动关闭：Error 5秒，其余 3秒
            _infoBarCts?.Cancel();
            _infoBarCts = new System.Threading.CancellationTokenSource();
            var token = _infoBarCts.Token;
            var delay = InfoBarSeverity == 3 ? 5000 : 3000;
            _ = AutoCloseInfoBarAsync(delay, token);
        }

        private async System.Threading.Tasks.Task AutoCloseInfoBarAsync(int delayMs, System.Threading.CancellationToken token)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(delayMs, token);
                IsInfoBarOpen = false;
            }
            catch (System.Threading.Tasks.TaskCanceledException) { }
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
        public const string NavTagBatch = "batch";
        public const string NavTagMedia = "media";
        public const string NavTagMediaV2 = "mediav2";
        public const string NavTagSettings = "settings";

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
                    OnPropertyChanged(nameof(IsSettingsPageSelected));
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
                    OnPropertyChanged(nameof(IsSettingsPageSelected));
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
            if (!EnsureRealtimeTranslationService(out var errorMessage))
            {
                StatusMessage = errorMessage;
                IsTranslating = false;
                ConfigVM.IsConfigurationEnabled = true;
                ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
                return;
            }

            var translationService = _translationService;
            if (translationService == null)
            {
                StatusMessage = "实时翻译服务初始化失败。";
                IsTranslating = false;
                ConfigVM.IsConfigurationEnabled = true;
                ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
                return;
            }

            var started = await translationService.StartTranslationAsync();
            if (!started)
            {
                IsTranslating = false;
                ConfigVM.IsConfigurationEnabled = true;

                ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
                return;
            }

            IsTranslating = true;
            ConfigVM.IsConfigurationEnabled = false;
            StatusMessage = "正在翻译...";

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopTranslationCommand).RaiseCanExecuteChanged();
        }

        private async Task UpdateTranslationConfigAsync(AzureSpeechConfig config)
        {
            _config = config;

            if (_translationService == null)
            {
                return;
            }

            if (!_realtimeTranslationServiceFactory.TryResolveConnectorFamily(config, out var desiredFamily, out _))
            {
                await _translationService.UpdateConfigAsync(config);
                return;
            }

            if (_translationService.ConnectorFamily == desiredFamily)
            {
                await _translationService.UpdateConfigAsync(config);
                return;
            }

            var shouldRestart = IsTranslating;
            try
            {
                if (shouldRestart)
                {
                    await _translationService.StopTranslationAsync();
                }
            }
            finally
            {
                DetachTranslationService(_translationService);
                _translationService = null;
            }

            if (!shouldRestart)
            {
                return;
            }

            if (!EnsureRealtimeTranslationService(out var errorMessage))
            {
                IsTranslating = false;
                ConfigVM.IsConfigurationEnabled = true;
                StatusMessage = errorMessage;
                return;
            }

            var started = await _translationService!.StartTranslationAsync();
            if (!started)
            {
                IsTranslating = false;
                ConfigVM.IsConfigurationEnabled = true;
            }
        }

        private bool EnsureRealtimeTranslationService(out string errorMessage)
        {
            errorMessage = string.Empty;

            if (_translationService != null &&
                _realtimeTranslationServiceFactory.TryResolveConnectorFamily(_config, out var desiredFamily, out _) &&
                _translationService.ConnectorFamily == desiredFamily)
            {
                return true;
            }

            if (_translationService != null)
            {
                DetachTranslationService(_translationService);
                _translationService = null;
            }

            if (!_realtimeTranslationServiceFactory.TryCreate(_config, AppendAudioStreamAuditLog, out var service, out errorMessage) ||
                service == null)
            {
                return false;
            }

            _translationService = service;
            AttachTranslationService(service);
            return true;
        }

        private void AttachTranslationService(IRealtimeTranslationService service)
        {
            service.OnRealtimeTranslationReceived += OnRealtimeTranslationReceived;
            service.OnFinalTranslationReceived += OnFinalTranslationReceived;
            service.OnStatusChanged += OnStatusChanged;
            service.OnReconnectTriggered += OnReconnectTriggered;
            service.OnAudioLevelUpdated += OnAudioLevelUpdated;
            service.OnDiagnosticsUpdated += OnDiagnosticsUpdated;
        }

        private void DetachTranslationService(IRealtimeTranslationService service)
        {
            service.OnRealtimeTranslationReceived -= OnRealtimeTranslationReceived;
            service.OnFinalTranslationReceived -= OnFinalTranslationReceived;
            service.OnStatusChanged -= OnStatusChanged;
            service.OnReconnectTriggered -= OnReconnectTriggered;
            service.OnAudioLevelUpdated -= OnAudioLevelUpdated;
            service.OnDiagnosticsUpdated -= OnDiagnosticsUpdated;
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
                    CurrentTranslated = TrimRealtimeDisplayText($"{CurrentTranslated} {marker}");
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

        /// <summary>导航到设置页（取代原来的模态弹窗）</summary>
        private Task ShowConfig()
        {
            NavigateToSettings?.Invoke();
            return Task.CompletedTask;
        }

        /// <summary>由 MainWindow 注册的导航回调，切换到 Settings 页</summary>
        public Action? NavigateToSettings { get; set; }

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

        private void ShowMediaStudio()
        {
            // Media Studio is now embedded as a page in the main window.
            // This method is kept for backward compatibility with ShowMediaStudioCommand.
        }

        private void SyncThemeModeFromConfig()
        {
            CurrentThemeMode = _config.ThemeMode;
        }

        public void ApplyStartupShellPreferences(ShellStartupPreferences preferences)
        {
            CurrentThemeMode = preferences.ThemeMode;
            IsMainNavPaneOpen = preferences.IsMainNavPaneOpen;
        }

        private void SyncMainNavPaneStateFromConfig()
        {
            IsMainNavPaneOpen = _config.IsMainNavPaneOpen;
        }

        public void UpdateMainNavPaneState(bool isOpen)
        {
            IsMainNavPaneOpen = isOpen;

            if (_config.IsMainNavPaneOpen == isOpen)
            {
                return;
            }

            _config.IsMainNavPaneOpen = isOpen;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _configService.SaveConfigAsync(_config).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        private void CycleThemeMode()
        {
            var nextMode = GetNextThemeMode(CurrentThemeMode);
            CurrentThemeMode = nextMode;
            _config.ThemeMode = nextMode;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _configService.SaveConfigAsync(_config).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = $"主题设置保存失败: {ex.Message}";
                    });
                }
            });

            StatusMessage = $"主题已切换为{GetThemeModeDisplayName(nextMode)}";
        }

        private static ThemeModePreference GetNextThemeMode(ThemeModePreference currentMode)
        {
            return currentMode switch
            {
                ThemeModePreference.System => ThemeModePreference.Light,
                ThemeModePreference.Light => ThemeModePreference.Dark,
                _ => ThemeModePreference.System
            };
        }

        private static string GetThemeModeDisplayName(ThemeModePreference mode)
        {
            return mode switch
            {
                ThemeModePreference.Light => "浅色",
                ThemeModePreference.Dark => "深色",
                _ => "跟随系统"
            };
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

        private void ShowFloatingInsight()
        {
            try
            {
                if (_floatingInsightManager == null)
                {
                    _floatingInsightManager = new FloatingInsightManager(AiInsight);
                    _floatingInsightManager.WindowStateChanged += (_, isOpen) => IsFloatingInsightOpen = isOpen;
                }

                _floatingInsightManager.ToggleWindow();
                IsFloatingInsightOpen = _floatingInsightManager.IsWindowOpen;

                StatusMessage = _floatingInsightManager.IsWindowOpen
                    ? "浮动洞察窗口已打开"
                    : "浮动洞察窗口已关闭";
            }
            catch (Exception ex)
            {
                StatusMessage = $"浮动洞察窗口操作失败: {ex.Message}";
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
                CurrentOriginal = TrimRealtimeDisplayText(item.OriginalText);
                CurrentTranslated = TrimRealtimeDisplayText(item.TranslatedText);

                if (_floatingSubtitleManager?.IsWindowOpen == true && !string.IsNullOrEmpty(CurrentTranslated))
                {
                    _floatingSubtitleManager.UpdateSubtitle(CurrentTranslated);
                }
            });
        }

        private string TrimRealtimeDisplayText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var limit = Math.Clamp(_config.RealtimeMaxLength, 40, 4000);
            if (text.Length <= limit)
            {
                return text;
            }

            return text[^limit..];
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
