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

    public partial class MainWindowViewModel : ViewModelBase
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
        private SpeechTranslationService? _translationService;
        private Window? _mainWindow;
        private ConfigurationService _configService;
        private TextEditorType _editorType = TextEditorType.Advanced;
        private EditorDisplayMode _editorDisplayMode = EditorDisplayMode.Translated;

        private readonly AiInsightService _aiInsightService;
        private readonly UpdateService _updateService = new();

        public AudioDevicesViewModel AudioDevices { get; }
        public ConfigViewModel ConfigVM { get; }
        public FileLibraryViewModel FileLibrary { get; }
        public BatchProcessingViewModel BatchProcessing { get; }
        public SettingsViewModel Settings { get; }

        private bool _isFloatingSubtitleOpen;

        private FloatingSubtitleManager? _floatingSubtitleManager;
        private FloatingInsightManager? _floatingInsightManager;
        private bool _isFloatingInsightOpen;

        private int _uiModeIndex;
        private bool _isReviewModeViewCreated;

        private readonly List<(string Name, Func<Task> Action)> _postShowInitActions = new();
        private int _postShowInitStarted;
        private volatile bool _isMainWindowShown;
        private volatile bool _isConfigLoaded;
        private string? _pendingReviewAudioPath;

        private bool _isUpdateAvailable;
        private string _updateVersionText = "";
        private bool _isDownloading;
        private double _downloadProgress;
        private int _pendingReviewSequence;
        private int _lastDispatchedReviewSequence;

        public MainWindowViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator,
            SettingsViewModel settingsViewModel)
        {
            _configService = configService;
            Settings = settingsViewModel;
            var azureTokenProvider = new AzureTokenProvider("ai");
            _aiInsightService = new AiInsightService(azureTokenProvider);
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
                cfg => _translationService?.UpdateConfigAsync(cfg),
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
                _aiInsightService,
                FileLibrary,
                Playback,
                configService,
                () => ConfigVM.NotifyReviewLampChanged());

            FileLibrary.SubtitleCuesLoaded += OnFileLibrarySubtitleCuesLoaded;
            FileLibrary.AudioLibraryRefreshed += BatchProcessing.OnAudioLibraryRefreshed;

            AudioDevices = new AudioDevicesViewModel(
                () => _config,
                msg => StatusMessage = msg,
                () => IsTranslating,
                cfg => _translationService?.UpdateConfigAsync(cfg),
                () => _translationService?.TryApplyLiveAudioRoutingFromCurrentConfig() ?? false,
                reason => ConfigVM.QueueConfigSave(reason),
                (eventName, message) => AppLogService.Instance.LogAudit(eventName, message),
                () => _mainWindow);

            RegisterPostShowInitializationAction(
                "SubscriptionValidation",
                async () => await Dispatcher.UIThread.InvokeAsync(() => ConfigVM.TriggerSubscriptionValidation()));
            RegisterPostShowInitializationAction(
                "AudioDevicesRefresh",
                async () => await Dispatcher.UIThread.InvokeAsync(() => AudioDevices.RefreshAudioDevices(persistSelection: false)));
            RegisterPostShowInitializationAction(
                "AudioLibraryRefresh",
                async () => await Dispatcher.UIThread.InvokeAsync(() => FileLibrary.RefreshAudioLibrary()));

            AiInsight = new AiInsightViewModel(
                _aiInsightService,
                azureTokenProvider,
                () => _config,
                () => _history,
                msg => StatusMessage = msg,
                ShowConfig);

            RegisterPostShowInitializationAction(
                "AiSilentLogin",
                async () => await AiInsight.TrySilentLoginForAiAsync());

            CrashLogger.SetContextProvider(BuildCrashContextSnapshot);
            CrashLogger.AddBreadcrumb("MainWindowViewModel initialized");

            _ = LoadConfigAsync();

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

            OpenAzureSpeechPortalCommand = new RelayCommand(
                execute: _ => OpenUrl("https://portal.azure.com/#view/Microsoft_Azure_ProjectOxford/CognitiveServicesHub/~/SpeechServices"),
                canExecute: _ => true
            );

            Open21vAzureSpeechPortalCommand = new RelayCommand(
                execute: _ => OpenUrl("https://portal.azure.cn/#create/Microsoft.CognitiveServicesSpeechServices"),
                canExecute: _ => true
            );

            OpenStoragePortalCommand = new RelayCommand(
                execute: _ => OpenUrl("https://portal.azure.com/#create/Microsoft.StorageAccount"),
                canExecute: _ => true
            );

            Open21vStoragePortalCommand = new RelayCommand(
                execute: _ => OpenUrl("https://portal.azure.cn/#create/Microsoft.StorageAccount"),
                canExecute: _ => true
            );

            OpenFoundryPortalCommand = new RelayCommand(
                execute: _ => OpenUrl("https://ai.azure.com"),
                canExecute: _ => true
            );

            OpenProjectGitHubCommand = new RelayCommand(
                execute: _ => OpenUrl("https://github.com/kukisama/TrueFluentPro"),
                canExecute: _ => true
            );

            AppVersion = LoadAppVersion();

            ShowAboutCommand = new RelayCommand(
                execute: async _ => await ShowAbout(),
                canExecute: _ => true
            );

            ShowHelpCommand = new RelayCommand(
                execute: async _ => await ShowHelp(),
                canExecute: _ => true
            );

            ShowMediaStudioCommand = new RelayCommand(
                execute: _ => ShowMediaStudio(),
                canExecute: _ => true
            );

            CheckForUpdateCommand = new RelayCommand(
                execute: async _ => await CheckForUpdateAsync(silent: false),
                canExecute: _ => !_isDownloading
            );

            DownloadAndApplyUpdateCommand = new RelayCommand(
                execute: async _ => await DownloadAndApplyUpdateAsync(),
                canExecute: _ => _isUpdateAvailable && !_isDownloading
            );

            RegisterPostShowInitializationAction(
                "UpdateCheck",
                async () =>
                {
                    await Task.Delay(3000);
                    await CheckForUpdateAsync(silent: true);
                });
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

            BatchProcessing.NormalizeSpeechSubtitleOption();
            BatchProcessing.RefreshCommandStates();
            BatchProcessing.RebuildReviewSheets();
            AiInsight.UpdateConfig();

            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

            StatusMessage = $"配置已加载，文件位置: {ConfigVM.GetConfigFilePath()}";
        }

        private void OnConfigVMConfigUpdatedFromExternal(AzureSpeechConfig config)
        {
            _config = config;

            AudioDevices.UpdateConfig();

            BatchProcessing.NormalizeSpeechSubtitleOption();
            BatchProcessing.RefreshCommandStates();
            BatchProcessing.RebuildReviewSheets();
            AiInsight.UpdateConfig();

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
    }
}
