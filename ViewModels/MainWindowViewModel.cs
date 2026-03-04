using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;
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
        private string _currentOriginal = "";
        private string _currentTranslated = "";
        private ObservableCollection<TranslationItem> _history;
        private SpeechTranslationService? _translationService;
        private Window? _mainWindow;
        private ConfigurationService _configService;
        private ObservableCollection<string> _subscriptionNames;
        private int _activeSubscriptionIndex;
        private string _sourceLanguage = "auto";
        private string _targetLanguage = "zh-CN";
        private bool _isConfigurationEnabled = true;
        private TextEditorType _editorType = TextEditorType.Advanced;
        private EditorDisplayMode _editorDisplayMode = EditorDisplayMode.Translated;

        private readonly AiInsightService _aiInsightService;

        // Proxy for ReviewBatch.cs which still references IsAiConfigured directly
        private bool IsAiConfigured => _config.AiConfig?.IsValid == true;

        public AudioDevicesViewModel AudioDevices { get; }

        private readonly ObservableCollection<MediaFileItem> _audioFiles;
        private readonly ObservableCollection<MediaFileItem> _subtitleFiles;
        private readonly ObservableCollection<SubtitleCue> _subtitleCues;
        private readonly ObservableCollection<BatchTaskItem> _batchTasks;
        private readonly ObservableCollection<BatchQueueItem> _batchQueueItems;
        private int _batchConcurrencyLimit = 10;
        private bool _isBatchRunning;
        private CancellationTokenSource? _batchCts;
        private string _batchStatusMessage = "";
        private string _batchQueueStatusText = "";
        private Task? _batchQueueRunnerTask;
        private List<ReviewSheetPreset> _batchReviewSheetSnapshot = new();

        private MediaFileItem? _selectedAudioFile;
        private MediaFileItem? _selectedSubtitleFile;
        private SubtitleCue? _selectedSubtitleCue;
        private double _subtitleListHeight;

        private bool _isFloatingSubtitleOpen;

        private readonly AzureSubscriptionValidator _subscriptionValidator;
        private SubscriptionValidationState _subscriptionValidationState = SubscriptionValidationState.Unknown;
        private string _subscriptionValidationStatusMessage = "";
        private CancellationTokenSource? _subscriptionValidationCts;
        private int _subscriptionValidationVersion;
        private bool _subscriptionLampBlinkOn = true;
        private readonly DispatcherTimer _subscriptionLampTimer;
        private bool _reviewLampBlinkOn = true;

        private FloatingSubtitleManager? _floatingSubtitleManager;


        private bool _isSpeechSubtitleGenerating;
        private string _speechSubtitleStatusMessage = "";
        private CancellationTokenSource? _speechSubtitleCts;

        private int _uiModeIndex;
        private bool _isReviewModeViewCreated;

        private readonly ObservableCollection<ReviewSheetState> _reviewSheets = new();
        private ReviewSheetState? _selectedReviewSheet;

        private readonly string[] _sourceLanguages = { "auto", "en", "zh-CN", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES" };
        private readonly string[] _targetLanguages = { "en", "zh-CN", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES" };

        private readonly List<(string Name, Func<Task> Action)> _postShowInitActions = new();
        private int _postShowInitStarted;
        private volatile bool _isMainWindowShown;
        private volatile bool _isConfigLoaded;

        public MainWindowViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator)
        {
            _configService = configService;
            _subscriptionValidator = subscriptionValidator;
            var azureTokenProvider = new AzureTokenProvider("ai");
            _aiInsightService = new AiInsightService(azureTokenProvider);
            _config = new AzureSpeechConfig();
            AppLogService.Initialize(() => _config.BatchLogLevel);
            _history = new ObservableCollection<TranslationItem>();
            _subscriptionNames = new ObservableCollection<string>();
            _audioFiles = new ObservableCollection<MediaFileItem>();
            _subtitleFiles = new ObservableCollection<MediaFileItem>();
            _subtitleCues = new ObservableCollection<SubtitleCue>();
            _batchTasks = new ObservableCollection<BatchTaskItem>();
            _batchQueueItems = new ObservableCollection<BatchQueueItem>();
            _batchQueueStatusText = "队列为空";
            _subtitleCues.CollectionChanged += (_, _) => UpdateSubtitleListHeight();
            _batchTasks.CollectionChanged += (_, _) =>
            {
                if (ClearBatchTasksCommand is RelayCommand cmd)
                {
                    cmd.RaiseCanExecuteChanged();
                }
                if (StartBatchCommand is RelayCommand startCmd)
                {
                    startCmd.RaiseCanExecuteChanged();
                }
                OnPropertyChanged(nameof(BatchStartButtonText));
            };
            _batchQueueItems.CollectionChanged += (_, _) => UpdateBatchQueueStatusText();

            Playback = new PlaybackViewModel(
                msg => StatusMessage = msg,
                () => _subtitleCues,
                cue => SelectedSubtitleCue = cue,
                () => _selectedSubtitleCue);

            AudioDevices = new AudioDevicesViewModel(
                () => _config,
                msg => StatusMessage = msg,
                () => IsTranslating,
                cfg => _translationService?.UpdateConfigAsync(cfg),
                () => _translationService?.TryApplyLiveAudioRoutingFromCurrentConfig() ?? false,
                reason => QueueConfigSave(reason),
                (eventName, message) => AppendBatchDebugLog(eventName, message),
                () => _mainWindow);

            _subscriptionLampTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                if (_subscriptionValidationState == SubscriptionValidationState.Validating)
                {
                    _subscriptionLampBlinkOn = !_subscriptionLampBlinkOn;
                    OnPropertyChanged(nameof(SubscriptionLampOpacity));
                }
                if (IsReviewSummaryLoading)
                {
                    _reviewLampBlinkOn = !_reviewLampBlinkOn;
                    OnPropertyChanged(nameof(ReviewSummaryLampOpacity));
                }
                else if (ReviewSummaryLampOpacity != 1)
                {
                    _reviewLampBlinkOn = true;
                    OnPropertyChanged(nameof(ReviewSummaryLampOpacity));
                }
                else if (SubscriptionLampOpacity != 1)
                {
                    _subscriptionLampBlinkOn = true;
                    OnPropertyChanged(nameof(SubscriptionLampOpacity));
                }
            });
            _subscriptionLampTimer.Start();

            RegisterPostShowInitializationAction(
                "SubscriptionValidation",
                async () => await Dispatcher.UIThread.InvokeAsync(() => TriggerSubscriptionValidation()));
            RegisterPostShowInitializationAction(
                "AudioDevicesRefresh",
                async () => await Dispatcher.UIThread.InvokeAsync(() => AudioDevices.RefreshAudioDevices(persistSelection: false)));
            RegisterPostShowInitializationAction(
                "AudioLibraryRefresh",
                async () => await Dispatcher.UIThread.InvokeAsync(() => RefreshAudioLibrary()));

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
            
            ToggleEditorTypeCommand = new RelayCommand(
                execute: _ => ToggleEditorType(),
                canExecute: _ => true
            );

            RefreshAudioLibraryCommand = new RelayCommand(
                execute: _ => RefreshAudioLibrary(),
                canExecute: _ => true
            );

            OpenAzureSpeechPortalCommand = new RelayCommand(
                execute: _ => OpenUrl("https://portal.azure.com/#view/Microsoft_Azure_ProjectOxford/CognitiveServicesHub/~/SpeechServices"),
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

            GenerateReviewSummaryCommand = new RelayCommand(
                execute: _ => GenerateReviewSummary(),
                canExecute: _ => CanGenerateReviewSummary()
            );

            GenerateAllReviewSheetsCommand = new RelayCommand(
                execute: _ => GenerateAllReviewSheets(),
                canExecute: _ => CanGenerateAllReviewSheets()
            );

            ReviewMarkdownLinkCommand = new RelayCommand(
                execute: param => OnReviewMarkdownLink(param)
            );

            LoadBatchTasksCommand = new RelayCommand(
                execute: _ => LoadBatchTasksFromLibrary()
            );

            ClearBatchTasksCommand = new RelayCommand(
                execute: _ => ClearBatchTasks(),
                canExecute: _ => BatchTasks.Count > 0
            );

            StartBatchCommand = new RelayCommand(
                execute: _ => StartBatchProcessing(),
                canExecute: _ => CanStartBatchProcessing()
            );

            StopBatchCommand = new RelayCommand(
                execute: _ => StopBatchProcessing(),
                canExecute: _ => IsBatchRunning
            );

            RefreshBatchQueueCommand = new RelayCommand(
                execute: _ => RefreshBatchQueue(),
                canExecute: _ => true
            );

            CancelBatchQueueItemCommand = new RelayCommand(
                execute: param => CancelBatchQueueItem(param as BatchQueueItem)
            );

            EnqueueSubtitleReviewCommand = new RelayCommand(
                execute: param => EnqueueSubtitleAndReviewFromLibrary(param as MediaFileItem),
                canExecute: param => CanEnqueueSubtitleAndReviewFromLibrary(param as MediaFileItem)
            );

            GenerateSpeechSubtitleCommand = new RelayCommand(
                execute: _ => GenerateSpeechSubtitle(),
                canExecute: _ => CanGenerateSpeechSubtitle()
            );

            CancelSpeechSubtitleCommand = new RelayCommand(
                execute: _ => CancelSpeechSubtitle(),
                canExecute: _ => IsSpeechSubtitleGenerating
            );

            GenerateBatchSpeechSubtitleCommand = new RelayCommand(
                execute: _ => GenerateBatchSpeechSubtitle(),
                canExecute: _ => CanGenerateBatchSpeechSubtitle()
            );

            ShowMediaStudioCommand = new RelayCommand(
                execute: _ => ShowMediaStudio(),
                canExecute: _ => true
            );
        }
    }
}
