using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.ComponentModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace TrueFluentPro.ViewModels
{
    public enum EditorDisplayMode
    {
        Original,
        Translated,
        Bilingual
    }

    public partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private AzureSpeechConfig _config;
        private bool _isTranslating = false;
        private string _statusMessage = "就绪";
        private string _audioDiagnosticStatus = "诊断: 未启动";
        private string _infoBarMessage = "";
        private bool _isInfoBarOpen = false;
        private int _infoBarSeverity = 0; // 0=Informational, 1=Success, 2=Warning, 3=Error
        private string _currentOriginal = "";
        private string _currentTranslated = "";
        private ObservableCollection<TranslationItem> _history;
        private IRealtimeTranslationService? _translationService;
        private Window? _mainWindow;
        private ConfigurationService _configService;
        private TextEditorType _editorType = TextEditorType.Advanced;
        private EditorDisplayMode _editorDisplayMode = EditorDisplayMode.Translated;
        private ThemeModePreference _currentThemeMode = ThemeModePreference.System;
        private bool _isMainNavPaneOpen;

        private readonly IAiInsightService _aiInsightService;
        private readonly IRealtimeTranslationServiceFactory _realtimeTranslationServiceFactory;
        public AudioDevicesViewModel AudioDevices { get; }
        public ConfigViewModel ConfigVM { get; }
        public FileLibraryViewModel FileLibrary { get; }
        public BatchProcessingViewModel BatchProcessing { get; }
        public SettingsViewModel Settings { get; }

        private bool _isFloatingSubtitleOpen;

        private FloatingSubtitleManager? _floatingSubtitleManager;
        private FloatingInsightManager? _floatingInsightManager;
        private bool _isFloatingInsightOpen;
        private int _liveSidePanelTabIndex;
        private bool _isLiveInsightPanelLoaded;

        private int _uiModeIndex;
        private bool _isReviewModeViewCreated;

        private readonly List<(string Name, Func<Task> Action)> _postShowInitActions = new();
        private int _postShowInitStarted;
        private volatile bool _isMainWindowShown;
        private volatile bool _isConfigLoaded;
        private string? _pendingReviewAudioPath;

        private int _pendingReviewSequence;
        private int _lastDispatchedReviewSequence;

        public MainWindowViewModel(
            ConfigurationService configService,
            IAzureTokenProviderStore azureTokenProviderStore,
            AzureSubscriptionValidator subscriptionValidator,
            SettingsViewModel settingsViewModel,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IRealtimeTranslationServiceFactory realtimeTranslationServiceFactory,
            IBatchPackageStateService batchPackageStateService,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            IAiInsightService aiInsightService)
        {
            _configService = configService;
            _realtimeTranslationServiceFactory = realtimeTranslationServiceFactory;
            Settings = settingsViewModel;
            Settings.StatusNotificationRequested += message => StatusMessage = message;
            _aiInsightService = aiInsightService;
            _config = new AzureSpeechConfig();
            AppLogService.Initialize(() => _config.BatchLogLevel);
            _history = new ObservableCollection<TranslationItem>();

            ConfigVM = new ConfigViewModel(
                configService,
                subscriptionValidator,
                _config,
                () =>
                {
                    (StartTranslationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ToggleTranslationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                },
                QueueUpdateTranslationConfig,
                (eventName, message, isSuccess) => AppLogService.Instance.LogAudit(eventName, message, isSuccess),
                () => BatchProcessing?.IsReviewSummaryLoading ?? false,
                () => _mainWindow);

            ConfigVM.ConfigLoaded += OnConfigVMConfigLoaded;
            ConfigVM.ConfigUpdatedFromExternal += OnConfigVMConfigUpdatedFromExternal;

            Playback = new PlaybackViewModel(
                msg => StatusMessage = msg,
                () => FileLibrary?.SubtitleCues ?? new ObservableCollection<SubtitleCue>(),
                cue => { if (FileLibrary != null) FileLibrary.SelectedSubtitleCue = cue; },
                () => FileLibrary?.SelectedSubtitleCue);

            FileLibrary = new FileLibraryViewModel(
                () => _config,
                batchPackageStateService,
                msg => StatusMessage = msg,
                audioFile =>
                {
                    CrashLogger.AddBreadcrumb($"AudioSelected: {DescribeMediaFile(audioFile)}");
                    Playback.LoadAudioForPlayback(audioFile);

                    // 顺序执行策略：先完成字幕加载，再触发复盘加载。
                    _pendingReviewAudioPath = audioFile?.FullPath;
                    _pendingReviewSequence++;
                    CrashLogger.AddBreadcrumb($"BatchReviewQueuedAfterSubtitleLoad: seq={_pendingReviewSequence}, audio={DescribeMediaFile(audioFile)}");

                    CrashLogger.AddBreadcrumb($"AudioSelectionHandled: subtitleCues={FileLibrary?.SubtitleCues.Count ?? -1}, subtitleFiles={FileLibrary?.SubtitleFiles.Count ?? -1}, playbackReady={Playback?.IsStopEnabled ?? false}");
                },
                () => Playback?.SuppressSubtitleSeek ?? false,
                cue => { if (cue != null) Playback?.SeekToTime(cue.Start); });

            BatchProcessing = new BatchProcessingViewModel(
                () => _config,
                msg => StatusMessage = msg,
                modelRuntimeResolver,
                speechResourceRuntimeResolver,
                aiAudioTranscriptionService,
                _aiInsightService,
                FileLibrary,
                Playback,
                configService,
                batchPackageStateService,
                azureTokenProviderStore,
                () => ConfigVM.NotifyReviewLampChanged());

            FileLibrary.SubtitleCuesLoaded += OnFileLibrarySubtitleCuesLoaded;
            FileLibrary.AudioLibraryRefreshed += BatchProcessing.OnAudioLibraryRefreshed;

            AudioDevices = new AudioDevicesViewModel(
                () => _config,
                msg => StatusMessage = msg,
                () => IsTranslating,
                QueueUpdateTranslationConfig,
                () => _translationService?.TryApplyLiveAudioRoutingFromCurrentConfig() ?? false,
                reason => ConfigVM.QueueConfigSave(reason),
                (eventName, message) => AppLogService.Instance.LogAudit(eventName, message),
                () => _mainWindow);

            RegisterPostShowInitializationAction(
                "SubscriptionValidation",
                async () => await Dispatcher.UIThread.InvokeAsync(() => ConfigVM.TriggerSubscriptionValidation()));
            RegisterPostShowInitializationAction(
                "AudioDevicesRefresh",
                async () => await AudioDevices.RefreshAudioDevicesAsync(persistSelection: false));
            RegisterPostShowInitializationAction(
                "AudioLibraryRefresh",
                async () => await FileLibrary.RefreshAudioLibraryAsync());

            AiInsight = new AiInsightViewModel(
                _aiInsightService,
                azureTokenProviderStore,
                modelRuntimeResolver,
                () => _config,
                () => _history,
                msg => StatusMessage = msg,
                ShowConfig);

            RegisterPostShowInitializationAction(
                "AiSilentLogin",
                async () => await AiInsight.TrySilentLoginForAiAsync());

            CrashLogger.SetContextProvider(BuildCrashContextSnapshot);
            CrashLogger.AddBreadcrumb("MainWindowViewModel initialized");

            StartTranslationCommand = new RelayCommand(
                execute: _ => StartTranslation(),
                canExecute: _ => !IsTranslating && _config.IsValid()
            );

            StopTranslationCommand = new RelayCommand(
                execute: _ => StopTranslation(),
                canExecute: _ => IsTranslating
            );

            ToggleTranslationCommand = new RelayCommand(
                execute: _ =>
                {
                    if (IsTranslating)
                    {
                        StopTranslation();
                        return;
                    }

                    StartTranslation();
                },
                canExecute: _ => IsTranslating || _config.IsValid()
            );

            ClearHistoryCommand = new RelayCommand(
                execute: _ => ClearHistory(),
                canExecute: _ => History.Count > 0
            );
            ShowConfigCommand = new RelayCommand(
                execute: async _ => await ShowConfig(),
                canExecute: _ => true
            );

            OpenHistoryFolderCommand = new RelayCommand(
                execute: _ => OpenHistoryFolder(),
                canExecute: _ => true
            );              ShowFloatingSubtitlesCommand = new RelayCommand(
                execute: _ => ShowFloatingSubtitles(),
                canExecute: _ => true
            );

            ShowFloatingInsightCommand = new RelayCommand(
                execute: _ => ShowFloatingInsight(),
                canExecute: _ => true
            );
            
            ToggleEditorTypeCommand = new RelayCommand(
                execute: _ => ToggleEditorType(),
                canExecute: _ => true
            );

            ShowMediaStudioCommand = new RelayCommand(
                execute: _ => ShowMediaStudio(),
                canExecute: _ => true
            );

            CycleThemeModeCommand = new RelayCommand(
                execute: _ => CycleThemeMode(),
                canExecute: _ => true
            );

            RegisterPostShowInitializationAction(
                "UpdateCheck",
                async () =>
                {
                    await Task.Delay(3000);
                    await Settings.AboutVM.CheckForUpdateOnStartupAsync();
                });

            _ = LoadConfigAsync();
        }

        private async Task LoadConfigAsync()
        {
            try
            {
                await ConfigVM.LoadConfigAsync();
                _config = ConfigVM.Config;

                MarkConfigLoaded();
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusMessage = $"加载配置失败: {ex.Message}";
                });

                MarkConfigLoaded();
            }
        }

        private void OnConfigVMConfigLoaded(AzureSpeechConfig config)
        {
            _config = config;

            // 初始化 SettingsViewModel
            Settings.Initialize(config);
            Settings.ConfigSaved += OnSettingsConfigSaved;

            AudioDevices.UpdateConfig();

            // Apply default font size from config
            Controls.AdvancedRichTextBox.DefaultFontSizeValue = _config.DefaultFontSize;
            SyncThemeModeFromConfig();
            SyncMainNavPaneStateFromConfig();

            BatchProcessing.NormalizeSpeechSubtitleOption();
            BatchProcessing.RefreshCommandStates();
            BatchProcessing.RebuildReviewSheets();
            AiInsight.UpdateConfig();

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

            var loadReport = _configService.LastLoadReport;
            StatusMessage = loadReport?.UsedFallbackConfig == true && !string.IsNullOrWhiteSpace(loadReport.WarningMessage)
                ? loadReport.WarningMessage!
                : $"配置已加载，文件位置: {ConfigVM.GetConfigFilePath()}";
        }

        private void OnConfigVMConfigUpdatedFromExternal(AzureSpeechConfig config)
        {
            _config = config;

            Settings.Initialize(config);

            AudioDevices.UpdateConfig();

            BatchProcessing.NormalizeSpeechSubtitleOption();
            BatchProcessing.RefreshCommandStates();
            BatchProcessing.RebuildReviewSheets();
            AiInsight.UpdateConfig();
            SyncThemeModeFromConfig();
            SyncMainNavPaneStateFromConfig();

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

            _ = AiInsight.TrySilentLoginForAiAsync();
        }

        private void OnSettingsConfigSaved(AzureSpeechConfig config)
        {
            _config = config;
            ConfigVM.SetConfig(config);

            AudioDevices.UpdateConfig();
            BatchProcessing.NormalizeSpeechSubtitleOption();
            BatchProcessing.RefreshCommandStates();
            BatchProcessing.RebuildReviewSheets();
            AiInsight.UpdateConfig();
            SyncThemeModeFromConfig();
            SyncMainNavPaneStateFromConfig();

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
        }

        private void OnFileLibrarySubtitleCuesLoaded()
        {
            BatchProcessing.OnSubtitleCuesLoaded();

            var sequence = _pendingReviewSequence;
            if (sequence == _lastDispatchedReviewSequence)
            {
                return;
            }

            var currentAudio = FileLibrary.SelectedAudioFile;
            var currentPath = currentAudio?.FullPath;
            if (!string.Equals(currentPath, _pendingReviewAudioPath, StringComparison.OrdinalIgnoreCase))
            {
                CrashLogger.AddBreadcrumb($"BatchReviewSkippedStaleQueue: queued='{_pendingReviewAudioPath}', current='{currentPath}'");
                return;
            }

            _lastDispatchedReviewSequence = sequence;
            CrashLogger.AddBreadcrumb($"BatchAudioSelectionDispatchSequential: seq={sequence}, audio={DescribeMediaFile(currentAudio)}");
            BatchProcessing.OnAudioFileSelected(currentAudio);
            CrashLogger.AddBreadcrumb($"BatchReviewLoadScheduled: seq={sequence}, audio={DescribeMediaFile(currentAudio)}");
        }

        private string BuildCrashContextSnapshot()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"StatusMessage: {_statusMessage}");
            sb.AppendLine($"IsTranslating: {_isTranslating}");
            sb.AppendLine($"HistoryCount: {_history.Count}");

            var selectedAudio = FileLibrary?.SelectedAudioFile;
            var selectedSubtitle = FileLibrary?.SelectedSubtitleFile;

            sb.AppendLine($"AudioFilesCount: {FileLibrary?.AudioFiles.Count ?? -1}");
            sb.AppendLine($"SubtitleFilesCount: {FileLibrary?.SubtitleFiles.Count ?? -1}");
            sb.AppendLine($"SubtitleCuesCount: {FileLibrary?.SubtitleCues.Count ?? -1}");
            sb.AppendLine($"SelectedAudio: {DescribeMediaFile(selectedAudio)}");
            sb.AppendLine($"SelectedSubtitle: {DescribeMediaFile(selectedSubtitle)}");
            sb.AppendLine($"PlaybackTime: {Playback?.PlaybackTimeText ?? "(null)"}");
            sb.AppendLine($"PlaybackState: playEnabled={Playback?.IsPlayEnabled ?? false}, pauseEnabled={Playback?.IsPauseEnabled ?? false}, stopEnabled={Playback?.IsStopEnabled ?? false}");
            sb.AppendLine($"BatchTasksCount: {BatchProcessing?.BatchTasks.Count ?? -1}");
            sb.AppendLine($"BatchQueueCount: {BatchProcessing?.BatchQueueItems.Count ?? -1}");

            return sb.ToString();
        }

        private static string DescribeMediaFile(MediaFileItem? item)
        {
            if (item == null)
            {
                return "(null)";
            }

            var name = item.Name ?? "(no-name)";
            var path = item.FullPath ?? "";
            long size = -1;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    size = new FileInfo(path).Length;
                }
            }
            catch
            {
                // ignore
            }

            return $"name='{name}', sizeBytes={size}, path='{path}'";
        }

        private void QueueUpdateTranslationConfig(AzureSpeechConfig config)
        {
            _ = UpdateTranslationConfigAsync(config);
        }
    }
}
