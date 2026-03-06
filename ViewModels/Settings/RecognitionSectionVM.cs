using System;
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
        }
    }
}
