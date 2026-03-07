using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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
            OnPropertyChanged(nameof(IsInputRecognitionEnabled));
            OnPropertyChanged(nameof(IsOutputRecognitionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
        }

        public void RefreshAudioDevices(bool persistSelection)
        {
            var config = _configProvider();
            var currentInputId = _selectedAudioDevice?.DeviceId ?? config.SelectedAudioDeviceId;
            var currentOutputId = _selectedOutputDevice?.DeviceId ?? config.SelectedOutputDeviceId;

            _logger("DeviceRefreshStart",
                $"persist={persistSelection} inputId='{currentInputId}' outputId='{currentOutputId}' " +
                $"inputName='{_selectedAudioDevice?.DisplayName ?? ""}' outputName='{_selectedOutputDevice?.DisplayName ?? ""}'");

            _suppressDeviceSelectionPersistence = true;
            try
            {
                _audioDevices.Clear();
                _outputDevices.Clear();

                if (!OperatingSystem.IsWindows())
                {
                    IsAudioDeviceSelectionEnabled = false;
                    IsAudioDeviceRefreshEnabled = false;
                    IsOutputDeviceSelectionEnabled = false;
                    SetSelectedAudioDevice(null, persistSelection: false);
                    SetSelectedOutputDevice(null, persistSelection: false);
                    return;
                }

                var inputDevices = AudioDeviceEnumerator.GetActiveDevices(AudioDeviceType.Capture);
                _audioDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = "",
                    DisplayName = "无",
                    DeviceType = AudioDeviceType.Capture
                });
                foreach (var device in inputDevices)
                {
                    _audioDevices.Add(device);
                }

                var outputDevices = AudioDeviceEnumerator.GetActiveDevices(AudioDeviceType.Render);
                _outputDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = "",
                    DisplayName = "无",
                    DeviceType = AudioDeviceType.Render
                });
                foreach (var device in outputDevices)
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

            if (_suppressDeviceSelectionPersistence && device == null)
            {
                return;
            }

            if (device == null && _audioDevices.Count > 0)
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

            if (_suppressDeviceSelectionPersistence && device == null)
            {
                return;
            }

            if (device == null && _outputDevices.Count > 0)
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
