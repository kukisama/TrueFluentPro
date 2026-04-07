using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels.Settings
{
    public class RecognitionSectionVM : SettingsSectionBase
    {
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
        private int _audioPreProcessorPluginIndex;
        private bool _webRtcAecEnabled = true;
        private bool _webRtcAecMobileMode;
        private int _webRtcAecLatencyMs = 40;
        private bool _webRtcNoiseSuppressionEnabled = true;
        private int _webRtcNoiseSuppressionLevel = 2;
        private bool _webRtcAgc1Enabled = true;
        private bool _webRtcAgc2Enabled;
        private int _webRtcAgcMode = 1;
        private int _webRtcAgcTargetLevelDbfs = -3;
        private int _webRtcAgcCompressionGainDb = 9;
        private bool _webRtcAgcLimiterEnabled = true;
        private bool _webRtcHighPassFilterEnabled = true;
        private bool _webRtcPreAmpEnabled;
        private double _webRtcPreAmpGain = 1.0;
        private string _webRtcPluginStatus = "未启用 WebRTC APM";
        private bool _webRtcPluginStatusHealthy;
        private bool _enableMasAudioProcessing;
        private bool _masEchoCancellationEnabled = true;
        private bool _masNoiseSuppressionEnabled = true;
        private bool _showReconnectMarker = true;
        private List<ModelOption> _speechToTextModels = new();
        private List<ModelOption> _textToSpeechModels = new();
        private ModelOption? _selectedRealtimeTranscriptionModel;
        private ModelOption? _selectedBatchTranscriptionModel;
        private ModelOption? _selectedTextToSpeechModel;

        public RecognitionSectionVM()
        {
            TestWebRtcPluginCommand = new RelayCommand(_ => RefreshWebRtcPluginStatus(manual: true), _ => IsWebRtcPluginSelected);
        }

        public bool FilterModalParticles { get => _filterModalParticles; set => Set(ref _filterModalParticles, value); }
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
        public int AudioPreProcessorPluginIndex
        {
            get => _audioPreProcessorPluginIndex;
            set => Set(ref _audioPreProcessorPluginIndex, value, then: () =>
            {
                OnPropertyChanged(nameof(IsWebRtcPluginSelected));
                ((RelayCommand)TestWebRtcPluginCommand).RaiseCanExecuteChanged();
                RefreshWebRtcPluginStatus(manual: false);
            });
        }
        public bool IsWebRtcPluginSelected => AudioPreProcessorPluginIndex == (int)AudioPreProcessorPluginType.WebRtcApm;
        public bool WebRtcAecEnabled { get => _webRtcAecEnabled; set => Set(ref _webRtcAecEnabled, value); }
        public bool WebRtcAecMobileMode { get => _webRtcAecMobileMode; set => Set(ref _webRtcAecMobileMode, value); }
        public int WebRtcAecLatencyMs { get => _webRtcAecLatencyMs; set => Set(ref _webRtcAecLatencyMs, value); }
        public bool WebRtcNoiseSuppressionEnabled { get => _webRtcNoiseSuppressionEnabled; set => Set(ref _webRtcNoiseSuppressionEnabled, value); }
        public int WebRtcNoiseSuppressionLevel { get => _webRtcNoiseSuppressionLevel; set => Set(ref _webRtcNoiseSuppressionLevel, value); }
        public bool WebRtcAgc1Enabled { get => _webRtcAgc1Enabled; set => Set(ref _webRtcAgc1Enabled, value); }
        public bool WebRtcAgc2Enabled { get => _webRtcAgc2Enabled; set => Set(ref _webRtcAgc2Enabled, value); }
        public int WebRtcAgcMode { get => _webRtcAgcMode; set => Set(ref _webRtcAgcMode, value); }
        public int WebRtcAgcTargetLevelDbfs { get => _webRtcAgcTargetLevelDbfs; set => Set(ref _webRtcAgcTargetLevelDbfs, value); }
        public int WebRtcAgcCompressionGainDb { get => _webRtcAgcCompressionGainDb; set => Set(ref _webRtcAgcCompressionGainDb, value); }
        public bool WebRtcAgcLimiterEnabled { get => _webRtcAgcLimiterEnabled; set => Set(ref _webRtcAgcLimiterEnabled, value); }
        public bool WebRtcHighPassFilterEnabled { get => _webRtcHighPassFilterEnabled; set => Set(ref _webRtcHighPassFilterEnabled, value); }
        public bool WebRtcPreAmpEnabled { get => _webRtcPreAmpEnabled; set => Set(ref _webRtcPreAmpEnabled, value); }
        public double WebRtcPreAmpGain { get => _webRtcPreAmpGain; set => Set(ref _webRtcPreAmpGain, value); }
        public string WebRtcPluginStatus { get => _webRtcPluginStatus; set => SetProperty(ref _webRtcPluginStatus, value); }
        public bool WebRtcPluginStatusHealthy { get => _webRtcPluginStatusHealthy; set => SetProperty(ref _webRtcPluginStatusHealthy, value); }
        public bool EnableMasAudioProcessing { get => _enableMasAudioProcessing; set => Set(ref _enableMasAudioProcessing, value); }
        public bool MasEchoCancellationEnabled { get => _masEchoCancellationEnabled; set => Set(ref _masEchoCancellationEnabled, value); }
        public bool MasNoiseSuppressionEnabled { get => _masNoiseSuppressionEnabled; set => Set(ref _masNoiseSuppressionEnabled, value); }
        public bool ShowReconnectMarker { get => _showReconnectMarker; set => Set(ref _showReconnectMarker, value); }
        public List<ModelOption> SpeechToTextModels { get => _speechToTextModels; set => SetProperty(ref _speechToTextModels, value); }
        public List<ModelOption> TextToSpeechModels { get => _textToSpeechModels; set => SetProperty(ref _textToSpeechModels, value); }
        public bool HasSpeechToTextModels => SpeechToTextModels.Count > 0;
        public bool HasTextToSpeechModels => TextToSpeechModels.Count > 0;
        public ModelOption? SelectedRealtimeTranscriptionModel { get => _selectedRealtimeTranscriptionModel; set => Set(ref _selectedRealtimeTranscriptionModel, value); }
        public ModelOption? SelectedBatchTranscriptionModel { get => _selectedBatchTranscriptionModel; set => Set(ref _selectedBatchTranscriptionModel, value); }
        public ModelOption? SelectedTextToSpeechModel { get => _selectedTextToSpeechModel; set => Set(ref _selectedTextToSpeechModel, value); }
        public ICommand TestWebRtcPluginCommand { get; }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            FilterModalParticles = config.FilterModalParticles;
            MaxHistoryItems = config.MaxHistoryItems;
            RealtimeMaxLength = config.RealtimeMaxLength;
            ChunkDurationMs = config.ChunkDurationMs;
            EnableAutoTimeout = config.EnableAutoTimeout;
            InitialSilenceTimeoutSeconds = config.InitialSilenceTimeoutSeconds;
            EndSilenceTimeoutSeconds = config.EndSilenceTimeoutSeconds;
            EnableNoResponseRestart = config.EnableNoResponseRestart;
            NoResponseRestartSeconds = config.NoResponseRestartSeconds;
            AudioActivityThreshold = config.AudioActivityThreshold;
            AudioLevelGain = config.AudioLevelGain;
            AutoGainPresetIndex = config.AutoGainEnabled ? (int)config.AutoGainPreset : 0;
            AudioPreProcessorPluginIndex = (int)config.AudioPreProcessorPlugin;
            WebRtcAecEnabled = config.WebRtcAecEnabled;
            WebRtcAecMobileMode = config.WebRtcAecMobileMode;
            WebRtcAecLatencyMs = config.WebRtcAecLatencyMs;
            WebRtcNoiseSuppressionEnabled = config.WebRtcNoiseSuppressionEnabled;
            WebRtcNoiseSuppressionLevel = config.WebRtcNoiseSuppressionLevel;
            WebRtcAgc1Enabled = config.WebRtcAgc1Enabled;
            WebRtcAgc2Enabled = config.WebRtcAgc2Enabled;
            WebRtcAgcMode = config.WebRtcAgcMode;
            WebRtcAgcTargetLevelDbfs = config.WebRtcAgcTargetLevelDbfs;
            WebRtcAgcCompressionGainDb = config.WebRtcAgcCompressionGainDb;
            WebRtcAgcLimiterEnabled = config.WebRtcAgcLimiterEnabled;
            WebRtcHighPassFilterEnabled = config.WebRtcHighPassFilterEnabled;
            WebRtcPreAmpEnabled = config.WebRtcPreAmpEnabled;
            WebRtcPreAmpGain = config.WebRtcPreAmpGain;
            EnableMasAudioProcessing = config.EnableMasAudioProcessing;
            MasEchoCancellationEnabled = config.MasEchoCancellationEnabled;
            MasNoiseSuppressionEnabled = config.MasNoiseSuppressionEnabled;
            ShowReconnectMarker = config.ShowReconnectMarkerInSubtitle;
            RefreshWebRtcPluginStatus(manual: false);
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.FilterModalParticles = FilterModalParticles;
            config.MaxHistoryItems = MaxHistoryItems;
            config.RealtimeMaxLength = RealtimeMaxLength;
            config.ChunkDurationMs = ChunkDurationMs;
            config.EnableAutoTimeout = EnableAutoTimeout;
            config.InitialSilenceTimeoutSeconds = InitialSilenceTimeoutSeconds;
            config.EndSilenceTimeoutSeconds = EndSilenceTimeoutSeconds;
            config.EnableNoResponseRestart = EnableNoResponseRestart;
            config.NoResponseRestartSeconds = NoResponseRestartSeconds;
            config.AudioActivityThreshold = AudioActivityThreshold;
            config.AudioLevelGain = AudioLevelGain;
            var presetIndex = Math.Clamp(AutoGainPresetIndex, 0, 3);
            config.AutoGainEnabled = presetIndex > 0;
            config.AutoGainPreset = (AutoGainPreset)presetIndex;
            config.AudioPreProcessorPlugin = (AudioPreProcessorPluginType)Math.Clamp(AudioPreProcessorPluginIndex, 0, 1);
            config.WebRtcAecEnabled = WebRtcAecEnabled;
            config.WebRtcAecMobileMode = WebRtcAecMobileMode;
            config.WebRtcAecLatencyMs = Math.Clamp(WebRtcAecLatencyMs, 0, 500);
            config.WebRtcNoiseSuppressionEnabled = WebRtcNoiseSuppressionEnabled;
            config.WebRtcNoiseSuppressionLevel = Math.Clamp(WebRtcNoiseSuppressionLevel, 0, 3);
            config.WebRtcAgc1Enabled = WebRtcAgc1Enabled;
            config.WebRtcAgc2Enabled = WebRtcAgc2Enabled;
            config.WebRtcAgcMode = Math.Clamp(WebRtcAgcMode, 0, 2);
            config.WebRtcAgcTargetLevelDbfs = Math.Clamp(WebRtcAgcTargetLevelDbfs, -31, 0);
            config.WebRtcAgcCompressionGainDb = Math.Clamp(WebRtcAgcCompressionGainDb, 0, 90);
            config.WebRtcAgcLimiterEnabled = WebRtcAgcLimiterEnabled;
            config.WebRtcHighPassFilterEnabled = WebRtcHighPassFilterEnabled;
            config.WebRtcPreAmpEnabled = WebRtcPreAmpEnabled;
            config.WebRtcPreAmpGain = (float)Math.Clamp(WebRtcPreAmpGain, 0.5, 8.0);
            config.EnableMasAudioProcessing = EnableMasAudioProcessing;
            config.MasEchoCancellationEnabled = MasEchoCancellationEnabled;
            config.MasNoiseSuppressionEnabled = MasNoiseSuppressionEnabled;
            config.ShowReconnectMarkerInSubtitle = ShowReconnectMarker;
            config.RealtimeTranscriptionModelRef = SelectedRealtimeTranscriptionModel?.Reference;
            config.BatchTranscriptionModelRef = SelectedBatchTranscriptionModel?.Reference;
            config.TextToSpeechModelRef = SelectedTextToSpeechModel?.Reference;
        }

        public void SelectModels(AzureSpeechConfig config, List<ModelOption> speechToTextModels, List<ModelOption> textToSpeechModels)
        {
            SpeechToTextModels = speechToTextModels;
            TextToSpeechModels = textToSpeechModels;
            SelectModelOption(config.RealtimeTranscriptionModelRef, speechToTextModels, v => _selectedRealtimeTranscriptionModel = v, nameof(SelectedRealtimeTranscriptionModel));
            SelectModelOption(config.BatchTranscriptionModelRef, speechToTextModels, v => _selectedBatchTranscriptionModel = v, nameof(SelectedBatchTranscriptionModel));
            SelectModelOption(config.TextToSpeechModelRef, textToSpeechModels, v => _selectedTextToSpeechModel = v, nameof(SelectedTextToSpeechModel));
            OnPropertyChanged(nameof(HasSpeechToTextModels));
            OnPropertyChanged(nameof(HasTextToSpeechModels));
        }

        public void RefreshModels(List<ModelOption> speechToTextModels, List<ModelOption> textToSpeechModels)
        {
            var realtimeRef = SelectedRealtimeTranscriptionModel?.Reference;
            var batchRef = SelectedBatchTranscriptionModel?.Reference;
            var ttsRef = SelectedTextToSpeechModel?.Reference;

            SpeechToTextModels = speechToTextModels;
            TextToSpeechModels = textToSpeechModels;
            SelectModelOption(realtimeRef, speechToTextModels, v => _selectedRealtimeTranscriptionModel = v, nameof(SelectedRealtimeTranscriptionModel));
            SelectModelOption(batchRef, speechToTextModels, v => _selectedBatchTranscriptionModel = v, nameof(SelectedBatchTranscriptionModel));
            SelectModelOption(ttsRef, textToSpeechModels, v => _selectedTextToSpeechModel = v, nameof(SelectedTextToSpeechModel));
            OnPropertyChanged(nameof(HasSpeechToTextModels));
            OnPropertyChanged(nameof(HasTextToSpeechModels));
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options, Action<ModelOption?> setter, string propertyName)
        {
            var match = reference == null
                ? null
                : options.FirstOrDefault(o => o.Reference.EndpointId == reference.EndpointId && o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }

        private void RefreshWebRtcPluginStatus(bool manual)
        {
            if (!IsWebRtcPluginSelected)
            {
                WebRtcPluginStatusHealthy = false;
                WebRtcPluginStatus = "当前未启用 WebRTC APM；启用后可在这里直接做本机初始化检测。";
                return;
            }

            try
            {
                using var processor = new WebRtcApmPreProcessor(BuildPreviewConfig());
                WebRtcPluginStatusHealthy = processor.IsAvailable;
                WebRtcPluginStatus = processor.IsAvailable
                    ? (manual
                        ? "WebRTC APM 自检成功：当前应用进程已能初始化插件与原生模块。此检测不会启动真实录音。"
                        : "WebRTC APM 当前可用：按现有参数可在本机完成初始化。")
                    : (manual
                        ? $"WebRTC APM 自检失败：{processor.UnavailableReason ?? "未知原因"}"
                        : $"WebRTC APM 当前不可用：{processor.UnavailableReason ?? "未知原因"}");
            }
            catch (Exception ex)
            {
                WebRtcPluginStatusHealthy = false;
                WebRtcPluginStatus = manual
                    ? $"WebRTC APM 自检异常：{ex.Message}"
                    : $"WebRTC APM 当前不可用：{ex.Message}";
            }
        }

        private AzureSpeechConfig BuildPreviewConfig()
        {
            return new AzureSpeechConfig
            {
                AudioPreProcessorPlugin = (AudioPreProcessorPluginType)Math.Clamp(AudioPreProcessorPluginIndex, 0, 1),
                WebRtcAecEnabled = WebRtcAecEnabled,
                WebRtcAecMobileMode = WebRtcAecMobileMode,
                WebRtcAecLatencyMs = Math.Clamp(WebRtcAecLatencyMs, 0, 500),
                WebRtcNoiseSuppressionEnabled = WebRtcNoiseSuppressionEnabled,
                WebRtcNoiseSuppressionLevel = Math.Clamp(WebRtcNoiseSuppressionLevel, 0, 3),
                WebRtcAgc1Enabled = WebRtcAgc1Enabled,
                WebRtcAgc2Enabled = WebRtcAgc2Enabled,
                WebRtcAgcMode = Math.Clamp(WebRtcAgcMode, 0, 2),
                WebRtcAgcTargetLevelDbfs = Math.Clamp(WebRtcAgcTargetLevelDbfs, -31, 0),
                WebRtcAgcCompressionGainDb = Math.Clamp(WebRtcAgcCompressionGainDb, 0, 90),
                WebRtcAgcLimiterEnabled = WebRtcAgcLimiterEnabled,
                WebRtcHighPassFilterEnabled = WebRtcHighPassFilterEnabled,
                WebRtcPreAmpEnabled = WebRtcPreAmpEnabled,
                WebRtcPreAmpGain = (float)Math.Clamp(WebRtcPreAmpGain, 0.5, 8.0)
            };
        }
    }
}
