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
        private AzureSpeechConfig _config = new();

        private Timer? _debounceTimer;
        private bool _isDirty;
        private string _autoSaveStatus = "";
        private const int DebounceMs = 500;

        // ═══ 分区 ViewModel ═══
        public SubscriptionSectionVM SubscriptionVM { get; }
        public EndpointsSectionVM EndpointsVM { get; }
        public StorageSectionVM StorageVM { get; }
        public RecognitionSectionVM RecognitionVM { get; }
        public TextSectionVM TextVM { get; }
        public InsightSectionVM InsightVM { get; }
        public ReviewSectionVM ReviewVM { get; }
        public ImageGenSectionVM ImageGenVM { get; }
        public VideoGenSectionVM VideoGenVM { get; }
        public AboutSectionVM AboutVM { get; }

        public event Action<AzureSpeechConfig>? ConfigSaved;

        public SettingsViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator,
            IAiEndpointModelDiscoveryService modelDiscoveryService,
            ISettingsImportExportService settingsImportExportService,
            IModelRuntimeResolver modelRuntimeResolver)
        {
            _configService = configService;
            _settingsImportExportService = settingsImportExportService;

            // 创建分区 ViewModel
            SubscriptionVM = new SubscriptionSectionVM(subscriptionValidator);
            EndpointsVM = new EndpointsSectionVM(modelDiscoveryService);
            StorageVM = new StorageSectionVM();
            RecognitionVM = new RecognitionSectionVM();
            TextVM = new TextSectionVM();
            InsightVM = new InsightSectionVM(modelRuntimeResolver);
            ReviewVM = new ReviewSectionVM();
            ImageGenVM = new ImageGenSectionVM();
            VideoGenVM = new VideoGenSectionVM();
            AboutVM = new AboutSectionVM();

            SubscribeSectionPropertyForwarding(SubscriptionVM);
            SubscribeSectionPropertyForwarding(EndpointsVM);
            SubscribeSectionPropertyForwarding(StorageVM);
            SubscribeSectionPropertyForwarding(RecognitionVM);
            SubscribeSectionPropertyForwarding(TextVM);
            SubscribeSectionPropertyForwarding(InsightVM);
            SubscribeSectionPropertyForwarding(ReviewVM);
            SubscribeSectionPropertyForwarding(ImageGenVM);
            SubscribeSectionPropertyForwarding(VideoGenVM);
            SubscribeSectionPropertyForwarding(AboutVM);

            // 统一订阅所有分区的 Changed 事件
            SubscriptionVM.Changed += MarkDirty;
            EndpointsVM.Changed += MarkDirty;
            StorageVM.Changed += MarkDirty;
            RecognitionVM.Changed += MarkDirty;
            TextVM.Changed += MarkDirty;
            InsightVM.Changed += MarkDirty;
            ReviewVM.Changed += MarkDirty;
            ImageGenVM.Changed += MarkDirty;
            VideoGenVM.Changed += MarkDirty;
            AboutVM.Changed += MarkDirty;

            // 终结点变更 → 刷新模型列表
            EndpointsVM.EndpointsChanged += RefreshModelOptions;
            EndpointsVM.EndpointsChanged += () => _ = RefreshAiAuthStatusAsync();
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
        public void NotifyEndpointChanged() { EndpointsVM.NotifyEndpointChanged(); _ = RefreshAiAuthStatusAsync(); }

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
        public bool UseSpeechSubtitleForReview { get => StorageVM.UseSpeechSubtitleForReview; set => StorageVM.UseSpeechSubtitleForReview = value; }
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
            // 注入配置引用到需要的分区
            SubscriptionVM.Config = _config;
            EndpointsVM.Config = _config;
            StorageVM.Config = _config;
            InsightVM.Config = _config;
            VideoGenVM.Config = _config;

            // 分发加载到各分区
            SubscriptionVM.LoadFrom(_config);
            EndpointsVM.LoadFrom(_config);
            StorageVM.LoadFrom(_config);
            RecognitionVM.LoadFrom(_config);
            TextVM.LoadFrom(_config);
            InsightVM.LoadFrom(_config);
            ReviewVM.LoadFrom(_config);
            ImageGenVM.LoadFrom(_config);
            VideoGenVM.LoadFrom(_config);
            AboutVM.LoadFrom(_config);

            var ai = _config.AiConfig ?? new AiConfig();

            // 构建模型列表并分发到各分区
            var textModels = BuildModelOptions(ModelCapability.Text);
            var imageModels = BuildModelOptions(ModelCapability.Image);
            var videoModels = BuildModelOptions(ModelCapability.Video);

            InsightVM.SelectModels(ai, textModels);
            ReviewVM.SelectModels(ai, textModels);
            ImageGenVM.SelectModels(_config.MediaGenConfig.ImageModelRef, imageModels);
            VideoGenVM.SelectModels(_config.MediaGenConfig.VideoModelRef, videoModels);

            _ = RefreshAiAuthStatusAsync();
        }

        // ═══ 模型列表管理 ═══

        private void RefreshModelOptions()
        {
            var textModels = BuildModelOptions(ModelCapability.Text);
            var imageModels = BuildModelOptions(ModelCapability.Image);
            var videoModels = BuildModelOptions(ModelCapability.Video);

            InsightVM.RefreshModels(textModels);
            ReviewVM.RefreshModels(textModels);
            ImageGenVM.RefreshModels(imageModels);
            VideoGenVM.RefreshModels(videoModels);
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

        public async Task RefreshAiAuthStatusAsync() => await InsightVM.RefreshAiAuthStatusAsync();

        private void SubscribeSectionPropertyForwarding(INotifyPropertyChanged section)
        {
            section.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.PropertyName))
                {
                    OnPropertyChanged(args.PropertyName);
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

        public SettingsTransferPackage CreateExportPackage()
        {
            PauseAutoSave();
            ApplyToConfig();
            AutoSaveStatus = "✓ 已生成可导出的资源配置";
            return _settingsImportExportService.CreateExportPackage(_config);
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
                AutoSaveStatus = "✓ 资源配置已导入并立即生效";
                ConfigSaved?.Invoke(_config);
            }
            catch (Exception ex)
            {
                AutoSaveStatus = $"导入失败: {ex.Message}";
                throw;
            }
        }

        private void PauseAutoSave()
        {
            _isDirty = false;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void ApplyToConfig()
        {
            // 收集各分区状态到配置模型
            SubscriptionVM.ApplyTo(_config);
            EndpointsVM.ApplyTo(_config);
            StorageVM.ApplyTo(_config);
            RecognitionVM.ApplyTo(_config);
            TextVM.ApplyTo(_config);
            InsightVM.ApplyTo(_config);
            ReviewVM.ApplyTo(_config);
            ImageGenVM.ApplyTo(_config);
            VideoGenVM.ApplyTo(_config);
            AboutVM.ApplyTo(_config);

            EndpointsVM.SyncEndpointsToConfig();
        }
    }
}
