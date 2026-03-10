using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.EndpointTesting;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 配置中心编排 ViewModel（瘦壳）。
    /// 持有各分区 ViewModel，统一编排加载/保存、自动保存 debounce、模型列表刷新。
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly ConfigurationService _configService;
        private readonly ISettingsImportExportService _settingsImportExportService;
        private readonly Lazy<InsightSectionVM> _insightVm;
        private readonly Lazy<ReviewSectionVM> _reviewVm;
        private readonly Lazy<ImageGenSectionVM> _imageGenVm;
        private readonly Lazy<VideoGenSectionVM> _videoGenVm;
        private readonly Lazy<TransferSectionVM> _transferVm;
        private readonly Lazy<AboutSectionVM> _aboutVm;

        private AzureSpeechConfig _config = new();
        private Timer? _debounceTimer;
        private bool _isDirty;
        private string _autoSaveStatus = "";

        private const int DebounceMs = 500;

        public event Action<AzureSpeechConfig>? ConfigSaved;
        public event Action<string>? StatusNotificationRequested;

        // ═══ 分区 ViewModel ═══
        public SubscriptionSectionVM SubscriptionVM { get; }
        public EndpointsSectionVM EndpointsVM { get; }
        public StorageSectionVM StorageVM { get; }
        public RecognitionSectionVM RecognitionVM { get; }
        public TextSectionVM TextVM { get; }
        public InsightSectionVM InsightVM => _insightVm.Value;
        public ReviewSectionVM ReviewVM => _reviewVm.Value;
        public ImageGenSectionVM ImageGenVM => _imageGenVm.Value;
        public VideoGenSectionVM VideoGenVM => _videoGenVm.Value;
        public TransferSectionVM TransferVM => _transferVm.Value;
        public AboutSectionVM AboutVM => _aboutVm.Value;

        public SettingsViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator,
            IAiEndpointModelDiscoveryService modelDiscoveryService,
            IEndpointTemplateService endpointTemplateService,
            ISettingsImportExportService settingsImportExportService,
            ISettingsTransferFileService settingsTransferFileService,
            IModelRuntimeResolver modelRuntimeResolver,
            IAboutSectionService aboutSectionService,
            IEndpointBatchTestService endpointBatchTestService)
        {
            _configService = configService;
            _settingsImportExportService = settingsImportExportService;

            SubscriptionVM = new SubscriptionSectionVM(subscriptionValidator);
            EndpointsVM = new EndpointsSectionVM(modelDiscoveryService, endpointTemplateService, endpointBatchTestService);
            StorageVM = new StorageSectionVM();
            RecognitionVM = new RecognitionSectionVM();
            TextVM = new TextSectionVM();

            _insightVm = CreateLazySection(() => new InsightSectionVM(modelRuntimeResolver), ConfigureInsightSection);
            _reviewVm = CreateLazySection(() => new ReviewSectionVM(), ConfigureReviewSection);
            _imageGenVm = CreateLazySection(() => new ImageGenSectionVM(), ConfigureImageGenSection);
            _videoGenVm = CreateLazySection(() => new VideoGenSectionVM(), ConfigureVideoGenSection);
            _aboutVm = CreateLazySection(() => new AboutSectionVM(aboutSectionService, ReportStatus), ConfigureAboutSection);
            _transferVm = new Lazy<TransferSectionVM>(() => new TransferSectionVM(
                settingsTransferFileService,
                CreateExportPackage,
                CreateFullExportConfig,
                ImportPackageAsync,
                ImportFullConfigAsync,
                ReportStatus));

            SubscribeSection(SubscriptionVM);
            SubscribeSection(EndpointsVM);
            SubscribeSection(StorageVM);
            SubscribeSection(RecognitionVM);
            SubscribeSection(TextVM);

            EndpointsVM.EndpointsChanged += RefreshModelOptions;
            EndpointsVM.EndpointsChanged += () => _ = RefreshAiAuthStatusAsync();
            SubscriptionVM.StatusRequested += ReportStatus;
            EndpointsVM.StatusRequested += ReportStatus;
        }

        // ═══ 向后兼容的公共属性（转发到分区 ViewModel） ═══

        // — Subscription —
        public ObservableCollection<AzureSubscription> Subscriptions { get => SubscriptionVM.Subscriptions; set => SubscriptionVM.Subscriptions = value; }
        public AzureSubscription? SelectedSubscription { get => SubscriptionVM.SelectedSubscription; set => SubscriptionVM.SelectedSubscription = value; }
        public string SubscriptionEditorName { get => SubscriptionVM.SubscriptionEditorName; set => SubscriptionVM.SubscriptionEditorName = value; }
        public string SubscriptionEditorKey { get => SubscriptionVM.SubscriptionEditorKey; set => SubscriptionVM.SubscriptionEditorKey = value; }
        public string SubscriptionEditorEndpoint { get => SubscriptionVM.SubscriptionEditorEndpoint; set => SubscriptionVM.SubscriptionEditorEndpoint = value; }
        public string SubscriptionEditorRegionHint { get => SubscriptionVM.SubscriptionEditorRegionHint; set => SubscriptionVM.SubscriptionEditorRegionHint = value; }
        public string SubscriptionMessage { get => SubscriptionVM.SubscriptionMessage; set => SubscriptionVM.SubscriptionMessage = value; }
        public string TestAllResult { get => SubscriptionVM.TestAllResult; set => SubscriptionVM.TestAllResult = value; }
        public bool ShowTestAllResult { get => SubscriptionVM.ShowTestAllResult; set => SubscriptionVM.ShowTestAllResult = value; }
        public ICommand AddSubscriptionCommand => SubscriptionVM.AddSubscriptionCommand;
        public ICommand UpdateSubscriptionCommand => SubscriptionVM.UpdateSubscriptionCommand;
        public ICommand DeleteSubscriptionCommand => SubscriptionVM.DeleteSubscriptionCommand;
        public ICommand TestSubscriptionCommand => SubscriptionVM.TestSubscriptionCommand;
        public ICommand TestAllSubscriptionsCommand => SubscriptionVM.TestAllSubscriptionsCommand;

        // — Endpoints —
        public ObservableCollection<AiEndpoint> Endpoints { get => EndpointsVM.Endpoints; set => EndpointsVM.Endpoints = value; }
        public AiEndpoint? SelectedEndpoint { get => EndpointsVM.SelectedEndpoint; set => EndpointsVM.SelectedEndpoint = value; }
        public List<AiModelEntry>? SelectedEndpointModels => EndpointsVM.SelectedEndpointModels;
        public int SelectedEndpointAuthMode { get => EndpointsVM.SelectedEndpointAuthMode; set => EndpointsVM.SelectedEndpointAuthMode = value; }
        public bool IsSelectedEndpointAad => EndpointsVM.IsSelectedEndpointAad;
        public ICommand AddEndpointCommand => EndpointsVM.AddEndpointCommand;
        public ICommand RemoveEndpointCommand => EndpointsVM.RemoveEndpointCommand;
        public void AddModelToSelectedEndpoint(string modelId, string displayName, string groupName, string deploymentName, ModelCapability capabilities) => EndpointsVM.AddModelToSelectedEndpoint(modelId, displayName, groupName, deploymentName, capabilities);
        public void RemoveModelFromSelectedEndpoint(AiModelEntry model) => EndpointsVM.RemoveModelFromSelectedEndpoint(model);
        public void NotifyModelChanged() => EndpointsVM.NotifyModelChanged();
        public void NotifyEndpointChanged()
        {
            EndpointsVM.NotifyEndpointChanged();
            _ = RefreshAiAuthStatusAsync();
        }

        // — Storage —
        public bool EnableRecording { get => StorageVM.EnableRecording; set => StorageVM.EnableRecording = value; }
        public int RecordingMp3BitrateKbps { get => StorageVM.RecordingMp3BitrateKbps; set => StorageVM.RecordingMp3BitrateKbps = value; }
        public string SessionDirectory { get => StorageVM.SessionDirectory; set => StorageVM.SessionDirectory = value; }
        public string BatchStorageConnectionString { get => StorageVM.BatchStorageConnectionString; set => StorageVM.BatchStorageConnectionString = value; }
        public string BatchStorageStatus { get => StorageVM.BatchStorageStatus; set => StorageVM.BatchStorageStatus = value; }
        public bool BatchStorageIsValid { get => StorageVM.BatchStorageIsValid; set => StorageVM.BatchStorageIsValid = value; }
        public int BatchLogLevelIndex { get => StorageVM.BatchLogLevelIndex; set => StorageVM.BatchLogLevelIndex = value; }
        public bool BatchForceRegeneration { get => StorageVM.BatchForceRegeneration; set => StorageVM.BatchForceRegeneration = value; }
        public bool ContextMenuForceRegeneration { get => StorageVM.ContextMenuForceRegeneration; set => StorageVM.ContextMenuForceRegeneration = value; }
        public bool EnableBatchSentenceSplit { get => StorageVM.EnableBatchSentenceSplit; set => StorageVM.EnableBatchSentenceSplit = value; }
        public bool BatchSplitOnComma { get => StorageVM.BatchSplitOnComma; set => StorageVM.BatchSplitOnComma = value; }
        public int BatchMaxChars { get => StorageVM.BatchMaxChars; set => StorageVM.BatchMaxChars = value; }
        public double BatchMaxDuration { get => StorageVM.BatchMaxDuration; set => StorageVM.BatchMaxDuration = value; }
        public int BatchPauseSplitMs { get => StorageVM.BatchPauseSplitMs; set => StorageVM.BatchPauseSplitMs = value; }
        public ReviewSubtitleSourceMode ReviewSubtitleSourceMode { get => StorageVM.ReviewSubtitleSourceMode; set => StorageVM.ReviewSubtitleSourceMode = value; }
        public int ReviewSubtitleSourceModeIndex { get => StorageVM.ReviewSubtitleSourceModeIndex; set => StorageVM.ReviewSubtitleSourceModeIndex = value; }
        public string ReviewSubtitleSourceDisplayText => StorageVM.ReviewSubtitleSourceDisplayText;
        public string ReviewSubtitleSourceDescription => StorageVM.ReviewSubtitleSourceDescription;
        public ICommand ValidateBatchStorageCommand => StorageVM.ValidateBatchStorageCommand;

        // — Recognition —
        public bool FilterModalParticles { get => RecognitionVM.FilterModalParticles; set => RecognitionVM.FilterModalParticles = value; }
        public int MaxHistoryItems { get => RecognitionVM.MaxHistoryItems; set => RecognitionVM.MaxHistoryItems = value; }
        public int RealtimeMaxLength { get => RecognitionVM.RealtimeMaxLength; set => RecognitionVM.RealtimeMaxLength = value; }
        public int ChunkDurationMs { get => RecognitionVM.ChunkDurationMs; set => RecognitionVM.ChunkDurationMs = value; }
        public bool EnableAutoTimeout { get => RecognitionVM.EnableAutoTimeout; set => RecognitionVM.EnableAutoTimeout = value; }
        public int InitialSilenceTimeoutSeconds { get => RecognitionVM.InitialSilenceTimeoutSeconds; set => RecognitionVM.InitialSilenceTimeoutSeconds = value; }
        public int EndSilenceTimeoutSeconds { get => RecognitionVM.EndSilenceTimeoutSeconds; set => RecognitionVM.EndSilenceTimeoutSeconds = value; }
        public bool EnableNoResponseRestart { get => RecognitionVM.EnableNoResponseRestart; set => RecognitionVM.EnableNoResponseRestart = value; }
        public int NoResponseRestartSeconds { get => RecognitionVM.NoResponseRestartSeconds; set => RecognitionVM.NoResponseRestartSeconds = value; }
        public int AudioActivityThreshold { get => RecognitionVM.AudioActivityThreshold; set => RecognitionVM.AudioActivityThreshold = value; }
        public double AudioLevelGain { get => RecognitionVM.AudioLevelGain; set => RecognitionVM.AudioLevelGain = value; }
        public int AutoGainPresetIndex { get => RecognitionVM.AutoGainPresetIndex; set => RecognitionVM.AutoGainPresetIndex = value; }
        public bool ShowReconnectMarker { get => RecognitionVM.ShowReconnectMarker; set => RecognitionVM.ShowReconnectMarker = value; }

        // — Text —
        public bool ExportSrt { get => TextVM.ExportSrt; set => TextVM.ExportSrt = value; }
        public bool ExportVtt { get => TextVM.ExportVtt; set => TextVM.ExportVtt = value; }
        public int DefaultFontSize { get => TextVM.DefaultFontSize; set => TextVM.DefaultFontSize = value; }

        // — Insight —
        public List<ModelOption> TextModels { get => InsightVM.TextModels; set => InsightVM.TextModels = value; }
        public ModelOption? SelectedInsightModel { get => InsightVM.SelectedInsightModel; set => InsightVM.SelectedInsightModel = value; }
        public ModelOption? SelectedSummaryModel { get => InsightVM.SelectedSummaryModel; set => InsightVM.SelectedSummaryModel = value; }
        public ModelOption? SelectedQuickModel { get => InsightVM.SelectedQuickModel; set => InsightVM.SelectedQuickModel = value; }
        public bool SummaryEnableReasoning { get => InsightVM.SummaryEnableReasoning; set => InsightVM.SummaryEnableReasoning = value; }
        public string InsightSystemPrompt { get => InsightVM.InsightSystemPrompt; set => InsightVM.InsightSystemPrompt = value; }
        public string InsightUserContentTemplate { get => InsightVM.InsightUserContentTemplate; set => InsightVM.InsightUserContentTemplate = value; }
        public bool AutoInsightBufferOutput { get => InsightVM.AutoInsightBufferOutput; set => InsightVM.AutoInsightBufferOutput = value; }
        public ObservableCollection<InsightPresetButton> PresetButtons { get => InsightVM.PresetButtons; set => InsightVM.PresetButtons = value; }
        public string AiTestStatus { get => InsightVM.AiTestStatus; set => InsightVM.AiTestStatus = value; }
        public string AiTestReasoning { get => InsightVM.AiTestReasoning; set => InsightVM.AiTestReasoning = value; }
        public string AiAuthStatus { get => InsightVM.AiAuthStatus; set => InsightVM.AiAuthStatus = value; }
        public ICommand TestEndpointCommand => InsightVM.TestEndpointCommand;
        public void NotifyPresetButtonsChanged() => InsightVM.NotifyPresetButtonsChanged();

        // — Review —
        public List<ModelOption> ReviewTextModels { get => ReviewVM.TextModels; set => ReviewVM.TextModels = value; }
        public ModelOption? SelectedReviewModel { get => ReviewVM.SelectedReviewModel; set => ReviewVM.SelectedReviewModel = value; }
        public string ReviewSystemPrompt { get => ReviewVM.ReviewSystemPrompt; set => ReviewVM.ReviewSystemPrompt = value; }
        public string ReviewUserContentTemplate { get => ReviewVM.ReviewUserContentTemplate; set => ReviewVM.ReviewUserContentTemplate = value; }
        public ObservableCollection<ReviewSheetPreset> ReviewSheets { get => ReviewVM.ReviewSheets; set => ReviewVM.ReviewSheets = value; }
        public void NotifyReviewSheetsChanged() => ReviewVM.NotifyReviewSheetsChanged();

        // — ImageGen —
        public List<ModelOption> ImageModels { get => ImageGenVM.ImageModels; set => ImageGenVM.ImageModels = value; }
        public ModelOption? SelectedImageModel { get => ImageGenVM.SelectedImageModel; set => ImageGenVM.SelectedImageModel = value; }
        public List<string> ImageSizeOptions => ImageGenVM.ImageSizeOptions;
        public List<string> ImageQualityOptions => ImageGenVM.ImageQualityOptions;
        public List<string> ImageFormatOptions => ImageGenVM.ImageFormatOptions;
        public List<int> ImageCountOptions => ImageGenVM.ImageCountOptions;
        public string ImageSize { get => ImageGenVM.ImageSize; set => ImageGenVM.ImageSize = value; }
        public string ImageQuality { get => ImageGenVM.ImageQuality; set => ImageGenVM.ImageQuality = value; }
        public string ImageFormat { get => ImageGenVM.ImageFormat; set => ImageGenVM.ImageFormat = value; }
        public int ImageCount { get => ImageGenVM.ImageCount; set => ImageGenVM.ImageCount = value; }

        // — VideoGen —
        public List<ModelOption> VideoModels { get => VideoGenVM.VideoModels; set => VideoGenVM.VideoModels = value; }
        public ModelOption? SelectedVideoModel { get => VideoGenVM.SelectedVideoModel; set => VideoGenVM.SelectedVideoModel = value; }
        public List<string> VideoAspectRatioOptions => VideoGenVM.VideoAspectRatioOptions;
        public List<string> VideoResolutionOptions => VideoGenVM.VideoResolutionOptions;
        public List<int> VideoSecondsOptions => VideoGenVM.VideoSecondsOptions;
        public List<int> VideoVariantsOptions => VideoGenVM.VideoVariantsOptions;
        public int VideoApiModeIndex { get => VideoGenVM.VideoApiModeIndex; set => VideoGenVM.VideoApiModeIndex = value; }
        public string VideoAspectRatio { get => VideoGenVM.VideoAspectRatio; set => VideoGenVM.VideoAspectRatio = value; }
        public string VideoResolution { get => VideoGenVM.VideoResolution; set => VideoGenVM.VideoResolution = value; }
        public int VideoWidth => VideoGenVM.VideoWidth;
        public int VideoHeight => VideoGenVM.VideoHeight;
        public int VideoSeconds { get => VideoGenVM.VideoSeconds; set => VideoGenVM.VideoSeconds = value; }
        public int VideoVariants { get => VideoGenVM.VideoVariants; set => VideoGenVM.VideoVariants = value; }
        public int VideoPollIntervalMs { get => VideoGenVM.VideoPollIntervalMs; set => VideoGenVM.VideoPollIntervalMs = value; }
        public int MaxLoadedSessionsInMemory { get => VideoGenVM.MaxLoadedSessionsInMemory; set => VideoGenVM.MaxLoadedSessionsInMemory = value; }
        public string MediaOutputDirectory { get => VideoGenVM.MediaOutputDirectory; set => VideoGenVM.MediaOutputDirectory = value; }

        // — About —
        public bool IsAutoUpdateEnabled { get => AboutVM.IsAutoUpdateEnabled; set => AboutVM.IsAutoUpdateEnabled = value; }

        // — Auto-save status —
        public string AutoSaveStatus { get => _autoSaveStatus; set => SetProperty(ref _autoSaveStatus, value); }

        // ═══ 初始化与加载 ═══

        public void Initialize(AzureSpeechConfig config)
        {
            _config = config;
            LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            SubscriptionVM.Config = _config;
            EndpointsVM.Config = _config;
            StorageVM.Config = _config;

            SubscriptionVM.LoadFrom(_config);
            EndpointsVM.LoadFrom(_config);
            StorageVM.LoadFrom(_config);
            RecognitionVM.LoadFrom(_config);
            TextVM.LoadFrom(_config);
            RecognitionVM.SelectModels(
                _config,
                BuildModelOptions(ModelCapability.SpeechToText),
                BuildModelOptions(ModelCapability.TextToSpeech));

            if (_insightVm.IsValueCreated)
            {
                ConfigureInsightSection(_insightVm.Value);
            }

            if (_reviewVm.IsValueCreated)
            {
                ConfigureReviewSection(_reviewVm.Value);
            }

            if (_imageGenVm.IsValueCreated)
            {
                ConfigureImageGenSection(_imageGenVm.Value);
            }

            if (_videoGenVm.IsValueCreated)
            {
                ConfigureVideoGenSection(_videoGenVm.Value);
            }

            if (_aboutVm.IsValueCreated)
            {
                ConfigureAboutSection(_aboutVm.Value);
            }

            _ = RefreshAiAuthStatusAsync();
        }

        // ═══ 模型列表管理 ═══

        private void RefreshModelOptions()
        {
            if (_insightVm.IsValueCreated)
            {
                _insightVm.Value.RefreshModels(BuildModelOptions(ModelCapability.Text));
            }

            if (_reviewVm.IsValueCreated)
            {
                _reviewVm.Value.RefreshModels(BuildModelOptions(ModelCapability.Text));
            }

            if (_imageGenVm.IsValueCreated)
            {
                _imageGenVm.Value.RefreshModels(BuildModelOptions(ModelCapability.Image));
            }

            if (_videoGenVm.IsValueCreated)
            {
                _videoGenVm.Value.RefreshModels(BuildModelOptions(ModelCapability.Video));
            }

            RecognitionVM.RefreshModels(
                BuildModelOptions(ModelCapability.SpeechToText),
                BuildModelOptions(ModelCapability.TextToSpeech));
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

        public async Task RefreshAiAuthStatusAsync()
        {
            if (_insightVm.IsValueCreated)
            {
                await _insightVm.Value.RefreshAiAuthStatusAsync();
            }
        }

        public void ReportStatus(string message)
        {
            AutoSaveStatus = message;
            StatusNotificationRequested?.Invoke(message);
        }

        private Lazy<T> CreateLazySection<T>(Func<T> factory, Action<T> configure)
            where T : class, ISettingsSection, INotifyPropertyChanged
        {
            return new Lazy<T>(() =>
            {
                var section = factory();
                SubscribeSection(section);
                configure(section);
                return section;
            });
        }

        private void SubscribeSection(INotifyPropertyChanged section)
        {
            SubscribeSectionPropertyForwarding(section);

            if (section is ISettingsSection settingsSection)
            {
                settingsSection.Changed += MarkDirty;
            }
        }

        private void ConfigureInsightSection(InsightSectionVM section)
        {
            section.Config = _config;
            section.LoadFrom(_config);
            section.SelectModels(_config.AiConfig ?? new AiConfig(), BuildModelOptions(ModelCapability.Text));
        }

        private void ConfigureReviewSection(ReviewSectionVM section)
        {
            section.LoadFrom(_config);
            section.SelectModels(_config.AiConfig ?? new AiConfig(), BuildModelOptions(ModelCapability.Text));
        }

        private void ConfigureImageGenSection(ImageGenSectionVM section)
        {
            section.LoadFrom(_config);
            section.SelectModels(_config.MediaGenConfig.ImageModelRef, BuildModelOptions(ModelCapability.Image));
        }

        private void ConfigureVideoGenSection(VideoGenSectionVM section)
        {
            section.Config = _config;
            section.LoadFrom(_config);
            section.SelectModels(_config.MediaGenConfig.VideoModelRef, BuildModelOptions(ModelCapability.Video));
        }

        private void ConfigureAboutSection(AboutSectionVM section)
        {
            section.LoadFrom(_config);
        }

        private void SubscribeSectionPropertyForwarding(INotifyPropertyChanged section)
        {
            section.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.PropertyName))
                {
                    OnPropertyChanged(args.PropertyName);

                    if (section is ReviewSectionVM && args.PropertyName == nameof(ReviewSectionVM.TextModels))
                    {
                        OnPropertyChanged(nameof(ReviewTextModels));
                    }
                }
            };
        }

        // ═══ 自动保存 (debounce) ═══

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
            if (!_isDirty)
            {
                return;
            }

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

        public SettingsTransferPackage CreateExportPackage()
        {
            PauseAutoSave();
            ApplyToConfig();
            AutoSaveStatus = "✓ 已生成可导出的基本AI配置";
            return _settingsImportExportService.CreateExportPackage(_config);
        }

        public AzureSpeechConfig CreateFullExportConfig()
        {
            PauseAutoSave();
            ApplyToConfig();
            AutoSaveStatus = "✓ 已生成可导出的完整配置";
            return _settingsImportExportService.CreateFullExportConfig(_config);
        }

        public async Task ImportPackageAsync(SettingsTransferPackage package)
        {
            PauseAutoSave();

            try
            {
                ApplyToConfig();
                _config = _settingsImportExportService.ApplyImportPackage(_config, package);
                LoadFromConfig();
                await _configService.SaveConfigAsync(_config);
                AutoSaveStatus = BuildImportSuccessStatus(package);
                ConfigSaved?.Invoke(_config);
            }
            catch (Exception ex)
            {
                AutoSaveStatus = $"导入失败: {ex.Message}";
                throw;
            }
        }

        public async Task ImportFullConfigAsync(AzureSpeechConfig config)
        {
            PauseAutoSave();

            try
            {
                ApplyToConfig();
                _config = _settingsImportExportService.NormalizeImportedFullConfig(config);
                LoadFromConfig();
                await _configService.SaveConfigAsync(_config);
                AutoSaveStatus = "✓ 已导入完整配置；AAD 相关字段与 AAD 认证端点已自动忽略";
                ConfigSaved?.Invoke(_config);
            }
            catch (Exception ex)
            {
                AutoSaveStatus = $"导入失败: {ex.Message}";
                throw;
            }
        }

        private static string BuildImportSuccessStatus(SettingsTransferPackage package)
        {
            return package.Version switch
            {
                1 => "✓ 已导入 v1 旧版资源级配置；废弃字段已自动忽略，当前支持的终结点与模型引用已生效",
                2 => "✓ 已导入 v2 资源级配置；当前终结点类型、模型清单与模型引用已生效",
                _ => $"✓ 已导入 v{package.Version} 资源级配置"
            };
        }

        private void PauseAutoSave()
        {
            _isDirty = false;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void ApplyToConfig()
        {
            SubscriptionVM.ApplyTo(_config);
            EndpointsVM.ApplyTo(_config);
            StorageVM.ApplyTo(_config);
            RecognitionVM.ApplyTo(_config);
            TextVM.ApplyTo(_config);

            if (_insightVm.IsValueCreated)
            {
                _insightVm.Value.ApplyTo(_config);
            }

            if (_reviewVm.IsValueCreated)
            {
                _reviewVm.Value.ApplyTo(_config);
            }

            if (_imageGenVm.IsValueCreated)
            {
                _imageGenVm.Value.ApplyTo(_config);
            }

            if (_videoGenVm.IsValueCreated)
            {
                _videoGenVm.Value.ApplyTo(_config);
            }

            if (_aboutVm.IsValueCreated)
            {
                _aboutVm.Value.ApplyTo(_config);
            }

            EndpointsVM.SyncEndpointsToConfig();
        }
    }
}
