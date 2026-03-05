using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 设置页 ViewModel —— 管理配置中心的全部编辑逻辑。
    /// 替代原 ConfigCenterView.axaml.cs 的 1300+ 行事件驱动代码。
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly ConfigurationService _configService;
        private readonly AzureSubscriptionValidator _subscriptionValidator;

        private AzureSpeechConfig _config = new();

        // ─── 自动保存防抖 ───
        private Timer? _debounceTimer;
        private bool _isDirty;
        private string _autoSaveStatus = "";
        private const int DebounceMs = 500;

        // ─── 终结点管理 ───
        private ObservableCollection<AiEndpoint> _endpoints = new();
        private AiEndpoint? _selectedEndpoint;

        // ─── 订阅管理 ───
        private ObservableCollection<AzureSubscription> _subscriptions = new();
        private AzureSubscription? _selectedSubscription;
        private string _subscriptionEditorName = "";
        private string _subscriptionEditorKey = "";
        private string _subscriptionEditorEndpoint = "";
        private string _subscriptionEditorRegionHint = "";
        private string _subscriptionMessage = "";
        private string _testAllResult = "";
        private bool _showTestAllResult;

        // ─── 录音与存储 ───
        private bool _enableRecording = true;
        private int _recordingMp3BitrateKbps = 256;
        private string _sessionDirectory = "";
        private string _batchStorageConnectionString = "";
        private string _batchStorageStatus = "";
        private bool _batchStorageIsValid;
        private int _batchLogLevelIndex;
        private bool _batchForceRegeneration;
        private bool _contextMenuForceRegeneration = true;
        private bool _enableBatchSentenceSplit = true;
        private bool _batchSplitOnComma;
        private int _batchMaxChars = 24;
        private double _batchMaxDuration = 6;
        private int _batchPauseSplitMs = 500;
        private bool _useSpeechSubtitleForReview;

        // ─── 语音识别 ───
        private bool _filterModalParticles = true;
        private int _maxHistoryItems = 15;
        private int _realtimeMaxLength = 150;
        private int _chunkDurationMs = 200;
        private bool _enableAutoTimeout = true;
        private int _initialSilenceTimeoutSeconds = 25;
        private int _endSilenceTimeoutSeconds = 1;
        private bool _enableNoResponseRestart;
        private int _noResponseRestartSeconds = 3;
        private int _audioActivityThreshold = 600;
        private double _audioLevelGain = 2.0;
        private int _autoGainPresetIndex;
        private bool _showReconnectMarker = true;

        // ─── 字幕与文本 ───
        private bool _exportSrt;
        private bool _exportVtt;
        private int _defaultFontSize = 38;

        // ─── AI 洞察 ───
        private string _insightSystemPrompt = "";
        private string _reviewSystemPrompt = "";
        private string _insightUserContentTemplate = "";
        private string _reviewUserContentTemplate = "";
        private bool _autoInsightBufferOutput = true;
        private bool _summaryEnableReasoning;
        private ObservableCollection<InsightPresetButton> _presetButtons = new();

        // ─── 复盘 ───
        private ObservableCollection<ReviewSheetPreset> _reviewSheets = new();

        // ─── AI 基础配置（旧字段，迁移兼容） ───
        private int _aiProviderTypeIndex;
        private string _aiApiEndpoint = "";
        private string _aiApiKey = "";
        private string _quickModelName = "";
        private string _summaryModelName = "";
        private string _quickDeploymentName = "";
        private string _summaryDeploymentName = "";
        private string _aiApiVersion = "2024-02-01";
        private int _aiAzureAuthModeIndex;
        private string _aiAzureTenantId = "";
        private string _aiAzureClientId = "";

        // ─── 模型引用 ───
        private ModelOption? _selectedInsightModel;
        private ModelOption? _selectedSummaryModel;
        private ModelOption? _selectedQuickModel;
        private ModelOption? _selectedReviewModel;
        private ModelOption? _selectedImageModel;
        private ModelOption? _selectedVideoModel;

        /// <summary>配置被完整更新后触发（供外部系统同步）</summary>
        public event Action<AzureSpeechConfig>? ConfigSaved;

        public SettingsViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator)
        {
            _configService = configService;
            _subscriptionValidator = subscriptionValidator;

            AddEndpointCommand = new RelayCommand(_ => AddEndpoint());
            RemoveEndpointCommand = new RelayCommand(_ => RemoveEndpoint(), _ => SelectedEndpoint != null);
            AddModelCommand = new RelayCommand(_ => { }, _ => SelectedEndpoint != null);
            RemoveModelCommand = new RelayCommand(_ => { }, _ => false);
            TestEndpointCommand = new RelayCommand(async _ => await TestAiConnection(), _ => !string.IsNullOrWhiteSpace(AiApiEndpoint));

            AddSubscriptionCommand = new RelayCommand(async _ => await AddSubscriptionAsync());
            UpdateSubscriptionCommand = new RelayCommand(async _ => await UpdateSubscriptionAsync(), _ => SelectedSubscription != null);
            DeleteSubscriptionCommand = new RelayCommand(_ => DeleteSubscription(), _ => SelectedSubscription != null);
            TestSubscriptionCommand = new RelayCommand(async _ => await TestSubscriptionAsync());
            TestAllSubscriptionsCommand = new RelayCommand(async _ => await TestAllSubscriptionsAsync(), _ => Subscriptions.Count > 0);
            ValidateBatchStorageCommand = new RelayCommand(async _ => await ValidateBatchStorageAsync());
        }

        // ═══════════════════════════════════════════════════
        //  初始化
        // ═══════════════════════════════════════════════════

        /// <summary>用已加载的配置初始化 ViewModel（由 MainWindowViewModel 调用）</summary>
        public void Initialize(AzureSpeechConfig config)
        {
            _config = config;
            LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            // 订阅
            _subscriptions = new ObservableCollection<AzureSubscription>(_config.Subscriptions);
            OnPropertyChanged(nameof(Subscriptions));

            // 终结点
            _endpoints = new ObservableCollection<AiEndpoint>(_config.Endpoints);
            OnPropertyChanged(nameof(Endpoints));
            if (_endpoints.Count > 0)
                SelectedEndpoint = _endpoints[0];

            // 录音与存储
            EnableRecording = _config.EnableRecording;
            RecordingMp3BitrateKbps = _config.RecordingMp3BitrateKbps;
            SessionDirectory = _config.SessionDirectory;
            BatchStorageConnectionString = _config.BatchStorageConnectionString;
            BatchStorageIsValid = _config.BatchStorageIsValid;
            BatchStorageStatus = _config.BatchStorageIsValid ? "已验证存储账号" : "";
            BatchLogLevelIndex = _config.BatchLogLevel switch
            {
                BatchLogLevel.FailuresOnly => 1,
                BatchLogLevel.SuccessAndFailure => 2,
                _ => 0
            };
            BatchForceRegeneration = _config.BatchForceRegeneration;
            ContextMenuForceRegeneration = _config.ContextMenuForceRegeneration;
            EnableBatchSentenceSplit = _config.EnableBatchSubtitleSentenceSplit;
            BatchSplitOnComma = _config.BatchSubtitleSplitOnComma;
            BatchMaxChars = _config.BatchSubtitleMaxChars;
            BatchMaxDuration = _config.BatchSubtitleMaxDurationSeconds;
            BatchPauseSplitMs = _config.BatchSubtitlePauseSplitMs;
            UseSpeechSubtitleForReview = _config.UseSpeechSubtitleForReview;

            // 语音识别
            FilterModalParticles = _config.FilterModalParticles;
            MaxHistoryItems = _config.MaxHistoryItems;
            RealtimeMaxLength = _config.RealtimeMaxLength;
            ChunkDurationMs = _config.ChunkDurationMs;
            EnableAutoTimeout = _config.EnableAutoTimeout;
            InitialSilenceTimeoutSeconds = _config.InitialSilenceTimeoutSeconds;
            EndSilenceTimeoutSeconds = _config.EndSilenceTimeoutSeconds;
            EnableNoResponseRestart = _config.EnableNoResponseRestart;
            NoResponseRestartSeconds = _config.NoResponseRestartSeconds;
            AudioActivityThreshold = _config.AudioActivityThreshold;
            AudioLevelGain = _config.AudioLevelGain;
            AutoGainPresetIndex = _config.AutoGainEnabled ? (int)_config.AutoGainPreset : 0;
            ShowReconnectMarker = _config.ShowReconnectMarkerInSubtitle;

            // 字幕与文本
            ExportSrt = _config.ExportSrtSubtitles;
            ExportVtt = _config.ExportVttSubtitles;
            DefaultFontSize = _config.DefaultFontSize;

            // AI 基础配置
            var ai = _config.AiConfig ?? new AiConfig();
            AiProviderTypeIndex = ai.ProviderType == AiProviderType.AzureOpenAi ? 1 : 0;
            AiApiEndpoint = ai.ApiEndpoint;
            AiApiKey = ai.ApiKey;
            QuickModelName = string.IsNullOrWhiteSpace(ai.QuickModelName) ? ai.ModelName : ai.QuickModelName;
            SummaryModelName = string.IsNullOrWhiteSpace(ai.SummaryModelName) ? ai.ModelName : ai.SummaryModelName;
            QuickDeploymentName = string.IsNullOrWhiteSpace(ai.QuickDeploymentName) ? ai.DeploymentName : ai.QuickDeploymentName;
            SummaryDeploymentName = string.IsNullOrWhiteSpace(ai.SummaryDeploymentName) ? ai.DeploymentName : ai.SummaryDeploymentName;
            AiApiVersion = string.IsNullOrWhiteSpace(ai.ApiVersion) ? "2024-02-01" : ai.ApiVersion;
            AiAzureAuthModeIndex = ai.AzureAuthMode == AzureAuthMode.AAD ? 1 : 0;
            AiAzureTenantId = ai.AzureTenantId;
            AiAzureClientId = ai.AzureClientId;
            SummaryEnableReasoning = ai.SummaryEnableReasoning;
            InsightSystemPrompt = ai.InsightSystemPrompt;
            ReviewSystemPrompt = ai.ReviewSystemPrompt;
            InsightUserContentTemplate = ai.InsightUserContentTemplate;
            ReviewUserContentTemplate = ai.ReviewUserContentTemplate;
            AutoInsightBufferOutput = ai.AutoInsightBufferOutput;
            _presetButtons = new ObservableCollection<InsightPresetButton>(ai.PresetButtons);
            OnPropertyChanged(nameof(PresetButtons));
            _reviewSheets = new ObservableCollection<ReviewSheetPreset>(
                ai.ReviewSheets.Select(s => new ReviewSheetPreset
                {
                    Name = s.Name,
                    FileTag = s.FileTag,
                    Prompt = s.Prompt,
                    IncludeInBatch = s.IncludeInBatch
                }));
            OnPropertyChanged(nameof(ReviewSheets));

            // 模型引用
            RefreshModelOptions();
            SelectModelOption(ai.InsightModelRef, TextModels, v => _selectedInsightModel = v, nameof(SelectedInsightModel));
            SelectModelOption(ai.SummaryModelRef, TextModels, v => _selectedSummaryModel = v, nameof(SelectedSummaryModel));
            SelectModelOption(ai.QuickModelRef, TextModels, v => _selectedQuickModel = v, nameof(SelectedQuickModel));
            SelectModelOption(ai.ReviewModelRef, TextModels, v => _selectedReviewModel = v, nameof(SelectedReviewModel));
            SelectModelOption(_config.MediaGenConfig.ImageModelRef, ImageModels, v => _selectedImageModel = v, nameof(SelectedImageModel));
            SelectModelOption(_config.MediaGenConfig.VideoModelRef, VideoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));
        }

        // ═══════════════════════════════════════════════════
        //  属性 — 终结点管理
        // ═══════════════════════════════════════════════════

        public ObservableCollection<AiEndpoint> Endpoints
        {
            get => _endpoints;
            set => SetProperty(ref _endpoints, value);
        }

        public AiEndpoint? SelectedEndpoint
        {
            get => _selectedEndpoint;
            set
            {
                if (SetProperty(ref _selectedEndpoint, value))
                {
                    ((RelayCommand)RemoveEndpointCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AddModelCommand).RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectedEndpointModels));
                }
            }
        }

        public IEnumerable<AiModelEntry>? SelectedEndpointModels => SelectedEndpoint?.Models;

        // ─── 模型筛选 ───

        private List<ModelOption> _textModels = new();
        private List<ModelOption> _imageModels = new();
        private List<ModelOption> _videoModels = new();

        public List<ModelOption> TextModels
        {
            get => _textModels;
            private set => SetProperty(ref _textModels, value);
        }

        public List<ModelOption> ImageModels
        {
            get => _imageModels;
            private set => SetProperty(ref _imageModels, value);
        }

        public List<ModelOption> VideoModels
        {
            get => _videoModels;
            private set => SetProperty(ref _videoModels, value);
        }

        public ModelOption? SelectedInsightModel
        {
            get => _selectedInsightModel;
            set { if (SetProperty(ref _selectedInsightModel, value)) MarkDirty(); }
        }

        public ModelOption? SelectedSummaryModel
        {
            get => _selectedSummaryModel;
            set { if (SetProperty(ref _selectedSummaryModel, value)) MarkDirty(); }
        }

        public ModelOption? SelectedQuickModel
        {
            get => _selectedQuickModel;
            set { if (SetProperty(ref _selectedQuickModel, value)) MarkDirty(); }
        }

        public ModelOption? SelectedReviewModel
        {
            get => _selectedReviewModel;
            set { if (SetProperty(ref _selectedReviewModel, value)) MarkDirty(); }
        }

        public ModelOption? SelectedImageModel
        {
            get => _selectedImageModel;
            set { if (SetProperty(ref _selectedImageModel, value)) MarkDirty(); }
        }

        public ModelOption? SelectedVideoModel
        {
            get => _selectedVideoModel;
            set { if (SetProperty(ref _selectedVideoModel, value)) MarkDirty(); }
        }

        // ═══════════════════════════════════════════════════
        //  属性 — 订阅管理
        // ═══════════════════════════════════════════════════

        public ObservableCollection<AzureSubscription> Subscriptions
        {
            get => _subscriptions;
            set => SetProperty(ref _subscriptions, value);
        }

        public AzureSubscription? SelectedSubscription
        {
            get => _selectedSubscription;
            set
            {
                if (SetProperty(ref _selectedSubscription, value))
                {
                    ((RelayCommand)UpdateSubscriptionCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)DeleteSubscriptionCommand).RaiseCanExecuteChanged();
                    if (value != null) LoadSubscriptionToEditor(value);
                }
            }
        }

        public string SubscriptionEditorName
        {
            get => _subscriptionEditorName;
            set => SetProperty(ref _subscriptionEditorName, value);
        }

        public string SubscriptionEditorKey
        {
            get => _subscriptionEditorKey;
            set => SetProperty(ref _subscriptionEditorKey, value);
        }

        public string SubscriptionEditorEndpoint
        {
            get => _subscriptionEditorEndpoint;
            set
            {
                if (SetProperty(ref _subscriptionEditorEndpoint, value))
                    UpdateEndpointRegionHint();
            }
        }

        public string SubscriptionEditorRegionHint
        {
            get => _subscriptionEditorRegionHint;
            set => SetProperty(ref _subscriptionEditorRegionHint, value);
        }

        public string SubscriptionMessage
        {
            get => _subscriptionMessage;
            set => SetProperty(ref _subscriptionMessage, value);
        }

        public string TestAllResult
        {
            get => _testAllResult;
            set => SetProperty(ref _testAllResult, value);
        }

        public bool ShowTestAllResult
        {
            get => _showTestAllResult;
            set => SetProperty(ref _showTestAllResult, value);
        }

        // ═══════════════════════════════════════════════════
        //  属性 — 录音与存储
        // ═══════════════════════════════════════════════════

        public bool EnableRecording
        {
            get => _enableRecording;
            set { if (SetProperty(ref _enableRecording, value)) MarkDirty(); }
        }

        public int RecordingMp3BitrateKbps
        {
            get => _recordingMp3BitrateKbps;
            set { if (SetProperty(ref _recordingMp3BitrateKbps, value)) MarkDirty(); }
        }

        public string SessionDirectory
        {
            get => _sessionDirectory;
            set { if (SetProperty(ref _sessionDirectory, value)) MarkDirty(); }
        }

        public string BatchStorageConnectionString
        {
            get => _batchStorageConnectionString;
            set => SetProperty(ref _batchStorageConnectionString, value);
        }

        public string BatchStorageStatus
        {
            get => _batchStorageStatus;
            set => SetProperty(ref _batchStorageStatus, value);
        }

        public bool BatchStorageIsValid
        {
            get => _batchStorageIsValid;
            set => SetProperty(ref _batchStorageIsValid, value);
        }

        public int BatchLogLevelIndex
        {
            get => _batchLogLevelIndex;
            set { if (SetProperty(ref _batchLogLevelIndex, value)) MarkDirty(); }
        }

        public bool BatchForceRegeneration
        {
            get => _batchForceRegeneration;
            set { if (SetProperty(ref _batchForceRegeneration, value)) MarkDirty(); }
        }

        public bool ContextMenuForceRegeneration
        {
            get => _contextMenuForceRegeneration;
            set { if (SetProperty(ref _contextMenuForceRegeneration, value)) MarkDirty(); }
        }

        public bool EnableBatchSentenceSplit
        {
            get => _enableBatchSentenceSplit;
            set { if (SetProperty(ref _enableBatchSentenceSplit, value)) MarkDirty(); }
        }

        public bool BatchSplitOnComma
        {
            get => _batchSplitOnComma;
            set { if (SetProperty(ref _batchSplitOnComma, value)) MarkDirty(); }
        }

        public int BatchMaxChars
        {
            get => _batchMaxChars;
            set { if (SetProperty(ref _batchMaxChars, value)) MarkDirty(); }
        }

        public double BatchMaxDuration
        {
            get => _batchMaxDuration;
            set { if (SetProperty(ref _batchMaxDuration, value)) MarkDirty(); }
        }

        public int BatchPauseSplitMs
        {
            get => _batchPauseSplitMs;
            set { if (SetProperty(ref _batchPauseSplitMs, value)) MarkDirty(); }
        }

        public bool UseSpeechSubtitleForReview
        {
            get => _useSpeechSubtitleForReview;
            set { if (SetProperty(ref _useSpeechSubtitleForReview, value)) MarkDirty(); }
        }

        // ═══════════════════════════════════════════════════
        //  属性 — 语音识别
        // ═══════════════════════════════════════════════════

        public bool FilterModalParticles
        {
            get => _filterModalParticles;
            set { if (SetProperty(ref _filterModalParticles, value)) MarkDirty(); }
        }

        public int MaxHistoryItems
        {
            get => _maxHistoryItems;
            set { if (SetProperty(ref _maxHistoryItems, value)) MarkDirty(); }
        }

        public int RealtimeMaxLength
        {
            get => _realtimeMaxLength;
            set { if (SetProperty(ref _realtimeMaxLength, value)) MarkDirty(); }
        }

        public int ChunkDurationMs
        {
            get => _chunkDurationMs;
            set { if (SetProperty(ref _chunkDurationMs, value)) MarkDirty(); }
        }

        public bool EnableAutoTimeout
        {
            get => _enableAutoTimeout;
            set { if (SetProperty(ref _enableAutoTimeout, value)) MarkDirty(); }
        }

        public int InitialSilenceTimeoutSeconds
        {
            get => _initialSilenceTimeoutSeconds;
            set { if (SetProperty(ref _initialSilenceTimeoutSeconds, value)) MarkDirty(); }
        }

        public int EndSilenceTimeoutSeconds
        {
            get => _endSilenceTimeoutSeconds;
            set { if (SetProperty(ref _endSilenceTimeoutSeconds, value)) MarkDirty(); }
        }

        public bool EnableNoResponseRestart
        {
            get => _enableNoResponseRestart;
            set { if (SetProperty(ref _enableNoResponseRestart, value)) MarkDirty(); }
        }

        public int NoResponseRestartSeconds
        {
            get => _noResponseRestartSeconds;
            set { if (SetProperty(ref _noResponseRestartSeconds, value)) MarkDirty(); }
        }

        public int AudioActivityThreshold
        {
            get => _audioActivityThreshold;
            set { if (SetProperty(ref _audioActivityThreshold, value)) MarkDirty(); }
        }

        public double AudioLevelGain
        {
            get => _audioLevelGain;
            set { if (SetProperty(ref _audioLevelGain, value)) MarkDirty(); }
        }

        public int AutoGainPresetIndex
        {
            get => _autoGainPresetIndex;
            set { if (SetProperty(ref _autoGainPresetIndex, value)) MarkDirty(); }
        }

        public bool ShowReconnectMarker
        {
            get => _showReconnectMarker;
            set { if (SetProperty(ref _showReconnectMarker, value)) MarkDirty(); }
        }

        // ═══════════════════════════════════════════════════
        //  属性 — 字幕与文本
        // ═══════════════════════════════════════════════════

        public bool ExportSrt
        {
            get => _exportSrt;
            set { if (SetProperty(ref _exportSrt, value)) MarkDirty(); }
        }

        public bool ExportVtt
        {
            get => _exportVtt;
            set { if (SetProperty(ref _exportVtt, value)) MarkDirty(); }
        }

        public int DefaultFontSize
        {
            get => _defaultFontSize;
            set { if (SetProperty(ref _defaultFontSize, value)) MarkDirty(); }
        }

        // ═══════════════════════════════════════════════════
        //  属性 — AI 基础配置
        // ═══════════════════════════════════════════════════

        public int AiProviderTypeIndex
        {
            get => _aiProviderTypeIndex;
            set
            {
                if (SetProperty(ref _aiProviderTypeIndex, value))
                {
                    OnPropertyChanged(nameof(IsAzureProvider));
                    OnPropertyChanged(nameof(IsOpenAiProvider));
                    MarkDirty();
                }
            }
        }

        public bool IsAzureProvider => AiProviderTypeIndex == 1;
        public bool IsOpenAiProvider => AiProviderTypeIndex == 0;

        public string AiApiEndpoint
        {
            get => _aiApiEndpoint;
            set { if (SetProperty(ref _aiApiEndpoint, value)) MarkDirty(); }
        }

        public string AiApiKey
        {
            get => _aiApiKey;
            set { if (SetProperty(ref _aiApiKey, value)) MarkDirty(); }
        }

        public string QuickModelName
        {
            get => _quickModelName;
            set { if (SetProperty(ref _quickModelName, value)) MarkDirty(); }
        }

        public string SummaryModelName
        {
            get => _summaryModelName;
            set { if (SetProperty(ref _summaryModelName, value)) MarkDirty(); }
        }

        public string QuickDeploymentName
        {
            get => _quickDeploymentName;
            set { if (SetProperty(ref _quickDeploymentName, value)) MarkDirty(); }
        }

        public string SummaryDeploymentName
        {
            get => _summaryDeploymentName;
            set { if (SetProperty(ref _summaryDeploymentName, value)) MarkDirty(); }
        }

        public string AiApiVersion
        {
            get => _aiApiVersion;
            set { if (SetProperty(ref _aiApiVersion, value)) MarkDirty(); }
        }

        public int AiAzureAuthModeIndex
        {
            get => _aiAzureAuthModeIndex;
            set
            {
                if (SetProperty(ref _aiAzureAuthModeIndex, value))
                {
                    OnPropertyChanged(nameof(IsAadAuth));
                    OnPropertyChanged(nameof(ShowApiKeyField));
                    MarkDirty();
                }
            }
        }

        public bool IsAadAuth => AiAzureAuthModeIndex == 1;
        public bool ShowApiKeyField => !IsAzureProvider || !IsAadAuth;

        public string AiAzureTenantId
        {
            get => _aiAzureTenantId;
            set { if (SetProperty(ref _aiAzureTenantId, value)) MarkDirty(); }
        }

        public string AiAzureClientId
        {
            get => _aiAzureClientId;
            set { if (SetProperty(ref _aiAzureClientId, value)) MarkDirty(); }
        }

        public bool SummaryEnableReasoning
        {
            get => _summaryEnableReasoning;
            set { if (SetProperty(ref _summaryEnableReasoning, value)) MarkDirty(); }
        }

        public string InsightSystemPrompt
        {
            get => _insightSystemPrompt;
            set { if (SetProperty(ref _insightSystemPrompt, value)) MarkDirty(); }
        }

        public string ReviewSystemPrompt
        {
            get => _reviewSystemPrompt;
            set { if (SetProperty(ref _reviewSystemPrompt, value)) MarkDirty(); }
        }

        public string InsightUserContentTemplate
        {
            get => _insightUserContentTemplate;
            set { if (SetProperty(ref _insightUserContentTemplate, value)) MarkDirty(); }
        }

        public string ReviewUserContentTemplate
        {
            get => _reviewUserContentTemplate;
            set { if (SetProperty(ref _reviewUserContentTemplate, value)) MarkDirty(); }
        }

        public bool AutoInsightBufferOutput
        {
            get => _autoInsightBufferOutput;
            set { if (SetProperty(ref _autoInsightBufferOutput, value)) MarkDirty(); }
        }

        public ObservableCollection<InsightPresetButton> PresetButtons
        {
            get => _presetButtons;
            set => SetProperty(ref _presetButtons, value);
        }

        public ObservableCollection<ReviewSheetPreset> ReviewSheets
        {
            get => _reviewSheets;
            set => SetProperty(ref _reviewSheets, value);
        }

        // ─── 自动保存状态 ───

        public string AutoSaveStatus
        {
            get => _autoSaveStatus;
            set => SetProperty(ref _autoSaveStatus, value);
        }

        // ═══════════════════════════════════════════════════
        //  Commands
        // ═══════════════════════════════════════════════════

        public ICommand AddEndpointCommand { get; }
        public ICommand RemoveEndpointCommand { get; }
        public ICommand AddModelCommand { get; }
        public ICommand RemoveModelCommand { get; }
        public ICommand TestEndpointCommand { get; }
        public ICommand AddSubscriptionCommand { get; }
        public ICommand UpdateSubscriptionCommand { get; }
        public ICommand DeleteSubscriptionCommand { get; }
        public ICommand TestSubscriptionCommand { get; }
        public ICommand TestAllSubscriptionsCommand { get; }
        public ICommand ValidateBatchStorageCommand { get; }

        // ═══════════════════════════════════════════════════
        //  终结点 CRUD
        // ═══════════════════════════════════════════════════

        private void AddEndpoint()
        {
            var ep = new AiEndpoint
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"终结点 {Endpoints.Count + 1}",
                IsEnabled = true,
            };
            Endpoints.Add(ep);
            SelectedEndpoint = ep;
            SyncEndpointsToConfig();
            MarkDirty();
        }

        private void RemoveEndpoint()
        {
            if (SelectedEndpoint == null) return;
            Endpoints.Remove(SelectedEndpoint);
            SelectedEndpoint = Endpoints.FirstOrDefault();
            SyncEndpointsToConfig();
            RefreshModelOptions();
            MarkDirty();
        }

        public void AddModelToSelectedEndpoint(string modelId, string displayName, string groupName, string deploymentName, ModelCapability capabilities)
        {
            if (SelectedEndpoint == null) return;
            SelectedEndpoint.Models.Add(new AiModelEntry
            {
                ModelId = modelId,
                DisplayName = displayName,
                GroupName = groupName,
                DeploymentName = deploymentName,
                Capabilities = capabilities
            });
            OnPropertyChanged(nameof(SelectedEndpointModels));
            SyncEndpointsToConfig();
            RefreshModelOptions();
            MarkDirty();
        }

        public void RemoveModelFromSelectedEndpoint(AiModelEntry model)
        {
            if (SelectedEndpoint == null) return;
            SelectedEndpoint.Models.Remove(model);
            OnPropertyChanged(nameof(SelectedEndpointModels));
            SyncEndpointsToConfig();
            RefreshModelOptions();
            MarkDirty();
        }

        private void SyncEndpointsToConfig()
        {
            _config.Endpoints = Endpoints.ToList();
        }

        // ═══════════════════════════════════════════════════
        //  模型筛选
        // ═══════════════════════════════════════════════════

        private void RefreshModelOptions()
        {
            TextModels = BuildModelOptions(ModelCapability.Text);
            ImageModels = BuildModelOptions(ModelCapability.Image);
            VideoModels = BuildModelOptions(ModelCapability.Video);
        }

        private List<ModelOption> BuildModelOptions(ModelCapability required)
        {
            return _config.GetAvailableModels(required)
                .Select(pair => new ModelOption
                {
                    Reference = new ModelReference
                    {
                        EndpointId = pair.Endpoint.Id,
                        ModelId = pair.Model.ModelId
                    },
                    EndpointName = pair.Endpoint.Name,
                    ModelDisplayName = string.IsNullOrWhiteSpace(pair.Model.DisplayName)
                        ? pair.Model.ModelId
                        : pair.Model.DisplayName
                })
                .ToList();
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options, Action<ModelOption?> setter, string propertyName)
        {
            if (reference == null) { setter(null); OnPropertyChanged(propertyName); return; }
            var match = options.FirstOrDefault(o =>
                o.Reference.EndpointId == reference.EndpointId &&
                o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }

        // ═══════════════════════════════════════════════════
        //  订阅 CRUD
        // ═══════════════════════════════════════════════════

        private void LoadSubscriptionToEditor(AzureSubscription sub)
        {
            SubscriptionEditorName = sub.Name;
            SubscriptionEditorKey = sub.SubscriptionKey;
            SubscriptionEditorEndpoint = sub.GetEffectiveEndpoint();
        }

        private void UpdateEndpointRegionHint()
        {
            var ep = SubscriptionEditorEndpoint?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(ep))
            {
                SubscriptionEditorRegionHint = "";
                return;
            }
            var region = AzureSubscription.ParseRegionFromEndpoint(ep);
            if (!string.IsNullOrWhiteSpace(region))
            {
                var type = ep.Contains(".azure.cn", StringComparison.OrdinalIgnoreCase) ? "中国区" : "国际版";
                SubscriptionEditorRegionHint = $"✓ 已识别区域: {region} ({type})";
            }
            else
            {
                SubscriptionEditorRegionHint = "✗ 无法识别区域，请检查终结点格式";
            }
        }

        private async Task AddSubscriptionAsync()
        {
            if (string.IsNullOrWhiteSpace(SubscriptionEditorName) ||
                string.IsNullOrWhiteSpace(SubscriptionEditorKey))
            {
                SubscriptionMessage = "请填写订阅名称和密钥";
                return;
            }

            var key = SubscriptionEditorKey.Trim();
            var endpoint = SubscriptionEditorEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(region))
            {
                SubscriptionMessage = "无法从终结点解析区域，请检查格式。";
                return;
            }

            SubscriptionMessage = "验证中...";
            var (isValid, message) = await ValidateSubscriptionAsync(key, region, endpoint);
            if (!isValid)
            {
                SubscriptionMessage = $"✗ {message}";
                return;
            }

            var newSub = new AzureSubscription
            {
                Name = SubscriptionEditorName.Trim(),
                SubscriptionKey = key,
                ServiceRegion = region,
                Endpoint = endpoint
            };
            Subscriptions.Add(newSub);
            SelectedSubscription = newSub;
            SyncSubscriptionsToConfig();
            MarkDirty();
            SubscriptionMessage = "✓ 订阅添加成功！";
        }

        private async Task UpdateSubscriptionAsync()
        {
            if (SelectedSubscription == null)
            {
                SubscriptionMessage = "请先选择要更新的订阅";
                return;
            }
            if (string.IsNullOrWhiteSpace(SubscriptionEditorName) ||
                string.IsNullOrWhiteSpace(SubscriptionEditorKey))
            {
                SubscriptionMessage = "请填写订阅名称和密钥";
                return;
            }

            var key = SubscriptionEditorKey.Trim();
            var endpoint = SubscriptionEditorEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(region))
            {
                SubscriptionMessage = "无法从终结点解析区域";
                return;
            }

            SubscriptionMessage = "验证中...";
            var (isValid, message) = await ValidateSubscriptionAsync(key, region, endpoint);
            if (!isValid)
            {
                SubscriptionMessage = $"✗ {message}";
                return;
            }

            SelectedSubscription.Name = SubscriptionEditorName.Trim();
            SelectedSubscription.SubscriptionKey = key;
            SelectedSubscription.ServiceRegion = region;
            SelectedSubscription.Endpoint = endpoint;

            // Refresh display
            var idx = Subscriptions.IndexOf(SelectedSubscription);
            if (idx >= 0)
            {
                var temp = SelectedSubscription;
                Subscriptions[idx] = temp;
            }

            SyncSubscriptionsToConfig();
            MarkDirty();
            SubscriptionMessage = "✓ 订阅更新成功！";
        }

        private void DeleteSubscription()
        {
            if (SelectedSubscription == null)
            {
                SubscriptionMessage = "请先选择要删除的订阅";
                return;
            }
            Subscriptions.Remove(SelectedSubscription);
            SelectedSubscription = Subscriptions.FirstOrDefault();
            SyncSubscriptionsToConfig();
            MarkDirty();
            SubscriptionMessage = "✓ 订阅删除成功！";
        }

        private async Task TestSubscriptionAsync()
        {
            if (string.IsNullOrWhiteSpace(SubscriptionEditorKey))
            {
                SubscriptionMessage = "请输入订阅密钥";
                return;
            }

            var endpoint = SubscriptionEditorEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(region))
            {
                SubscriptionMessage = "无法从终结点解析区域";
                return;
            }

            SubscriptionMessage = "测试中...";
            var (isValid, message) = await ValidateSubscriptionAsync(SubscriptionEditorKey.Trim(), region, endpoint);
            SubscriptionMessage = isValid ? $"✓ {message}" : $"✗ {message}";
        }

        private async Task TestAllSubscriptionsAsync()
        {
            if (Subscriptions.Count == 0)
            {
                SubscriptionMessage = "订阅列表为空";
                return;
            }

            ShowTestAllResult = true;
            TestAllResult = "正在测试所有订阅...";

            var results = new List<(string Name, string Region, bool IsValid, long Ms, string Msg)>();
            foreach (var sub in Subscriptions)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (isValid, message) = await _subscriptionValidator.ValidateAsync(sub, CancellationToken.None);
                sw.Stop();
                results.Add((sub.Name, sub.ServiceRegion, isValid, sw.ElapsedMilliseconds, message));
            }

            results.Sort((a, b) =>
            {
                if (a.IsValid != b.IsValid) return a.IsValid ? -1 : 1;
                return a.Ms.CompareTo(b.Ms);
            });

            var text = "测试结果（按速度排序）：\n";
            foreach (var (name, region, isValid, ms, msg) in results)
            {
                text += $"{(isValid ? "✓" : "✗")} {name} ({region}) — {ms}ms";
                if (!isValid) text += $" [{msg}]";
                text += "\n";
            }

            if (results.Any(r => r.IsValid))
            {
                var fastest = results.First(r => r.IsValid);
                text += $"\n🏆 最快: {fastest.Name} ({fastest.Region}) — {fastest.Ms}ms";
            }

            TestAllResult = text.TrimEnd();
        }

        private async Task<(bool, string)> ValidateSubscriptionAsync(string key, string region, string endpoint)
        {
            var sub = new AzureSubscription
            {
                Name = "(test)",
                SubscriptionKey = key,
                ServiceRegion = region,
                Endpoint = endpoint
            };
            return await _subscriptionValidator.ValidateAsync(sub, CancellationToken.None);
        }

        private void SyncSubscriptionsToConfig()
        {
            _config.Subscriptions = Subscriptions.ToList();
        }

        // ═══════════════════════════════════════════════════
        //  存储验证
        // ═══════════════════════════════════════════════════

        private async Task ValidateBatchStorageAsync()
        {
            var cs = BatchStorageConnectionString?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(cs))
            {
                BatchStorageStatus = "请填写存储账号连接字符串";
                BatchStorageIsValid = false;
                return;
            }

            BatchStorageStatus = "验证中...";
            try
            {
                var client = new Azure.Storage.Blobs.BlobServiceClient(cs);
                await client.GetAccountInfoAsync(CancellationToken.None);

                var audioContainer = client.GetBlobContainerClient(AzureSpeechConfig.DefaultBatchAudioContainerName);
                await audioContainer.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

                var resultContainer = client.GetBlobContainerClient(AzureSpeechConfig.DefaultBatchResultContainerName);
                await resultContainer.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

                BatchStorageStatus = "存储账号验证成功，可用";
                BatchStorageIsValid = true;
                _config.BatchStorageIsValid = true;
                _config.BatchStorageConnectionString = cs;
                MarkDirty();
            }
            catch (Exception ex)
            {
                BatchStorageStatus = $"存储账号验证失败: {ex.Message}";
                BatchStorageIsValid = false;
                _config.BatchStorageIsValid = false;
            }
        }

        // ═══════════════════════════════════════════════════
        //  AI 测试连接
        // ═══════════════════════════════════════════════════

        private string _aiTestStatus = "";
        private string _aiTestReasoning = "";

        public string AiTestStatus
        {
            get => _aiTestStatus;
            set => SetProperty(ref _aiTestStatus, value);
        }

        public string AiTestReasoning
        {
            get => _aiTestReasoning;
            set => SetProperty(ref _aiTestReasoning, value);
        }

        private async Task TestAiConnection()
        {
            var testConfig = new AiConfig
            {
                ProviderType = AiProviderTypeIndex == 1 ? AiProviderType.AzureOpenAi : AiProviderType.OpenAiCompatible,
                ApiEndpoint = AiApiEndpoint?.Trim() ?? "",
                ApiKey = AiApiKey?.Trim() ?? "",
                QuickModelName = QuickModelName?.Trim() ?? "",
                SummaryModelName = SummaryModelName?.Trim() ?? "",
                QuickDeploymentName = QuickDeploymentName?.Trim() ?? "",
                SummaryDeploymentName = SummaryDeploymentName?.Trim() ?? "",
                ApiVersion = AiApiVersion?.Trim() ?? "2024-02-01",
                SummaryEnableReasoning = SummaryEnableReasoning
            };
            ConfigViewHelper.ApplyModelDeploymentFallbacks(testConfig);

            if (!testConfig.IsValid)
            {
                AiTestStatus = "请填写必要的配置信息";
                return;
            }

            AiTestStatus = "正在连接...";
            AiTestReasoning = "";

            try
            {
                var service = new AiInsightService();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var received = false;
                var reasoningBuilder = new System.Text.StringBuilder();
                var reasoningReceived = false;

                await service.StreamChatAsync(
                    testConfig,
                    "You are a helpful assistant.",
                    "Provide one short answer and think step-by-step.",
                    chunk => { received = true; },
                    cts.Token,
                    AiChatProfile.Summary,
                    enableReasoning: testConfig.SummaryEnableReasoning,
                    onOutcome: null,
                    onReasoningChunk: chunk =>
                    {
                        reasoningReceived = true;
                        reasoningBuilder.Append(chunk);
                    });

                if (reasoningReceived)
                    AiTestReasoning = reasoningBuilder.ToString();
                else if (testConfig.SummaryEnableReasoning)
                    AiTestReasoning = "未收到思考内容。";

                AiTestStatus = received ? "连接成功！AI 服务可用。" : "连接成功但未收到响应，请检查模型配置。";
            }
            catch (OperationCanceledException)
            {
                AiTestStatus = "连接超时，请检查 API 端点是否正确。";
            }
            catch (Exception ex)
            {
                AiTestStatus = $"连接失败: {ex.Message}";
            }
        }

        // ═══════════════════════════════════════════════════
        //  自动保存 (debounce)
        // ═══════════════════════════════════════════════════

        private void MarkDirty()
        {
            _isDirty = true;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                Dispatcher.UIThread.Post(() => _ = FlushSaveAsync());
            }, null, DebounceMs, Timeout.Infinite);
        }

        private async Task FlushSaveAsync()
        {
            if (!_isDirty) return;
            _isDirty = false;

            try
            {
                ApplyToConfig();
                await _configService.SaveConfigAsync(_config);
                AutoSaveStatus = "✓ 配置已自动保存";
                ConfigSaved?.Invoke(_config);
            }
            catch (Exception ex)
            {
                AutoSaveStatus = $"保存失败: {ex.Message}";
            }
        }

        /// <summary>将 ViewModel 中所有属性写回 AzureSpeechConfig</summary>
        private void ApplyToConfig()
        {
            // 录音与存储
            _config.EnableRecording = EnableRecording;
            _config.RecordingMp3BitrateKbps = RecordingMp3BitrateKbps;
            var defaultDir = PathManager.Instance.DefaultSessionsPath;
            _config.SessionDirectoryOverride = string.IsNullOrWhiteSpace(SessionDirectory)
                ? null
                : (string.Equals(SessionDirectory, defaultDir, StringComparison.OrdinalIgnoreCase) ? null : SessionDirectory);
            PathManager.Instance.SetSessionsPath(_config.SessionDirectoryOverride);

            _config.BatchStorageConnectionString = BatchStorageConnectionString?.Trim() ?? "";
            _config.BatchAudioContainerName = AzureSpeechConfig.DefaultBatchAudioContainerName;
            _config.BatchResultContainerName = AzureSpeechConfig.DefaultBatchResultContainerName;
            _config.BatchLogLevel = BatchLogLevelIndex switch
            {
                1 => BatchLogLevel.FailuresOnly,
                2 => BatchLogLevel.SuccessAndFailure,
                _ => BatchLogLevel.Off
            };
            _config.BatchForceRegeneration = BatchForceRegeneration;
            _config.ContextMenuForceRegeneration = ContextMenuForceRegeneration;
            _config.EnableBatchSubtitleSentenceSplit = EnableBatchSentenceSplit;
            _config.BatchSubtitleSplitOnComma = BatchSplitOnComma;
            _config.BatchSubtitleMaxChars = BatchMaxChars;
            _config.BatchSubtitleMaxDurationSeconds = BatchMaxDuration;
            _config.BatchSubtitlePauseSplitMs = BatchPauseSplitMs;
            _config.UseSpeechSubtitleForReview = UseSpeechSubtitleForReview;

            // 语音识别
            _config.FilterModalParticles = FilterModalParticles;
            _config.MaxHistoryItems = MaxHistoryItems;
            _config.RealtimeMaxLength = RealtimeMaxLength;
            _config.ChunkDurationMs = ChunkDurationMs;
            _config.EnableAutoTimeout = EnableAutoTimeout;
            _config.InitialSilenceTimeoutSeconds = InitialSilenceTimeoutSeconds;
            _config.EndSilenceTimeoutSeconds = EndSilenceTimeoutSeconds;
            _config.EnableNoResponseRestart = EnableNoResponseRestart;
            _config.NoResponseRestartSeconds = NoResponseRestartSeconds;
            _config.AudioActivityThreshold = AudioActivityThreshold;
            _config.AudioLevelGain = AudioLevelGain;
            var presetIndex = Math.Clamp(AutoGainPresetIndex, 0, 3);
            _config.AutoGainEnabled = presetIndex > 0;
            _config.AutoGainPreset = (AutoGainPreset)presetIndex;
            _config.ShowReconnectMarkerInSubtitle = ShowReconnectMarker;

            // 字幕与文本
            _config.ExportSrtSubtitles = ExportSrt;
            _config.ExportVttSubtitles = ExportVtt;
            _config.DefaultFontSize = DefaultFontSize;
            Controls.AdvancedRichTextBox.DefaultFontSizeValue = DefaultFontSize;

            // AI 基础配置
            var ai = _config.AiConfig ?? new AiConfig();
            ai.ProviderType = AiProviderTypeIndex == 1 ? AiProviderType.AzureOpenAi : AiProviderType.OpenAiCompatible;
            ai.ApiEndpoint = AiApiEndpoint?.Trim() ?? "";
            ai.ApiKey = AiApiKey?.Trim() ?? "";
            ai.QuickModelName = QuickModelName?.Trim() ?? "";
            ai.SummaryModelName = SummaryModelName?.Trim() ?? "";
            ai.QuickDeploymentName = QuickDeploymentName?.Trim() ?? "";
            ai.SummaryDeploymentName = SummaryDeploymentName?.Trim() ?? "";
            ConfigViewHelper.ApplyModelDeploymentFallbacks(ai);
            ai.ApiVersion = AiApiVersion?.Trim() ?? "2024-02-01";
            ai.AzureAuthMode = AiAzureAuthModeIndex == 1 ? AzureAuthMode.AAD : AzureAuthMode.ApiKey;
            ai.AzureTenantId = AiAzureTenantId ?? "";
            ai.AzureClientId = AiAzureClientId ?? "";
            ai.SummaryEnableReasoning = SummaryEnableReasoning;
            ai.InsightSystemPrompt = InsightSystemPrompt?.Trim() ?? "";
            ai.ReviewSystemPrompt = ReviewSystemPrompt?.Trim() ?? "";
            ai.InsightUserContentTemplate = InsightUserContentTemplate?.Trim() ?? "";
            ai.ReviewUserContentTemplate = ReviewUserContentTemplate?.Trim() ?? "";
            ai.AutoInsightBufferOutput = AutoInsightBufferOutput;
            ai.PresetButtons = PresetButtons.Where(b => !string.IsNullOrWhiteSpace(b.Name)).ToList();
            ai.ReviewSheets = ReviewSheets
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new ReviewSheetPreset
                {
                    Name = s.Name.Trim(),
                    FileTag = string.IsNullOrWhiteSpace(s.FileTag) ? "summary" : s.FileTag.Trim(),
                    Prompt = s.Prompt?.Trim() ?? "",
                    IncludeInBatch = s.IncludeInBatch
                }).ToList();

            // 模型引用
            ai.InsightModelRef = SelectedInsightModel?.Reference;
            ai.SummaryModelRef = SelectedSummaryModel?.Reference;
            ai.QuickModelRef = SelectedQuickModel?.Reference;
            ai.ReviewModelRef = SelectedReviewModel?.Reference;
            _config.AiConfig = ai;

            _config.MediaGenConfig.ImageModelRef = SelectedImageModel?.Reference;
            _config.MediaGenConfig.VideoModelRef = SelectedVideoModel?.Reference;

            // 终结点
            SyncEndpointsToConfig();
        }
    }
}
