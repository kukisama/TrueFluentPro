using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly ConfigurationService _configService;
        private readonly AzureSubscriptionValidator _subscriptionValidator;

        private AzureSpeechConfig _config = new();

        private Timer? _debounceTimer;
        private bool _isDirty;
        private string _autoSaveStatus = "";
        private const int DebounceMs = 500;

        private ObservableCollection<AiEndpoint> _endpoints = new();
        private AiEndpoint? _selectedEndpoint;

        private ObservableCollection<AzureSubscription> _subscriptions = new();
        private AzureSubscription? _selectedSubscription;
        private string _subscriptionEditorName = "";
        private string _subscriptionEditorKey = "";
        private string _subscriptionEditorEndpoint = "";
        private string _subscriptionEditorRegionHint = "";
        private string _subscriptionMessage = "";
        private string _testAllResult = "";
        private bool _showTestAllResult;

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

        private bool _exportSrt;
        private bool _exportVtt;
        private int _defaultFontSize = 38;
        private bool _isAutoUpdateEnabled = true;

        private string _insightSystemPrompt = "";
        private string _reviewSystemPrompt = "";
        private string _insightUserContentTemplate = "";
        private string _reviewUserContentTemplate = "";
        private bool _autoInsightBufferOutput = true;
        private bool _summaryEnableReasoning;
        private ObservableCollection<InsightPresetButton> _presetButtons = new();

        private ObservableCollection<ReviewSheetPreset> _reviewSheets = new();

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

        private ModelOption? _selectedInsightModel;
        private ModelOption? _selectedSummaryModel;
        private ModelOption? _selectedQuickModel;
        private ModelOption? _selectedReviewModel;
        private ModelOption? _selectedImageModel;
        private ModelOption? _selectedVideoModel;

        private int _videoApiModeIndex;
        private int _videoWidth = 1280;
        private int _videoHeight = 720;
        private int _videoPollIntervalMs = 3000;

        // 图片/视频参数本地字段（不再代理 MediaGenConfig，ApplyToConfigCore 时写入）
        private string _imageSize = "1024x1024";
        private string _imageQuality = "medium";
        private string _imageFormat = "png";
        private int _imageCount = 1;
        private string _videoAspectRatio = "16:9";
        private string _videoResolution = "720p";
        private int _videoSeconds = 5;
        private int _videoVariants = 1;

        private int _maxLoadedSessionsInMemory = 8;
        private string _mediaOutputDirectory = "";

        public event Action<AzureSpeechConfig>? ConfigSaved;

        public SettingsViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator)
        {
            _configService = configService;
            _subscriptionValidator = subscriptionValidator;

            AddEndpointCommand = new RelayCommand(_ => AddEndpoint());
            RemoveEndpointCommand = new RelayCommand(_ => RemoveEndpoint(), _ => SelectedEndpoint != null);
            TestEndpointCommand = new RelayCommand(async _ => await TestAiConnection());

            AddSubscriptionCommand = new RelayCommand(async _ => await AddSubscriptionAsync());
            UpdateSubscriptionCommand = new RelayCommand(async _ => await UpdateSubscriptionAsync(), _ => SelectedSubscription != null);
            DeleteSubscriptionCommand = new RelayCommand(_ => DeleteSubscription(), _ => SelectedSubscription != null);
            TestSubscriptionCommand = new RelayCommand(async _ => await TestSubscriptionAsync());
            TestAllSubscriptionsCommand = new RelayCommand(async _ => await TestAllSubscriptionsAsync(), _ => Subscriptions.Count > 0);
            ValidateBatchStorageCommand = new RelayCommand(async _ => await ValidateBatchStorageAsync());
        }

        public void Initialize(AzureSpeechConfig config)
        {
            _config = config;
            LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            _subscriptions = new ObservableCollection<AzureSubscription>(_config.Subscriptions);
            OnPropertyChanged(nameof(Subscriptions));
            ((RelayCommand)TestAllSubscriptionsCommand).RaiseCanExecuteChanged();

            _endpoints = new ObservableCollection<AiEndpoint>(_config.Endpoints);
            OnPropertyChanged(nameof(Endpoints));
            if (_endpoints.Count > 0)
                SelectedEndpoint = _endpoints[0];

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

            ExportSrt = _config.ExportSrtSubtitles;
            ExportVtt = _config.ExportVttSubtitles;
            DefaultFontSize = _config.DefaultFontSize;
            IsAutoUpdateEnabled = _config.IsAutoUpdateEnabled;

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

            var reviewSource = ai.ReviewSheets.Count > 0 ? ai.ReviewSheets : new AiConfig().ReviewSheets;
            _reviewSheets = new ObservableCollection<ReviewSheetPreset>(
                reviewSource.Select(s => new ReviewSheetPreset
                {
                    Name = s.Name,
                    FileTag = s.FileTag,
                    Prompt = s.Prompt,
                    IncludeInBatch = s.IncludeInBatch
                }));
            OnPropertyChanged(nameof(ReviewSheets));

            RefreshModelOptions();
            SelectModelOption(ai.InsightModelRef, TextModels, v => _selectedInsightModel = v, nameof(SelectedInsightModel));
            SelectModelOption(ai.SummaryModelRef, TextModels, v => _selectedSummaryModel = v, nameof(SelectedSummaryModel));
            SelectModelOption(ai.QuickModelRef, TextModels, v => _selectedQuickModel = v, nameof(SelectedQuickModel));
            SelectModelOption(ai.ReviewModelRef, TextModels, v => _selectedReviewModel = v, nameof(SelectedReviewModel));
            SelectModelOption(_config.MediaGenConfig.ImageModelRef, ImageModels, v => _selectedImageModel = v, nameof(SelectedImageModel));
            SelectModelOption(_config.MediaGenConfig.VideoModelRef, VideoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));

            _config.MediaGenConfig ??= new MediaGenConfig();
            var media = _config.MediaGenConfig;

            _imageSize = string.IsNullOrWhiteSpace(media.ImageSize) ? "1024x1024" : media.ImageSize;
            _imageQuality = string.IsNullOrWhiteSpace(media.ImageQuality) ? "medium" : media.ImageQuality;
            _imageFormat = string.IsNullOrWhiteSpace(media.ImageFormat) ? "png" : media.ImageFormat;
            _imageCount = media.ImageCount <= 0 ? 1 : media.ImageCount;
            _videoAspectRatio = string.IsNullOrWhiteSpace(media.VideoAspectRatio) ? "16:9" : media.VideoAspectRatio;
            _videoResolution = string.IsNullOrWhiteSpace(media.VideoResolution) ? "720p" : media.VideoResolution;
            _videoSeconds = media.VideoSeconds <= 0 ? 5 : media.VideoSeconds;
            _videoVariants = media.VideoVariants <= 0 ? 1 : media.VideoVariants;

            VideoApiModeIndex = media.VideoApiMode == VideoApiMode.Videos ? 1 : 0;
            VideoPollIntervalMs = media.VideoPollIntervalMs <= 0 ? 3000 : media.VideoPollIntervalMs;

            RefreshVideoCapabilityOptions();
            ApplyVideoSizeToAspectResolution(media.VideoWidth, media.VideoHeight);

            MaxLoadedSessionsInMemory = media.MaxLoadedSessionsInMemory <= 0 ? 8 : media.MaxLoadedSessionsInMemory;
            MediaOutputDirectory = media.OutputDirectory ?? "";

            OnPropertyChanged(nameof(ImageSize));
            OnPropertyChanged(nameof(ImageQuality));
            OnPropertyChanged(nameof(ImageFormat));
            OnPropertyChanged(nameof(ImageCount));
            OnPropertyChanged(nameof(VideoAspectRatio));
            OnPropertyChanged(nameof(VideoResolution));
            OnPropertyChanged(nameof(VideoSeconds));
            OnPropertyChanged(nameof(VideoVariants));

            _ = RefreshAiAuthStatusAsync();
        }

        public ObservableCollection<AiEndpoint> Endpoints { get => _endpoints; set => SetProperty(ref _endpoints, value); }

        public AiEndpoint? SelectedEndpoint
        {
            get => _selectedEndpoint;
            set
            {
                if (SetProperty(ref _selectedEndpoint, value))
                {
                    ((RelayCommand)RemoveEndpointCommand).RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectedEndpointModels));
                    OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                    OnPropertyChanged(nameof(IsSelectedEndpointAad));
                }
            }
        }

        public List<AiModelEntry>? SelectedEndpointModels =>
            SelectedEndpoint?.Models?.ToList();

        public int SelectedEndpointAuthMode
        {
            get => SelectedEndpoint?.AuthMode == AzureAuthMode.AAD ? 1 : 0;
            set
            {
                if (SelectedEndpoint == null) return;
                SelectedEndpoint.AuthMode = value == 1 ? AzureAuthMode.AAD : AzureAuthMode.ApiKey;
                OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                OnPropertyChanged(nameof(IsSelectedEndpointAad));
                SyncEndpointsToConfig();
                MarkDirty();
            }
        }

        public bool IsSelectedEndpointAad => SelectedEndpointAuthMode == 1;

        private List<ModelOption> _textModels = new();
        private List<ModelOption> _imageModels = new();
        private List<ModelOption> _videoModels = new();

        public List<ModelOption> TextModels { get => _textModels; private set => SetProperty(ref _textModels, value); }
        public List<ModelOption> ImageModels { get => _imageModels; private set => SetProperty(ref _imageModels, value); }
        public List<ModelOption> VideoModels { get => _videoModels; private set => SetProperty(ref _videoModels, value); }
        public ModelOption? SelectedInsightModel { get => _selectedInsightModel;
            set => Set(ref _selectedInsightModel, value, then: () => _ = RefreshAiAuthStatusAsync()); }

        public ModelOption? SelectedSummaryModel { get => _selectedSummaryModel;
            set => Set(ref _selectedSummaryModel, value, then: () => _ = RefreshAiAuthStatusAsync()); }

        public ModelOption? SelectedQuickModel { get => _selectedQuickModel;
            set => Set(ref _selectedQuickModel, value, then: () => _ = RefreshAiAuthStatusAsync()); }

        public ModelOption? SelectedReviewModel { get => _selectedReviewModel; set => Set(ref _selectedReviewModel, value); }
        public ModelOption? SelectedImageModel { get => _selectedImageModel; set => Set(ref _selectedImageModel, value); }
        public ModelOption? SelectedVideoModel { get => _selectedVideoModel;
            set => Set(ref _selectedVideoModel, value, then: RefreshVideoCapabilityOptions); }

        public List<string> ImageSizeOptions { get; } = ["1024x1024", "1024x1536", "1536x1024"];
        public List<string> ImageQualityOptions { get; } = ["low", "medium", "high"];
        public List<string> ImageFormatOptions { get; } = ["png", "jpeg"];
        public List<int> ImageCountOptions { get; } = [1, 2, 3, 4, 5];
        private List<string> _videoAspectRatioOptions = new() { "16:9", "9:16" };
        private List<string> _videoResolutionOptions = new() { "720p" };
        private List<int> _videoSecondsOptions = new() { 4, 8, 12 };
        private List<int> _videoVariantsOptions = new() { 1 };

        public List<string> VideoAspectRatioOptions { get => _videoAspectRatioOptions; private set => SetProperty(ref _videoAspectRatioOptions, value); }
        public List<string> VideoResolutionOptions { get => _videoResolutionOptions; private set => SetProperty(ref _videoResolutionOptions, value); }
        public List<int> VideoSecondsOptions { get => _videoSecondsOptions; private set => SetProperty(ref _videoSecondsOptions, value); }
        public List<int> VideoVariantsOptions { get => _videoVariantsOptions; private set => SetProperty(ref _videoVariantsOptions, value); }

        public string ImageSize { get => _imageSize; set => Set(ref _imageSize, value); }
        public string ImageQuality { get => _imageQuality; set => Set(ref _imageQuality, value); }
        public string ImageFormat { get => _imageFormat; set => Set(ref _imageFormat, value); }
        public int ImageCount { get => _imageCount; set => Set(ref _imageCount, value); }
        public string VideoAspectRatio { get => _videoAspectRatio;
            set => Set(ref _videoAspectRatio, value, then: SyncVideoDimensionsFromSelection); }
        public string VideoResolution { get => _videoResolution;
            set => Set(ref _videoResolution, value, then: SyncVideoDimensionsFromSelection); }
        public int VideoApiModeIndex { get => _videoApiModeIndex;
            set => Set(ref _videoApiModeIndex, value, then: RefreshVideoCapabilityOptions); }

        public int VideoWidth { get => _videoWidth; private set => SetProperty(ref _videoWidth, value); }
        public int VideoHeight { get => _videoHeight; private set => SetProperty(ref _videoHeight, value); }
        public int VideoSeconds { get => _videoSeconds; set => Set(ref _videoSeconds, value); }
        public int VideoVariants { get => _videoVariants; set => Set(ref _videoVariants, value); }
        public int VideoPollIntervalMs { get => _videoPollIntervalMs; set => Set(ref _videoPollIntervalMs, value); }
        public int MaxLoadedSessionsInMemory { get => _maxLoadedSessionsInMemory; set => Set(ref _maxLoadedSessionsInMemory, value); }
        public string MediaOutputDirectory { get => _mediaOutputDirectory; set => Set(ref _mediaOutputDirectory, value); }

        private void RefreshVideoCapabilityOptions()
        {
            var mode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            var modelId = SelectedVideoModel?.Reference?.ModelId ?? _config.MediaGenConfig.VideoModel;
            var profile = VideoCapabilityResolver.ResolveProfile(mode, modelId);

            VideoAspectRatioOptions = profile.AspectRatioOptions.ToList();
            VideoResolutionOptions = profile.ResolutionOptions.ToList();
            VideoSecondsOptions = profile.DurationOptions.ToList();
            VideoVariantsOptions = profile.CountOptions.ToList();

            if (!VideoAspectRatioOptions.Contains(VideoAspectRatio))
                VideoAspectRatio = VideoAspectRatioOptions.FirstOrDefault() ?? "16:9";
            if (!VideoResolutionOptions.Contains(VideoResolution))
                VideoResolution = VideoResolutionOptions.FirstOrDefault() ?? "720p";
            if (!VideoSecondsOptions.Contains(VideoSeconds))
                VideoSeconds = VideoSecondsOptions.FirstOrDefault();
            if (!VideoVariantsOptions.Contains(VideoVariants))
                VideoVariants = VideoVariantsOptions.FirstOrDefault();

            SyncVideoDimensionsFromSelection();
        }

        private void SyncVideoDimensionsFromSelection()
        {
            var mode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            var modelId = SelectedVideoModel?.Reference?.ModelId ?? _config.MediaGenConfig.VideoModel;
            if (VideoCapabilityResolver.TryResolveSize(mode, modelId, VideoAspectRatio, VideoResolution, out var w, out var h))
            {
                VideoWidth = w;
                VideoHeight = h;
            }
        }

        private void ApplyVideoSizeToAspectResolution(int width, int height)
        {
            var mode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            var modelId = SelectedVideoModel?.Reference?.ModelId ?? _config.MediaGenConfig.VideoModel;
            var profile = VideoCapabilityResolver.ResolveProfile(mode, modelId);

            foreach (var aspect in profile.AspectRatioOptions)
            {
                foreach (var res in profile.ResolutionOptions)
                {
                    if (profile.TryResolveSize(aspect, res, out var w, out var h)
                        && w == width && h == height)
                    {
                        VideoAspectRatio = aspect;
                        VideoResolution = res;
                        return;
                    }
                }
            }

            SyncVideoDimensionsFromSelection();
        }

        public ObservableCollection<AzureSubscription> Subscriptions { get => _subscriptions; set => SetProperty(ref _subscriptions, value); }

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

        public string SubscriptionEditorName { get => _subscriptionEditorName; set => SetProperty(ref _subscriptionEditorName, value); }
        public string SubscriptionEditorKey { get => _subscriptionEditorKey; set => SetProperty(ref _subscriptionEditorKey, value); }
        public string SubscriptionEditorEndpoint { get => _subscriptionEditorEndpoint;
            set => Set(ref _subscriptionEditorEndpoint, value, dirty: false, then: UpdateEndpointRegionHint); }

        public string SubscriptionEditorRegionHint { get => _subscriptionEditorRegionHint; set => SetProperty(ref _subscriptionEditorRegionHint, value); }
        public string SubscriptionMessage { get => _subscriptionMessage; set => SetProperty(ref _subscriptionMessage, value); }
        public string TestAllResult { get => _testAllResult; set => SetProperty(ref _testAllResult, value); }
        public bool ShowTestAllResult { get => _showTestAllResult; set => SetProperty(ref _showTestAllResult, value); }
        public bool EnableRecording { get => _enableRecording; set => Set(ref _enableRecording, value); }
        public int RecordingMp3BitrateKbps { get => _recordingMp3BitrateKbps; set => Set(ref _recordingMp3BitrateKbps, value); }
        public string SessionDirectory { get => _sessionDirectory; set => Set(ref _sessionDirectory, value); }
        public string BatchStorageConnectionString { get => _batchStorageConnectionString; set => SetProperty(ref _batchStorageConnectionString, value); }
        public string BatchStorageStatus { get => _batchStorageStatus; set => SetProperty(ref _batchStorageStatus, value); }
        public bool BatchStorageIsValid { get => _batchStorageIsValid; set => SetProperty(ref _batchStorageIsValid, value); }
        public int BatchLogLevelIndex { get => _batchLogLevelIndex; set => Set(ref _batchLogLevelIndex, value); }
        public bool BatchForceRegeneration { get => _batchForceRegeneration; set => Set(ref _batchForceRegeneration, value); }
        public bool ContextMenuForceRegeneration { get => _contextMenuForceRegeneration; set => Set(ref _contextMenuForceRegeneration, value); }
        public bool EnableBatchSentenceSplit { get => _enableBatchSentenceSplit; set => Set(ref _enableBatchSentenceSplit, value); }
        public bool BatchSplitOnComma { get => _batchSplitOnComma; set => Set(ref _batchSplitOnComma, value); }
        public int BatchMaxChars { get => _batchMaxChars; set => Set(ref _batchMaxChars, value); }
        public double BatchMaxDuration { get => _batchMaxDuration; set => Set(ref _batchMaxDuration, value); }
        public int BatchPauseSplitMs { get => _batchPauseSplitMs; set => Set(ref _batchPauseSplitMs, value); }
        public bool UseSpeechSubtitleForReview { get => _useSpeechSubtitleForReview; set => Set(ref _useSpeechSubtitleForReview, value); }
        public bool FilterModalParticles { get => _filterModalParticles; set => Set(ref _filterModalParticles, value); }
        public bool IsAutoUpdateEnabled { get => _isAutoUpdateEnabled; set => Set(ref _isAutoUpdateEnabled, value); }
        public int MaxHistoryItems { get => _maxHistoryItems; set => Set(ref _maxHistoryItems, value); }
        public int RealtimeMaxLength { get => _realtimeMaxLength; set => Set(ref _realtimeMaxLength, value); }
        public int ChunkDurationMs { get => _chunkDurationMs; set => Set(ref _chunkDurationMs, value); }
        public bool EnableAutoTimeout { get => _enableAutoTimeout; set => Set(ref _enableAutoTimeout, value); }
        public int InitialSilenceTimeoutSeconds { get => _initialSilenceTimeoutSeconds; set => Set(ref _initialSilenceTimeoutSeconds, value); }
        public int EndSilenceTimeoutSeconds { get => _endSilenceTimeoutSeconds; set => Set(ref _endSilenceTimeoutSeconds, value); }
        public bool EnableNoResponseRestart { get => _enableNoResponseRestart; set => Set(ref _enableNoResponseRestart, value); }
        public int NoResponseRestartSeconds { get => _noResponseRestartSeconds; set => Set(ref _noResponseRestartSeconds, value); }
        public int AudioActivityThreshold { get => _audioActivityThreshold; set => Set(ref _audioActivityThreshold, value); }
        public double AudioLevelGain { get => _audioLevelGain; set => Set(ref _audioLevelGain, value); }
        public int AutoGainPresetIndex { get => _autoGainPresetIndex; set => Set(ref _autoGainPresetIndex, value); }
        public bool ShowReconnectMarker { get => _showReconnectMarker; set => Set(ref _showReconnectMarker, value); }
        public bool ExportSrt { get => _exportSrt; set => Set(ref _exportSrt, value); }
        public bool ExportVtt { get => _exportVtt; set => Set(ref _exportVtt, value); }
        public int DefaultFontSize { get => _defaultFontSize; set => Set(ref _defaultFontSize, value); }
        public int AiProviderTypeIndex { get => _aiProviderTypeIndex;
            set => Set(ref _aiProviderTypeIndex, value, then: () => {
                OnPropertyChanged(nameof(IsAzureProvider));
                OnPropertyChanged(nameof(IsOpenAiProvider));
            }); }

        public bool IsAzureProvider => AiProviderTypeIndex == 1;
        public bool IsOpenAiProvider => AiProviderTypeIndex == 0;

        public string AiApiEndpoint { get => _aiApiEndpoint; set => Set(ref _aiApiEndpoint, value); }
        public string AiApiKey { get => _aiApiKey; set => Set(ref _aiApiKey, value); }
        public string QuickModelName { get => _quickModelName; set => Set(ref _quickModelName, value); }
        public string SummaryModelName { get => _summaryModelName; set => Set(ref _summaryModelName, value); }
        public string QuickDeploymentName { get => _quickDeploymentName; set => Set(ref _quickDeploymentName, value); }
        public string SummaryDeploymentName { get => _summaryDeploymentName; set => Set(ref _summaryDeploymentName, value); }
        public string AiApiVersion { get => _aiApiVersion; set => Set(ref _aiApiVersion, value); }
        public int AiAzureAuthModeIndex { get => _aiAzureAuthModeIndex;
            set => Set(ref _aiAzureAuthModeIndex, value, then: () => {
                OnPropertyChanged(nameof(IsAadAuth));
                OnPropertyChanged(nameof(ShowApiKeyField));
            }); }

        public bool IsAadAuth => AiAzureAuthModeIndex == 1;
        public bool ShowApiKeyField => !IsAzureProvider || !IsAadAuth;

        public string AiAzureTenantId { get => _aiAzureTenantId; set => Set(ref _aiAzureTenantId, value); }
        public string AiAzureClientId { get => _aiAzureClientId; set => Set(ref _aiAzureClientId, value); }
        public bool SummaryEnableReasoning { get => _summaryEnableReasoning; set => Set(ref _summaryEnableReasoning, value); }
        public string InsightSystemPrompt { get => _insightSystemPrompt; set => Set(ref _insightSystemPrompt, value); }
        public string ReviewSystemPrompt { get => _reviewSystemPrompt; set => Set(ref _reviewSystemPrompt, value); }
        public string InsightUserContentTemplate { get => _insightUserContentTemplate; set => Set(ref _insightUserContentTemplate, value); }
        public string ReviewUserContentTemplate { get => _reviewUserContentTemplate; set => Set(ref _reviewUserContentTemplate, value); }
        public bool AutoInsightBufferOutput { get => _autoInsightBufferOutput; set => Set(ref _autoInsightBufferOutput, value); }
        public ObservableCollection<InsightPresetButton> PresetButtons { get => _presetButtons; set => SetProperty(ref _presetButtons, value); }
        public ObservableCollection<ReviewSheetPreset> ReviewSheets { get => _reviewSheets; set => SetProperty(ref _reviewSheets, value); }
        public string AutoSaveStatus { get => _autoSaveStatus; set => SetProperty(ref _autoSaveStatus, value); }

        public ICommand AddEndpointCommand { get; }
        public ICommand RemoveEndpointCommand { get; }
        public ICommand TestEndpointCommand { get; }
        public ICommand AddSubscriptionCommand { get; }
        public ICommand UpdateSubscriptionCommand { get; }
        public ICommand DeleteSubscriptionCommand { get; }
        public ICommand TestSubscriptionCommand { get; }
        public ICommand TestAllSubscriptionsCommand { get; }
        public ICommand ValidateBatchStorageCommand { get; }

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
            RefreshModelOptions();
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

        public void NotifyModelChanged()
        {
            SyncEndpointsToConfig();
            RefreshModelOptions();
            MarkDirty();
        }

        public void NotifyEndpointChanged()
        {
            if (SelectedEndpoint == null)
                return;

            var selectedId = SelectedEndpoint.Id;
            Endpoints = new ObservableCollection<AiEndpoint>(Endpoints);
            SelectedEndpoint = Endpoints.FirstOrDefault(e => e.Id == selectedId);

            SyncEndpointsToConfig();
            RefreshModelOptions();
            MarkDirty();
            _ = RefreshAiAuthStatusAsync();
        }

        public void NotifyPresetButtonsChanged()
        {
            OnPropertyChanged(nameof(PresetButtons));
            MarkDirty();
        }

        public void NotifyReviewSheetsChanged()
        {
            OnPropertyChanged(nameof(ReviewSheets));
            MarkDirty();
        }

        private void SyncEndpointsToConfig()
        {
            _config.Endpoints = Endpoints.ToList();
        }

        private void RefreshModelOptions()
        {
            var insightRef = SelectedInsightModel?.Reference;
            var summaryRef = SelectedSummaryModel?.Reference;
            var quickRef = SelectedQuickModel?.Reference;
            var reviewRef = SelectedReviewModel?.Reference;
            var imageRef = SelectedImageModel?.Reference;
            var videoRef = SelectedVideoModel?.Reference;

            TextModels = BuildModelOptions(ModelCapability.Text);
            ImageModels = BuildModelOptions(ModelCapability.Image);
            VideoModels = BuildModelOptions(ModelCapability.Video);

            SelectModelOption(insightRef, TextModels, v => _selectedInsightModel = v, nameof(SelectedInsightModel));
            SelectModelOption(summaryRef, TextModels, v => _selectedSummaryModel = v, nameof(SelectedSummaryModel));
            SelectModelOption(quickRef, TextModels, v => _selectedQuickModel = v, nameof(SelectedQuickModel));
            SelectModelOption(reviewRef, TextModels, v => _selectedReviewModel = v, nameof(SelectedReviewModel));
            SelectModelOption(imageRef, ImageModels, v => _selectedImageModel = v, nameof(SelectedImageModel));
            SelectModelOption(videoRef, VideoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));
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
            var match = reference == null ? null
                : options.FirstOrDefault(o => o.Reference.EndpointId == reference.EndpointId && o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }

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
            ((RelayCommand)TestAllSubscriptionsCommand).RaiseCanExecuteChanged();
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
            ((RelayCommand)TestAllSubscriptionsCommand).RaiseCanExecuteChanged();
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

        private string _aiTestStatus = "";
        private string _aiTestReasoning = "";
        private string _aiAuthStatus = "认证状态：未检测";

        public string AiTestStatus { get => _aiTestStatus; set => SetProperty(ref _aiTestStatus, value); }
        public string AiTestReasoning { get => _aiTestReasoning; set => SetProperty(ref _aiTestReasoning, value); }
        public string AiAuthStatus { get => _aiAuthStatus; set => SetProperty(ref _aiAuthStatus, value); }

        private async Task TestAiConnection()
        {
            var testInfo = await BuildAiTestContextAsync();
            if (testInfo == null)
            {
                AiTestStatus = "请先在“洞察模型选择”中选择一个可用模型";
                return;
            }

            var (testConfig, endpoint, tokenProvider) = testInfo.Value;

            if (!testConfig.IsValid)
            {
                AiTestStatus = "请填写必要的配置信息";
                return;
            }

            AiTestStatus = "正在连接...";
            AiTestReasoning = "";

            try
            {
                var service = new AiInsightService(tokenProvider);
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
                if (endpoint.ProviderType == AiProviderType.AzureOpenAi
                    && endpoint.AuthMode == AzureAuthMode.AAD
                    && ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase))
                {
                    AiTestStatus = $"AAD 鉴权失败(401)：请确认当前账号已授予 Azure OpenAI 访问权限（如 Cognitive Services OpenAI User），并检查终结点/部署名/租户是否匹配。原始错误: {ex.Message}";
                }
                else
                {
                    AiTestStatus = $"连接失败: {ex.Message}";
                }
            }
        }

        private async Task<(AiConfig config, AiEndpoint endpoint, AzureTokenProvider? tokenProvider)?> BuildAiTestContextAsync()
        {
            var selected = SelectedSummaryModel ?? SelectedInsightModel ?? SelectedQuickModel;
            var reference = selected?.Reference;
            if (reference == null)
                return null;

            var (endpoint, model) = _config.ResolveModel(reference);
            if (endpoint == null || model == null)
                return null;

            AzureTokenProvider? tokenProvider = null;

            var cfg = new AiConfig
            {
                ProviderType = endpoint.ProviderType,
                ApiEndpoint = endpoint.BaseUrl?.Trim() ?? "",
                ApiKey = endpoint.ApiKey?.Trim() ?? "",
                ApiVersion = string.IsNullOrWhiteSpace(endpoint.ApiVersion) ? "2024-02-01" : endpoint.ApiVersion.Trim(),
                AzureAuthMode = endpoint.AuthMode,
                AzureTenantId = endpoint.AzureTenantId ?? "",
                AzureClientId = endpoint.AzureClientId ?? "",
                SummaryEnableReasoning = SummaryEnableReasoning
            };

            if (endpoint.ProviderType == AiProviderType.AzureOpenAi && endpoint.AuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = new AzureTokenProvider(GetEndpointProfileKey(endpoint));
                var silentLoggedIn = await tokenProvider.TrySilentLoginAsync(
                    endpoint.AzureTenantId,
                    endpoint.AzureClientId);

                if (!silentLoggedIn)
                {
                    AiAuthStatus = "认证状态：AAD 未登录（请先在 AI 终结点中点击“登录”）";
                    return null;
                }

                var username = string.IsNullOrWhiteSpace(tokenProvider.Username) ? "已认证" : tokenProvider.Username;
                AiAuthStatus = $"认证状态：AAD 已登录（{username}）";
            }
            else if (endpoint.ProviderType == AiProviderType.AzureOpenAi)
            {
                AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                    ? "认证状态：API Key 未配置"
                    : "认证状态：API Key 已配置";
            }
            else
            {
                AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                    ? "认证状态：OpenAI 兼容 API Key 未配置"
                    : "认证状态：OpenAI 兼容 API Key 已配置";
            }

            if (endpoint.ProviderType == AiProviderType.AzureOpenAi)
            {
                var deployment = string.IsNullOrWhiteSpace(model.DeploymentName)
                    ? model.ModelId
                    : model.DeploymentName;
                cfg.QuickDeploymentName = deployment;
                cfg.SummaryDeploymentName = deployment;
            }
            else
            {
                cfg.QuickModelName = model.ModelId;
                cfg.SummaryModelName = model.ModelId;
            }

            ConfigViewHelper.ApplyModelDeploymentFallbacks(cfg);
            return (cfg, endpoint, tokenProvider);
        }

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        public async Task RefreshAiAuthStatusAsync()
        {
            try
            {
                var selected = SelectedSummaryModel ?? SelectedInsightModel ?? SelectedQuickModel;
                var reference = selected?.Reference;
                if (reference == null)
                {
                    AiAuthStatus = "认证状态：未选择模型";
                    return;
                }

                var (endpoint, _) = _config.ResolveModel(reference);
                if (endpoint == null)
                {
                    AiAuthStatus = "认证状态：当前模型对应终结点不存在";
                    return;
                }

                if (endpoint.ProviderType != AiProviderType.AzureOpenAi)
                {
                    AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                        ? "认证状态：OpenAI 兼容 API Key 未配置"
                        : "认证状态：OpenAI 兼容 API Key 已配置";
                    return;
                }

                if (endpoint.AuthMode != AzureAuthMode.AAD)
                {
                    AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                        ? "认证状态：API Key 未配置"
                        : "认证状态：API Key 已配置";
                    return;
                }

                var provider = new AzureTokenProvider(GetEndpointProfileKey(endpoint));
                var loggedIn = await provider.TrySilentLoginAsync(endpoint.AzureTenantId, endpoint.AzureClientId);
                AiAuthStatus = loggedIn
                    ? $"认证状态：AAD 已登录（{(string.IsNullOrWhiteSpace(provider.Username) ? "已认证" : provider.Username)}）"
                    : "认证状态：AAD 未登录（请先在 AI 终结点中点击“登录”）";
            }
            catch (Exception ex)
            {
                AiAuthStatus = $"认证状态检测失败：{ex.Message}";
            }
        }

        private bool Set<T>(ref T field, T value, bool dirty = true, Action? then = null,
            [CallerMemberName] string? name = null)
        {
            if (!SetProperty(ref field, value, name)) return false;
            then?.Invoke();
            if (dirty) MarkDirty();
            return true;
        }

        private void MarkDirty()
        {
            _isDirty = true;
            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(_ =>
                {
                    Dispatcher.UIThread.Post(() => _ = FlushSaveAsync());
                }, null, DebounceMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(DebounceMs, Timeout.Infinite);
            }
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

        private void ApplyToConfig()
        {
            ApplyToConfigCore();
        }

        private void ApplyToConfigCore()
        {
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

            _config.ExportSrtSubtitles = ExportSrt;
            _config.ExportVttSubtitles = ExportVtt;
            _config.DefaultFontSize = DefaultFontSize;
            _config.IsAutoUpdateEnabled = IsAutoUpdateEnabled;
            Controls.AdvancedRichTextBox.DefaultFontSizeValue = DefaultFontSize;

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

            ai.InsightModelRef = SelectedInsightModel?.Reference;
            ai.SummaryModelRef = SelectedSummaryModel?.Reference;
            ai.QuickModelRef = SelectedQuickModel?.Reference;
            ai.ReviewModelRef = SelectedReviewModel?.Reference;
            _config.AiConfig = ai;

            _config.MediaGenConfig.ImageModelRef = SelectedImageModel?.Reference;
            _config.MediaGenConfig.VideoModelRef = SelectedVideoModel?.Reference;

            var media = _config.MediaGenConfig;
            media.ImageSize = string.IsNullOrWhiteSpace(_imageSize) ? "1024x1024" : _imageSize;
            media.ImageQuality = string.IsNullOrWhiteSpace(_imageQuality) ? "medium" : _imageQuality;
            media.ImageFormat = string.IsNullOrWhiteSpace(_imageFormat) ? "png" : _imageFormat;
            media.ImageCount = Math.Clamp(_imageCount, 1, 10);

            media.VideoApiMode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            media.VideoAspectRatio = _videoAspectRatio ?? "16:9";
            media.VideoResolution = _videoResolution ?? "720p";
            media.VideoWidth = Math.Max(64, VideoWidth);
            media.VideoHeight = Math.Max(64, VideoHeight);
            media.VideoSeconds = Math.Clamp(_videoSeconds, 1, 120);
            media.VideoVariants = Math.Clamp(_videoVariants, 1, 4);
            media.VideoPollIntervalMs = Math.Clamp(VideoPollIntervalMs, 500, 60000);

            media.MaxLoadedSessionsInMemory = Math.Clamp(MaxLoadedSessionsInMemory, 1, 64);
            media.OutputDirectory = MediaOutputDirectory?.Trim() ?? "";

            if (SelectedImageModel?.Reference != null)
                media.ImageModel = SelectedImageModel.Reference.ModelId;
            if (SelectedVideoModel?.Reference != null)
                media.VideoModel = SelectedVideoModel.Reference.ModelId;

            SyncEndpointsToConfig();
        }
    }
}
