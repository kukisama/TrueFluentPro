using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels
{
    public class AudioDevicesViewModel : ViewModelBase
    {
        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly Action<string> _statusSetter;
        private readonly Func<bool> _isTranslatingProvider;
        private readonly Action<AzureSpeechConfig> _translationServiceUpdater;
        private readonly Func<bool> _tryApplyLiveAudioRouting;
        private readonly Action<string> _configSaver;
        private readonly Action<string, string> _logger;
        private readonly Func<Window?> _mainWindowProvider;

        /// <summary>音频管线配置变化时触发（录制来源等切换）</summary>
        public event Action? PipelineConfigChanged;

        private int _audioSourceModeIndex;
        private readonly ObservableCollection<AudioDeviceInfo> _audioDevices;
        private readonly ObservableCollection<AudioDeviceInfo> _outputDevices;
        private AudioDeviceInfo? _selectedAudioDevice;
        private AudioDeviceInfo? _selectedOutputDevice;
        private bool _isAudioDeviceSelectionEnabled;
        private bool _isAudioDeviceRefreshEnabled;
        private bool _isOutputDeviceSelectionEnabled;
        private bool _suppressDeviceSelectionPersistence;
        private double _audioLevel;
        private readonly ObservableCollection<double> _audioLevelHistory;
        private int _refreshVersion;

        private sealed class AudioDevicesSnapshot
        {
            public required List<AudioDeviceInfo> InputDevices { get; init; }
            public required List<AudioDeviceInfo> OutputDevices { get; init; }
            public required bool IsWindows { get; init; }
        }

        public ICommand RefreshAudioDevicesCommand { get; }

        public AudioDevicesViewModel(
            Func<AzureSpeechConfig> configProvider,
            Action<string> statusSetter,
            Func<bool> isTranslatingProvider,
            Action<AzureSpeechConfig> translationServiceUpdater,
            Func<bool> tryApplyLiveAudioRouting,
            Action<string> configSaver,
            Action<string, string> logger,
            Func<Window?> mainWindowProvider)
        {
            _configProvider = configProvider;
            _statusSetter = statusSetter;
            _isTranslatingProvider = isTranslatingProvider;
            _translationServiceUpdater = translationServiceUpdater;
            _tryApplyLiveAudioRouting = tryApplyLiveAudioRouting;
            _configSaver = configSaver;
            _logger = logger;
            _mainWindowProvider = mainWindowProvider;

            _audioDevices = new ObservableCollection<AudioDeviceInfo>();
            _outputDevices = new ObservableCollection<AudioDeviceInfo>();
            _audioLevelHistory = new ObservableCollection<double>(Enumerable.Repeat(0d, 24));

            RefreshAudioDevicesCommand = new RelayCommand(
                execute: _ => RefreshAudioDevices(persistSelection: true),
                canExecute: _ => true
            );
        }

        public int AudioSourceModeIndex
        {
            get => _audioSourceModeIndex;
            set
            {
                if (!SetProperty(ref _audioSourceModeIndex, value))
                {
                    return;
                }

                var config = _configProvider();

                if (!OperatingSystem.IsWindows())
                {
                    if (config.AudioSourceMode != AudioSourceMode.DefaultMic)
                    {
                        config.AudioSourceMode = AudioSourceMode.DefaultMic;
                        _audioSourceModeIndex = 0;
                        OnPropertyChanged(nameof(AudioSourceModeIndex));
                    }

                    RefreshAudioDevices(persistSelection: false);
                    return;
                }

                var mode = IndexToAudioSourceMode(value);
                if (config.AudioSourceMode != mode)
                {
                    config.AudioSourceMode = mode;
                    LogAudioModeSnapshot("音源模式已切换");

                    _translationServiceUpdater(config);

                    _configSaver("AudioSourceModeChanged");
                }

                RefreshAudioDevices(persistSelection: true);
            }
        }

        public ObservableCollection<AudioDeviceInfo> AudioDevicesList => _audioDevices;

        public ObservableCollection<AudioDeviceInfo> OutputDevicesList => _outputDevices;

        public ObservableCollection<double> AudioLevelHistory => _audioLevelHistory;

        public double AudioLevel
        {
            get => _audioLevel;
            private set => SetProperty(ref _audioLevel, value);
        }

        public AudioDeviceInfo? SelectedAudioDevice
        {
            get => _selectedAudioDevice;
            set => SetSelectedAudioDevice(value, persistSelection: true);
        }

        public AudioDeviceInfo? SelectedOutputDevice
        {
            get => _selectedOutputDevice;
            set => SetSelectedOutputDevice(value, persistSelection: true);
        }

        public bool IsAudioSourceSelectionEnabled => OperatingSystem.IsWindows();

        public bool IsAudioDeviceSelectionEnabled
        {
            get => _isAudioDeviceSelectionEnabled;
            set => SetProperty(ref _isAudioDeviceSelectionEnabled, value);
        }

        public bool IsOutputDeviceSelectionEnabled
        {
            get => _isOutputDeviceSelectionEnabled;
            set => SetProperty(ref _isOutputDeviceSelectionEnabled, value);
        }

        public bool IsInputRecognitionEnabled
        {
            get => _configProvider().UseInputForRecognition;
            set => SetInputRecognitionEnabled(value);
        }

        public bool IsOutputRecognitionEnabled
        {
            get => _configProvider().UseOutputForRecognition;
            set => SetOutputRecognitionEnabled(value);
        }

        public bool IsInputDeviceUiEnabled => IsAudioDeviceSelectionEnabled;

        public bool IsOutputDeviceUiEnabled => IsOutputDeviceSelectionEnabled;

        public bool IsAudioDeviceRefreshEnabled
        {
            get => _isAudioDeviceRefreshEnabled;
            set => SetProperty(ref _isAudioDeviceRefreshEnabled, value);
        }

        public bool IsRecordingLoopbackOnly
        {
            get => _configProvider().RecordingMode == RecordingMode.LoopbackOnly;
            set
            {
                if (value)
                {
                    SetRecordingMode(RecordingMode.LoopbackOnly);
                }
            }
        }

        public bool IsRecordingLoopbackMix
        {
            get => _configProvider().RecordingMode == RecordingMode.LoopbackWithMic;
            set
            {
                if (value)
                {
                    SetRecordingMode(RecordingMode.LoopbackWithMic);
                }
            }
        }

        public bool IsRecordingMicOnly
        {
            get => _configProvider().RecordingMode == RecordingMode.MicOnly;
            set
            {
                if (value)
                {
                    SetRecordingMode(RecordingMode.MicOnly);
                }
            }
        }

        // ── 识别音源 三态（与录制独立） ──
        public bool IsRecognitionLoopbackOnly
        {
            get
            {
                var c = _configProvider();
                return c.AudioSourceMode == AudioSourceMode.Loopback
                    || (c.AudioSourceMode == AudioSourceMode.CaptureDevice && c.UseOutputForRecognition && !c.UseInputForRecognition);
            }
            set { if (value) SetRecognitionMode(loopback: true, mic: false); }
        }

        public bool IsRecognitionLoopbackMix
        {
            get
            {
                var c = _configProvider();
                return c.AudioSourceMode == AudioSourceMode.CaptureDevice
                    && c.UseInputForRecognition && c.UseOutputForRecognition;
            }
            set { if (value) SetRecognitionMode(loopback: true, mic: true); }
        }

        public bool IsRecognitionMicOnly
        {
            get
            {
                var c = _configProvider();
                return c.AudioSourceMode == AudioSourceMode.DefaultMic
                    || (c.AudioSourceMode == AudioSourceMode.CaptureDevice && c.UseInputForRecognition && !c.UseOutputForRecognition);
            }
            set { if (value) SetRecognitionMode(loopback: false, mic: true); }
        }

        // ── 滑动指示器：每段固定宽度，指示器通过 Margin 左偏移实现位置过渡 ──
        public const double SegmentWidth = 68;
        public int RecognitionSelectedIndex =>
            IsRecognitionLoopbackOnly ? 0 : (IsRecognitionLoopbackMix ? 1 : (IsRecognitionMicOnly ? 2 : 1));
        public int RecordingSelectedIndex =>
            IsRecordingLoopbackOnly ? 0 : (IsRecordingLoopbackMix ? 1 : (IsRecordingMicOnly ? 2 : 1));
        public Thickness RecognitionIndicatorMargin => new(SegmentWidth * RecognitionSelectedIndex, 0, 0, 0);
        public Thickness RecordingIndicatorMargin => new(SegmentWidth * RecordingSelectedIndex, 0, 0, 0);

        public void SetAudioLevel(double level)
        {
            AudioLevel = level;
            PushAudioLevelSample(level);
        }

        public void ResetAudioLevelHistory()
        {
            _audioLevelHistory.Clear();
            for (var i = 0; i < 24; i++)
            {
                _audioLevelHistory.Add(0);
            }
        }

        public void NotifyTranslatingChanged()
        {
            OnPropertyChanged(nameof(IsAudioSourceSelectionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
        }

        public void UpdateConfig()
        {
            var config = _configProvider();
            _audioSourceModeIndex = AudioSourceModeToIndex(config.AudioSourceMode);
            NormalizeRecognitionToggles();
            RefreshAudioDevices(persistSelection: false);

            OnPropertyChanged(nameof(AudioSourceModeIndex));
            OnPropertyChanged(nameof(AudioDevicesList));
            OnPropertyChanged(nameof(SelectedAudioDevice));
            OnPropertyChanged(nameof(IsAudioSourceSelectionEnabled));
            OnPropertyChanged(nameof(IsAudioDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsAudioDeviceRefreshEnabled));
            OnPropertyChanged(nameof(OutputDevicesList));
            OnPropertyChanged(nameof(SelectedOutputDevice));
            OnPropertyChanged(nameof(IsOutputDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsRecordingLoopbackOnly));
            OnPropertyChanged(nameof(IsRecordingLoopbackMix));
            OnPropertyChanged(nameof(IsRecordingMicOnly));
            OnPropertyChanged(nameof(RecordingSelectedIndex));
            OnPropertyChanged(nameof(RecordingIndicatorMargin));
            OnPropertyChanged(nameof(IsRecognitionLoopbackOnly));
            OnPropertyChanged(nameof(IsRecognitionLoopbackMix));
            OnPropertyChanged(nameof(IsRecognitionMicOnly));
            OnPropertyChanged(nameof(RecognitionSelectedIndex));
            OnPropertyChanged(nameof(RecognitionIndicatorMargin));
            OnPropertyChanged(nameof(IsInputRecognitionEnabled));
            OnPropertyChanged(nameof(IsOutputRecognitionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
        }

        public void RefreshAudioDevices(bool persistSelection)
        {
            _ = RefreshAudioDevicesAsync(persistSelection);
        }

        public async Task RefreshAudioDevicesAsync(bool persistSelection, CancellationToken cancellationToken = default)
        {
            var config = _configProvider();
            var currentInputId = _selectedAudioDevice?.DeviceId ?? config.SelectedAudioDeviceId;
            var currentOutputId = _selectedOutputDevice?.DeviceId ?? config.SelectedOutputDeviceId;
            var refreshVersion = Interlocked.Increment(ref _refreshVersion);

            AudioDevicesSnapshot snapshot;
            try
            {
                snapshot = await Task.Run(() => BuildAudioDevicesSnapshot(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (refreshVersion != _refreshVersion)
                {
                    return;
                }

                ApplyAudioDevicesSnapshot(snapshot, currentInputId, currentOutputId, persistSelection);
            });
        }

        private AudioDevicesSnapshot BuildAudioDevicesSnapshot(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OperatingSystem.IsWindows())
            {
                return new AudioDevicesSnapshot
                {
                    IsWindows = false,
                    InputDevices = new List<AudioDeviceInfo>(),
                    OutputDevices = new List<AudioDeviceInfo>()
                };
            }

            var inputDevices = AudioDeviceEnumerator.GetActiveDevices(AudioDeviceType.Capture).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            var outputDevices = AudioDeviceEnumerator.GetActiveDevices(AudioDeviceType.Render).ToList();

            return new AudioDevicesSnapshot
            {
                IsWindows = true,
                InputDevices = inputDevices,
                OutputDevices = outputDevices
            };
        }

        private void ApplyAudioDevicesSnapshot(AudioDevicesSnapshot snapshot, string? currentInputId, string? currentOutputId, bool persistSelection)
        {
            var config = _configProvider();

            _logger("DeviceRefreshStart",
                $"persist={persistSelection} inputId='{currentInputId}' outputId='{currentOutputId}' " +
                $"isWindows={snapshot.IsWindows} inputs={snapshot.InputDevices.Count} outputs={snapshot.OutputDevices.Count}");

            _suppressDeviceSelectionPersistence = true;
            try
            {
                _audioDevices.Clear();
                _outputDevices.Clear();

                if (!snapshot.IsWindows)
                {
                    IsAudioDeviceSelectionEnabled = false;
                    IsAudioDeviceRefreshEnabled = false;
                    IsOutputDeviceSelectionEnabled = false;
                    SetSelectedAudioDevice(null, persistSelection: false);
                    SetSelectedOutputDevice(null, persistSelection: false);
                    return;
                }

                _audioDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = "",
                    DisplayName = "无",
                    DeviceType = AudioDeviceType.Capture
                });
                foreach (var device in snapshot.InputDevices)
                {
                    _audioDevices.Add(device);
                }

                _outputDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = "",
                    DisplayName = "无",
                    DeviceType = AudioDeviceType.Render
                });
                foreach (var device in snapshot.OutputDevices)
                {
                    _outputDevices.Add(device);
                }

                var inputPreview = string.Join(" | ", _audioDevices.Take(5)
                    .Select(d => $"{d.DisplayName} ({d.DeviceId})"));
                var outputPreview = string.Join(" | ", _outputDevices.Take(5)
                    .Select(d => $"{d.DisplayName} ({d.DeviceId})"));
                _logger("DeviceRefreshList",
                    $"inputs={_audioDevices.Count} outputs={_outputDevices.Count} " +
                    $"inputPreview='{inputPreview}' outputPreview='{outputPreview}'");

                IsAudioDeviceRefreshEnabled = true;
                IsAudioDeviceSelectionEnabled = _audioDevices.Count > 0;
                IsOutputDeviceSelectionEnabled = _outputDevices.Count > 0;
                OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
                OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));

                var targetInputId = currentInputId;
                if (!string.IsNullOrWhiteSpace(targetInputId)
                    && _audioDevices.All(d => d.DeviceId != targetInputId))
                {
                    targetInputId = AudioDeviceEnumerator.GetDefaultDeviceId(AudioDeviceType.Capture) ?? "";
                    if (string.IsNullOrWhiteSpace(targetInputId))
                    {
                        targetInputId = _audioDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.DeviceId))?.DeviceId ?? "";
                    }
                }

                var inputSelection = _audioDevices.FirstOrDefault(d => d.DeviceId == targetInputId)
                    ?? _audioDevices.FirstOrDefault();
                SetSelectedAudioDevice(inputSelection, persistSelection: false);

                var targetOutputId = currentOutputId;
                if (!string.IsNullOrWhiteSpace(targetOutputId)
                    && _outputDevices.All(d => d.DeviceId != targetOutputId))
                {
                    targetOutputId = AudioDeviceEnumerator.GetDefaultDeviceId(AudioDeviceType.Render) ?? "";
                    if (string.IsNullOrWhiteSpace(targetOutputId))
                    {
                        targetOutputId = _outputDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.DeviceId))?.DeviceId ?? "";
                    }
                }

                var outputSelection = _outputDevices.FirstOrDefault(d => d.DeviceId == targetOutputId)
                    ?? _outputDevices.FirstOrDefault();
                SetSelectedOutputDevice(outputSelection, persistSelection: false);

                if (string.IsNullOrWhiteSpace(config.SelectedAudioDeviceId)
                    && _selectedAudioDevice != null)
                {
                    config.SelectedAudioDeviceId = _selectedAudioDevice.DeviceId;
                    _configSaver("InputDevicePersistDefault");
                    _logger("DevicePersist",
                        $"inputId='{config.SelectedAudioDeviceId}' inputName='{_selectedAudioDevice.DisplayName}'");
                }

                if (string.IsNullOrWhiteSpace(config.SelectedOutputDeviceId)
                    && _selectedOutputDevice != null)
                {
                    config.SelectedOutputDeviceId = _selectedOutputDevice.DeviceId;
                    _configSaver("OutputDevicePersistDefault");
                    _logger("DevicePersist",
                        $"outputId='{config.SelectedOutputDeviceId}' outputName='{_selectedOutputDevice.DisplayName}'");
                }

                _logger("DeviceRefreshSelect",
                    $"inputSelected='{_selectedAudioDevice?.DisplayName ?? ""}' ({_selectedAudioDevice?.DeviceId ?? ""}) " +
                    $"outputSelected='{_selectedOutputDevice?.DisplayName ?? ""}' ({_selectedOutputDevice?.DeviceId ?? ""})");

                ForceUpdateDeviceComboBoxSelection();
            }
            finally
            {
                _suppressDeviceSelectionPersistence = false;
            }
        }

        private void SetSelectedAudioDevice(AudioDeviceInfo? device, bool persistSelection)
        {
            _logger("InputSelectChange",
                $"device='{device?.DisplayName ?? ""}' id='{device?.DeviceId ?? ""}' persist={persistSelection} suppress={_suppressDeviceSelectionPersistence}");

            var nextEnabled = device != null && !string.IsNullOrWhiteSpace(device.DeviceId);

            if (_suppressDeviceSelectionPersistence && device == null && persistSelection)
            {
                return;
            }

            if (device == null && _audioDevices.Count > 0 && persistSelection)
            {
                return;
            }

            if (!SetProperty(ref _selectedAudioDevice, device))
            {
                return;
            }

            if (!persistSelection || _suppressDeviceSelectionPersistence)
            {
                return;
            }

            var config = _configProvider();
            var deviceId = nextEnabled ? (device?.DeviceId ?? "") : "";
            var recognitionChanged = config.UseInputForRecognition != nextEnabled;
            var deviceChanged = config.SelectedAudioDeviceId != deviceId;

            if (recognitionChanged || deviceChanged)
            {
                config.UseInputForRecognition = nextEnabled;
                config.SelectedAudioDeviceId = deviceId;
                var applied = UpdateRecognitionModeFromToggles();

                _configSaver("InputDeviceSelected");
                var deviceName = device?.DisplayName ?? "无";
                _statusSetter(BuildDeviceChangeStatusMessage("麦克风输入", deviceName, applied));
            }
        }

        private void SetSelectedOutputDevice(AudioDeviceInfo? device, bool persistSelection)
        {
            _logger("OutputSelectChange",
                $"device='{device?.DisplayName ?? ""}' id='{device?.DeviceId ?? ""}' persist={persistSelection} suppress={_suppressDeviceSelectionPersistence}");

            var nextEnabled = device != null && !string.IsNullOrWhiteSpace(device.DeviceId);

            if (_suppressDeviceSelectionPersistence && device == null && persistSelection)
            {
                return;
            }

            if (device == null && _outputDevices.Count > 0 && persistSelection)
            {
                return;
            }

            if (!SetProperty(ref _selectedOutputDevice, device))
            {
                return;
            }

            if (!persistSelection || _suppressDeviceSelectionPersistence)
            {
                return;
            }

            var config = _configProvider();
            var deviceId = nextEnabled ? (device?.DeviceId ?? "") : "";
            var recognitionChanged = config.UseOutputForRecognition != nextEnabled;
            var deviceChanged = config.SelectedOutputDeviceId != deviceId;

            if (recognitionChanged || deviceChanged)
            {
                config.UseOutputForRecognition = nextEnabled;
                config.SelectedOutputDeviceId = deviceId;
                var applied = UpdateRecognitionModeFromToggles();

                _configSaver("OutputDeviceSelected");
                var deviceName = device?.DisplayName ?? "无";
                _statusSetter(BuildDeviceChangeStatusMessage("系统回环", deviceName, applied));
            }
        }

        private void SetRecordingMode(RecordingMode mode)
        {
            var config = _configProvider();
            if (config.RecordingMode == mode)
            {
                return;
            }

            config.RecordingMode = mode;
            LogAudioModeSnapshot("录制模式已切换");

            if (!_isTranslatingProvider())
            {
                _translationServiceUpdater(config);
                _statusSetter($"录制来源已切换为“{FormatRecordingMode(mode)}”。");
            }
            else
            {
                var applied = _tryApplyLiveAudioRouting();
                _statusSetter(applied
                    ? $"录制来源已切换为“{FormatRecordingMode(mode)}”，已立即生效。"
                    : $"录制来源已切换为“{FormatRecordingMode(mode)}”，将于下次开始翻译时生效。");
            }

            _configSaver("RecordingModeChanged");
            OnPropertyChanged(nameof(IsRecordingLoopbackOnly));
            OnPropertyChanged(nameof(IsRecordingLoopbackMix));
            OnPropertyChanged(nameof(IsRecordingMicOnly));
            OnPropertyChanged(nameof(RecordingSelectedIndex));
            OnPropertyChanged(nameof(RecordingIndicatorMargin));
            PipelineConfigChanged?.Invoke();
        }

        private void SetRecognitionMode(bool loopback, bool mic)
        {
            var config = _configProvider();
            // 计算目标 AudioSourceMode + 两个 bool
            AudioSourceMode targetMode;
            if (loopback && mic) targetMode = AudioSourceMode.CaptureDevice;
            else if (loopback) targetMode = AudioSourceMode.Loopback;
            else targetMode = AudioSourceMode.DefaultMic;

            if (config.AudioSourceMode == targetMode
                && config.UseInputForRecognition == mic
                && config.UseOutputForRecognition == loopback)
            {
                return;
            }

            config.AudioSourceMode = targetMode;
            config.UseInputForRecognition = mic;
            config.UseOutputForRecognition = loopback;
            _audioSourceModeIndex = AudioSourceModeToIndex(targetMode);

            var label = loopback && mic ? "环回+麦克风" : loopback ? "仅环回" : "仅麦克风";

            if (_isTranslatingProvider())
            {
                var applied = _tryApplyLiveAudioRouting();
                _statusSetter(applied
                    ? $"识别音源已切换为“{label}”，已立即生效。"
                    : $"识别音源已切换为“{label}”，将于下次开始翻译时生效。");
            }
            else
            {
                _translationServiceUpdater(config);
                _statusSetter($"识别音源已切换为“{label}”。");
            }

            LogAudioModeSnapshot("识别模式已切换");
            _configSaver("RecognitionModeChanged");
            OnPropertyChanged(nameof(AudioSourceModeIndex));
            OnPropertyChanged(nameof(IsRecognitionLoopbackOnly));
            OnPropertyChanged(nameof(IsRecognitionLoopbackMix));
            OnPropertyChanged(nameof(IsRecognitionMicOnly));
            OnPropertyChanged(nameof(RecognitionSelectedIndex));
            OnPropertyChanged(nameof(RecognitionIndicatorMargin));
            OnPropertyChanged(nameof(IsInputRecognitionEnabled));
            OnPropertyChanged(nameof(IsOutputRecognitionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
            PipelineConfigChanged?.Invoke();
        }

        private void SetInputRecognitionEnabled(bool enabled)
        {
            var config = _configProvider();
            if (config.UseInputForRecognition == enabled)
            {
                return;
            }

            config.UseInputForRecognition = enabled;
            UpdateRecognitionModeFromToggles();
            LogAudioModeSnapshot("输入识别开关");
            _configSaver("InputRecognitionToggle");
            OnPropertyChanged(nameof(IsInputRecognitionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
        }

        private void SetOutputRecognitionEnabled(bool enabled)
        {
            var config = _configProvider();
            if (config.UseOutputForRecognition == enabled)
            {
                return;
            }

            config.UseOutputForRecognition = enabled;
            UpdateRecognitionModeFromToggles();
            LogAudioModeSnapshot("输出识别开关");
            _configSaver("OutputRecognitionToggle");
            OnPropertyChanged(nameof(IsOutputRecognitionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
        }

        private bool UpdateRecognitionModeFromToggles()
        {
            var config = _configProvider();
            if (config.UseOutputForRecognition && !config.UseInputForRecognition)
            {
                config.AudioSourceMode = AudioSourceMode.Loopback;
            }
            else
            {
                config.AudioSourceMode = AudioSourceMode.CaptureDevice;
            }

            _audioSourceModeIndex = AudioSourceModeToIndex(config.AudioSourceMode);

            var applied = false;

            if (_isTranslatingProvider())
            {
                applied = _tryApplyLiveAudioRouting();
            }
            else
            {
                _translationServiceUpdater(config);
            }

            LogAudioModeSnapshot("识别路由已更新");

            OnPropertyChanged(nameof(AudioSourceModeIndex));
            return applied;
        }

        public void NormalizeRecognitionToggles()
        {
            UpdateRecognitionModeFromToggles();
        }

        private void LogAudioModeSnapshot(string eventName)
        {
            var config = _configProvider();
            _logger(eventName,
                $"音源模式={FormatAudioSourceMode(config.AudioSourceMode)} 输入识别={( config.UseInputForRecognition ? "开" : "关")} 输出识别={( config.UseOutputForRecognition ? "开" : "关")} " +
                $"录制模式={FormatRecordingMode(config.RecordingMode)} 翻译中={_isTranslatingProvider()} " +
                $"输入设备ID='{config.SelectedAudioDeviceId}' 输出设备ID='{config.SelectedOutputDeviceId}'");
        }

        private static string FormatAudioSourceMode(AudioSourceMode mode)
        {
            return mode switch
            {
                AudioSourceMode.DefaultMic => "默认麦克风",
                AudioSourceMode.CaptureDevice => "选择输入设备",
                AudioSourceMode.Loopback => "系统回环",
                _ => mode.ToString()
            };
        }

        private static string FormatRecordingMode(RecordingMode mode)
        {
            return mode switch
            {
                RecordingMode.LoopbackOnly => "仅环回",
                RecordingMode.LoopbackWithMic => "环回+麦克风",
                RecordingMode.MicOnly => "仅麦克风",
                _ => mode.ToString()
            };
        }

        private string BuildDeviceChangeStatusMessage(string label, string deviceName, bool applied)
        {
            var target = string.Equals(deviceName, "无", StringComparison.Ordinal)
                ? $"{label}已关闭"
                : $"{label}已切换到“{deviceName}”";

            if (!_isTranslatingProvider())
            {
                return $"{target}。";
            }

            return applied
                ? $"{target}，已立即生效。"
                : $"{target}，将于下次开始翻译时生效。";
        }

        private static int AudioSourceModeToIndex(AudioSourceMode mode)
        {
            return mode switch
            {
                AudioSourceMode.DefaultMic => 0,
                AudioSourceMode.CaptureDevice => 1,
                AudioSourceMode.Loopback => 2,
                _ => 0
            };
        }

        private static AudioSourceMode IndexToAudioSourceMode(int index)
        {
            return index switch
            {
                1 => AudioSourceMode.CaptureDevice,
                2 => AudioSourceMode.Loopback,
                _ => AudioSourceMode.DefaultMic
            };
        }

        private void PushAudioLevelSample(double level)
        {
            if (_audioLevelHistory.Count == 0)
            {
                return;
            }

            _audioLevelHistory.RemoveAt(0);
            _audioLevelHistory.Add(level);
        }

        private void ForceUpdateDeviceComboBoxSelection()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _suppressDeviceSelectionPersistence = true;
                try
                {
                    // Force binding refresh by bouncing the values
                    var savedInput = _selectedAudioDevice;
                    var savedOutput = _selectedOutputDevice;

                    _selectedAudioDevice = null;
                    OnPropertyChanged(nameof(SelectedAudioDevice));
                    _selectedAudioDevice = savedInput;
                    OnPropertyChanged(nameof(SelectedAudioDevice));

                    _selectedOutputDevice = null;
                    OnPropertyChanged(nameof(SelectedOutputDevice));
                    _selectedOutputDevice = savedOutput;
                    OnPropertyChanged(nameof(SelectedOutputDevice));
                }
                finally
                {
                    _suppressDeviceSelectionPersistence = false;
                }
            });
        }
    }
}
