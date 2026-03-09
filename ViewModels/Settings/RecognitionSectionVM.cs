using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;

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
        private bool _showReconnectMarker = true;
        private List<ModelOption> _speechToTextModels = new();
        private List<ModelOption> _textToSpeechModels = new();
        private ModelOption? _selectedRealtimeTranscriptionModel;
        private ModelOption? _selectedBatchTranscriptionModel;
        private ModelOption? _selectedTextToSpeechModel;

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
        public bool ShowReconnectMarker { get => _showReconnectMarker; set => Set(ref _showReconnectMarker, value); }
        public List<ModelOption> SpeechToTextModels { get => _speechToTextModels; set => SetProperty(ref _speechToTextModels, value); }
        public List<ModelOption> TextToSpeechModels { get => _textToSpeechModels; set => SetProperty(ref _textToSpeechModels, value); }
        public bool HasSpeechToTextModels => SpeechToTextModels.Count > 0;
        public bool HasTextToSpeechModels => TextToSpeechModels.Count > 0;
        public ModelOption? SelectedRealtimeTranscriptionModel { get => _selectedRealtimeTranscriptionModel; set => Set(ref _selectedRealtimeTranscriptionModel, value); }
        public ModelOption? SelectedBatchTranscriptionModel { get => _selectedBatchTranscriptionModel; set => Set(ref _selectedBatchTranscriptionModel, value); }
        public ModelOption? SelectedTextToSpeechModel { get => _selectedTextToSpeechModel; set => Set(ref _selectedTextToSpeechModel, value); }

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
            ShowReconnectMarker = config.ShowReconnectMarkerInSubtitle;
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
    }
}
