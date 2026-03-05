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
            else if (message.Contains("成功") || message.Contains("已完成") || message.Contains("已加载") || message.Contains("已打开") || message.Contains("已切换") || message.Contains("已停止"))
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
        public const string NavTagMedia = "media";
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

        private UpdateInfo? _latestUpdate;

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set
            {
                if (SetProperty(ref _isUpdateAvailable, value))
                    RaiseUpdateCommandsCanExecuteChanged();
            }
        }

        public string UpdateVersionText
        {
            get => _updateVersionText;
            set => SetProperty(ref _updateVersionText, value);
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set
            {
                if (SetProperty(ref _isDownloading, value))
                    RaiseUpdateCommandsCanExecuteChanged();
            }
        }

        public double DownloadProgress
        {
            get => _downloadProgress;
            set => SetProperty(ref _downloadProgress, value);
        }

        private void RaiseUpdateCommandsCanExecuteChanged()
        {
            (CheckForUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadAndApplyUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task CheckForUpdateAsync(bool silent)
        {
            try
            {
                // silent=true 来自后台自动检查，受设置控制；silent=false 来自手动按钮，始终执行
                if (silent && !Settings.IsAutoUpdateEnabled)
                    return;

                var currentVersion = UpdateService.ParseCurrentVersion();
                var info = await _updateService.CheckForUpdateAsync(currentVersion);

                if (info == null)
                {
                    if (!silent)
                        StatusMessage = "当前已是最新版本";
                    return;
                }

                _latestUpdate = info;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateVersionText = $"发现新版本 v{info.LatestVersion}";
                    IsUpdateAvailable = true;
                });

                if (silent && !string.IsNullOrEmpty(info.DownloadUrl))
                {
                    // 自动更新模式：静默下载，完成后弹窗确认
                    await SilentDownloadAndPromptAsync();
                }
                else if (!silent)
                {
                    // 手动检查：跳转设置页展示更新面板
                    StatusMessage = $"发现新版本 v{info.LatestVersion}，请在关于中查看";
                }
            }
            catch
            {
                if (!silent)
                    StatusMessage = "检查更新失败";
            }
        }

        /// <summary>
        /// 自动更新：静默下载 → 弹窗确认 → 启动 Updater 并退出
        /// </summary>
        private async Task SilentDownloadAndPromptAsync()
        {
            if (_latestUpdate == null || string.IsNullOrEmpty(_latestUpdate.DownloadUrl))
                return;

            try
            {
                IsDownloading = true;
                DownloadProgress = 0;

                var progress = new Progress<double>(p =>
                    Dispatcher.UIThread.Post(() => DownloadProgress = p));

                var zipPath = await _updateService.DownloadUpdateAsync(_latestUpdate.DownloadUrl, _latestUpdate.AssetSize, progress);

                if (string.IsNullOrEmpty(zipPath))
                    return; // 静默失败，不打扰用户

                DownloadProgress = 1.0;

                // 弹窗确认
                var confirmed = await Dispatcher.UIThread.InvokeAsync(() => ShowUpdateReadyDialog());

                if (confirmed)
                {
                    if (!_updateService.LaunchUpdaterAndExit(zipPath))
                    {
                        StatusMessage = $"未找到 Updater.exe，更新包已下载到: {zipPath}";
                    }
                }
            }
            catch
            {
                // 静默失败
            }
            finally
            {
                IsDownloading = false;
            }
        }

        /// <summary>
        /// 显示"更新已准备好"确认弹窗，返回用户是否点击了确定。
        /// </summary>
        private async Task<bool> ShowUpdateReadyDialog()
        {
            var owner = _mainWindow
                ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null) return false;

            var result = false;
            var dialog = new Window
            {
                Title = "更新准备就绪",
                Width = 400,
                Height = 180,
                CanResize = false,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                Content = new Avalonia.Controls.StackPanel
                {
                    Margin = new Avalonia.Thickness(24),
                    Spacing = 16,
                    Children =
                    {
                        new Avalonia.Controls.TextBlock
                        {
                            Text = $"新版本 v{_latestUpdate?.LatestVersion} 已下载完成。\n点击「确定」将关闭程序并开始更新。",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            FontSize = 14
                        },
                        new Avalonia.Controls.StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                new Avalonia.Controls.Button { Content = "稍后再说", Padding = new Avalonia.Thickness(16, 6) },
                                new Avalonia.Controls.Button { Content = "确定", Padding = new Avalonia.Thickness(16, 6), Classes = { "accent" } }
                            }
                        }
                    }
                }
            };

            var buttons = ((Avalonia.Controls.StackPanel)((Avalonia.Controls.StackPanel)dialog.Content).Children[1]);
            ((Avalonia.Controls.Button)buttons.Children[0]).Click += (_, _) => { result = false; dialog.Close(); };
            ((Avalonia.Controls.Button)buttons.Children[1]).Click += (_, _) => { result = true; dialog.Close(); };

            await dialog.ShowDialog(owner);
            return result;
        }

        private async Task DownloadAndApplyUpdateAsync()
        {
            if (_latestUpdate == null || string.IsNullOrEmpty(_latestUpdate.DownloadUrl))
            {
                StatusMessage = "无可用的下载地址，请前往 GitHub 手动下载";
                OpenUrl(_latestUpdate?.ReleasePageUrl ?? "https://github.com/kukisama/TrueFluentPro/releases");
                return;
            }

            try
            {
                IsDownloading = true;
                DownloadProgress = 0;
                StatusMessage = "正在下载更新...";

                var progress = new Progress<double>(p =>
                    Dispatcher.UIThread.Post(() => DownloadProgress = p));

                var zipPath = await _updateService.DownloadUpdateAsync(_latestUpdate.DownloadUrl, _latestUpdate.AssetSize, progress);

                if (string.IsNullOrEmpty(zipPath))
                {
                    StatusMessage = "下载更新失败，请检查网络连接";
                    return;
                }

                DownloadProgress = 1.0;
                StatusMessage = "下载完成，正在启动更新...";

                await Task.Delay(500);

                if (!_updateService.LaunchUpdaterAndExit(zipPath))
                {
                    StatusMessage = $"未找到 Updater.exe，更新包已下载到: {zipPath}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"更新失败: {ex.Message}";
            }
            finally
            {
                IsDownloading = false;
            }
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
