using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels.Settings
{
    public sealed class VoiceImeSectionVM : SettingsSectionBase
    {
        [Flags]
        private enum EditedField { None = 0, Resource = 1, Model = 2, Microphone = 4, Language = 8, Silence = 16, Prewarm = 32 }

        private readonly VoiceImeRegistrationService _registration;
        private AzureSpeechConfig? _config;
        private EditedField _edited;
        private bool _refreshing;
        private bool _enablePrewarm = true;
        private string _resourceId = "";
        private ModelReference? _modelRef;
        private string _microphoneId = "";
        private string _language = "auto";
        private double _endSilenceTimeoutSeconds = 0.8;
        private List<ResourceOption> _speechResources = new();
        private List<ModelOption> _speechModels = new();
        private List<AudioDeviceInfo> _availableMicrophones = new();
        private List<AudioDeviceInfo> _microphones = new();
        private List<LanguageOption> _languages = new();
        private ResourceOption? _selectedResource;
        private ModelOption? _selectedModel;
        private AudioDeviceInfo? _selectedMicrophone;
        private LanguageOption? _selectedLanguage;
        private bool _isRefreshingMicrophones;
        private bool _isRegistrationBusy;
        private string _microphoneStatus = "尚未枚举麦克风。";
        private string _registrationStatus = "尚未查询输入法安装状态。";
        private string _operationMessage = "";

        public VoiceImeSectionVM(VoiceImeRegistrationService registration)
        {
            _registration = registration;
            RefreshOptionsCommand = new RelayCommand(_ => _ = RefreshOptionsAsync());
            RefreshMicrophonesCommand = new RelayCommand(_ => _ = RefreshMicrophonesAsync(),
                _ => IsWindows && !IsRefreshingMicrophones);
            RegisterCommand = new RelayCommand(_ => _ = RunRegistrationAsync(true), _ => CanManageRegistration);
            UnregisterCommand = new RelayCommand(_ => _ = RunRegistrationAsync(false), _ => CanManageRegistration);
            RefreshStatusCommand = new RelayCommand(_ => _ = RefreshStatusAsync(), _ => CanManageRegistration);
        }

        public bool IsWindows => OperatingSystem.IsWindows();
        public string ImeVersionText => $"版本 v{VoiceImeRegistrationService.GetProductVersionString()}（跟随主产品版本）";
        public bool CanManageRegistration => IsWindows && !IsRegistrationBusy;
        public List<ResourceOption> SpeechResources => _speechResources;
        public List<ModelOption> SpeechModels => _speechModels;
        public List<AudioDeviceInfo> Microphones => _microphones;
        public List<LanguageOption> Languages => _languages;
        public ICommand RefreshOptionsCommand { get; }
        public ICommand RefreshMicrophonesCommand { get; }
        public ICommand RegisterCommand { get; }
        public ICommand UnregisterCommand { get; }
        public ICommand RefreshStatusCommand { get; }

        public ResourceOption? SelectedResource
        {
            get => _selectedResource;
            set
            {
                if (_refreshing || value == null || !SetProperty(ref _selectedResource, value)) return;
                _resourceId = value.Id;
                Edited(EditedField.Resource);
            }
        }

        public ModelOption? SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (_refreshing || value == null || !SetProperty(ref _selectedModel, value)) return;
                _modelRef = string.IsNullOrEmpty(value.Reference.EndpointId) && string.IsNullOrEmpty(value.Reference.ModelId)
                    ? null : CloneReference(value.Reference);
                Edited(EditedField.Model);
            }
        }

        public AudioDeviceInfo? SelectedMicrophone
        {
            get => _selectedMicrophone;
            set
            {
                if (_refreshing || value == null || !SetProperty(ref _selectedMicrophone, value)) return;
                _microphoneId = value.DeviceId;
                Edited(EditedField.Microphone);
            }
        }

        public LanguageOption? SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (_refreshing || value == null || !SetProperty(ref _selectedLanguage, value)) return;
                _language = value.Code;
                Edited(EditedField.Language);
            }
        }

        public bool EnablePrewarm
        {
            get => _enablePrewarm;
            set
            {
                if (_refreshing || !SetProperty(ref _enablePrewarm, value)) return;
                Edited(EditedField.Prewarm);
            }
        }

        public double EndSilenceTimeoutSeconds
        {
            get => _endSilenceTimeoutSeconds;
            set
            {
                if (_refreshing || !double.IsFinite(value) || value < 0.2 || value > 3
                    || !SetProperty(ref _endSilenceTimeoutSeconds, value)) return;
                Edited(EditedField.Silence);
            }
        }

        public bool IsRefreshingMicrophones
        {
            get => _isRefreshingMicrophones;
            private set
            {
                if (SetProperty(ref _isRefreshingMicrophones, value))
                    ((RelayCommand)RefreshMicrophonesCommand).RaiseCanExecuteChanged();
            }
        }

        public bool IsRegistrationBusy
        {
            get => _isRegistrationBusy;
            private set
            {
                if (!SetProperty(ref _isRegistrationBusy, value)) return;
                OnPropertyChanged(nameof(CanManageRegistration));
                ((RelayCommand)RegisterCommand).RaiseCanExecuteChanged();
                ((RelayCommand)UnregisterCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RefreshStatusCommand).RaiseCanExecuteChanged();
            }
        }

        public string MicrophoneStatus { get => _microphoneStatus; private set => SetProperty(ref _microphoneStatus, value); }
        public string RegistrationStatus { get => _registrationStatus; private set => SetProperty(ref _registrationStatus, value); }
        public string OperationMessage { get => _operationMessage; private set => SetProperty(ref _operationMessage, value); }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            // 同一配置刷新只重读未编辑字段；导入/切换配置时才清除旧编辑状态。
            if (!ReferenceEquals(_config, config)) _edited = EditedField.None;
            _config = config;
            var source = config.VoiceIme ?? new VoiceImeConfig();
            if (!_edited.HasFlag(EditedField.Prewarm)) _enablePrewarm = source.EnablePrewarm;
            if (!_edited.HasFlag(EditedField.Resource)) _resourceId = source.SpeechResourceId ?? "";
            if (!_edited.HasFlag(EditedField.Model)) _modelRef = CloneReference(source.ModelRef);
            if (!_edited.HasFlag(EditedField.Microphone)) _microphoneId = source.MicrophoneDeviceId ?? "";
            if (!_edited.HasFlag(EditedField.Language)) _language = source.RecognitionLanguage ?? "auto";
            if (!_edited.HasFlag(EditedField.Silence)) _endSilenceTimeoutSeconds = source.EndSilenceTimeoutSeconds;
            RefreshChoices(config);
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            if (_edited == EditedField.None) return;
            var target = config.VoiceIme ??= new VoiceImeConfig();
            if (_edited.HasFlag(EditedField.Prewarm)) target.EnablePrewarm = _enablePrewarm;
            if (_edited.HasFlag(EditedField.Resource)) target.SpeechResourceId = _resourceId;
            if (_edited.HasFlag(EditedField.Model)) target.ModelRef = CloneReference(_modelRef);
            if (_edited.HasFlag(EditedField.Microphone)) target.MicrophoneDeviceId = _microphoneId;
            if (_edited.HasFlag(EditedField.Language)) target.RecognitionLanguage = _language;
            if (_edited.HasFlag(EditedField.Silence)) target.EndSilenceTimeoutSeconds = _endSilenceTimeoutSeconds;
            _edited = EditedField.None;
        }

        private void RefreshChoices(AzureSpeechConfig config)
        {
            _refreshing = true;
            try
            {
                var activeName = config.GetActiveSpeechResource()?.GetDisplayName() ?? "尚未选择资源";
                _speechResources = new List<ResourceOption> { new("", $"跟随主程序活动资源（{activeName}）") };
                _speechResources.AddRange(config.GetEffectiveSpeechResources()
                    .Where(r => r.IsEnabled && r.HasCapability(SpeechCapability.RealtimeSpeechToText))
                    .Select(r => new ResourceOption(r.Id, r.GetDisplayName())));
                if (_speechResources.All(r => r.Id != _resourceId))
                    _speechResources.Add(new ResourceOption(_resourceId, "已保存的资源（当前不可用，保留引用）"));
                _selectedResource = _speechResources.First(r => r.Id == _resourceId);

                _speechModels = new List<ModelOption>
                {
                    new() { EndpointName = "沿用语音资源", ModelDisplayName = "不覆盖模型" }
                };
                _speechModels.AddRange(config.GetAvailableModels(ModelCapability.SpeechToText).Select(pair => new ModelOption
                {
                    Reference = new ModelReference { EndpointId = pair.Endpoint.Id, ModelId = pair.Model.ModelId },
                    EndpointName = pair.Endpoint.Name,
                    ModelDisplayName = string.IsNullOrWhiteSpace(pair.Model.DisplayName) ? pair.Model.ModelId : pair.Model.DisplayName,
                    EndpointType = pair.Endpoint.EndpointType
                }));
                if (_modelRef != null && _speechModels.All(m => !SameReference(m.Reference, _modelRef)))
                    _speechModels.Add(new ModelOption
                    {
                        Reference = CloneReference(_modelRef)!, EndpointName = "已保存的模型（当前不可用）",
                        ModelDisplayName = _modelRef.ModelId
                    });
                _selectedModel = _modelRef == null ? _speechModels[0]
                    : _speechModels.First(m => SameReference(m.Reference, _modelRef));

                _languages = AudioLabSectionVM.SourceLanguageOptions.ToList();
                if (_languages.All(l => l.Code != _language))
                    _languages.Add(new LanguageOption(_language, "已保存的语言"));
                _selectedLanguage = _languages.First(l => l.Code == _language);
                OnPropertyChanged(nameof(SpeechResources));
                OnPropertyChanged(nameof(SpeechModels));
                OnPropertyChanged(nameof(Languages));
                OnPropertyChanged(nameof(SelectedResource));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SelectedLanguage));
                OnPropertyChanged(nameof(EnablePrewarm));
                OnPropertyChanged(nameof(EndSilenceTimeoutSeconds));
                ApplyMicrophones(_availableMicrophones);
            }
            finally { _refreshing = false; }
        }

        /// <summary>进入分区及显式刷新时重建选项；绝不注册或自动更改资源。</summary>
        public async Task RefreshAsync()
        {
            await RefreshOptionsAsync();
            await RefreshStatusAsync();
        }

        private async Task RefreshOptionsAsync()
        {
            if (_config != null) LoadFrom(_config);
            await RefreshMicrophonesAsync();
        }

        private async Task RefreshMicrophonesAsync()
        {
            if (IsRefreshingMicrophones) return;
            if (!IsWindows) { MicrophoneStatus = "语音输入法仅适用于 Windows。"; return; }
            IsRefreshingMicrophones = true;
            MicrophoneStatus = "正在枚举麦克风…";
            try
            {
                var devices = await Task.Run(() => AudioDeviceEnumerator.GetActiveDevices(AudioDeviceType.Capture).ToList());
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _refreshing = true;
                    try
                    {
                        _availableMicrophones = devices;
                        ApplyMicrophones(_availableMicrophones);
                    }
                    finally { _refreshing = false; }
                    MicrophoneStatus = devices.Any(d => d.DeviceId == _microphoneId) || string.IsNullOrEmpty(_microphoneId)
                        ? $"已找到 {devices.Count} 个麦克风；空设备引用使用系统默认麦克风。"
                        : "所选麦克风当前离线，已保留设备引用；可重新选择或稍后刷新。";
                });
            }
            catch { MicrophoneStatus = "枚举麦克风失败，请检查设备连接及 Windows 麦克风权限后重试。"; }
            finally { IsRefreshingMicrophones = false; }
        }

        private void ApplyMicrophones(List<AudioDeviceInfo> devices)
        {
            _microphones = new List<AudioDeviceInfo> { new() { DisplayName = "系统默认麦克风" } };
            _microphones.AddRange(devices);
            if (_microphones.All(m => m.DeviceId != _microphoneId))
                _microphones.Add(new AudioDeviceInfo { DeviceId = _microphoneId, DisplayName = "已保存的麦克风（当前离线）" });
            _selectedMicrophone = _microphones.First(m => m.DeviceId == _microphoneId);
            OnPropertyChanged(nameof(Microphones));
            OnPropertyChanged(nameof(SelectedMicrophone));
        }

        private async Task RefreshStatusAsync()
        {
            if (IsRegistrationBusy) return;
            IsRegistrationBusy = true;
            try { RegistrationStatus = (await _registration.RefreshStatusAsync()).Message; }
            catch { RegistrationStatus = "安装状态查询失败，当前状态未知，请稍后刷新。"; }
            finally { IsRegistrationBusy = false; }
        }

        private async Task RunRegistrationAsync(bool register)
        {
            if (!CanManageRegistration) return;
            IsRegistrationBusy = true;
            OperationMessage = register ? "正在预检组件、安装/修复输入法；必要时更新或恢复旧版…"
                : "正在停止本用户输入法后台、移除系统入口并清理自有文件…";
            try
            {
                var result = register ? await _registration.RegisterAsync() : await _registration.UnregisterAsync();
                OperationMessage = result.Message;
                try { RegistrationStatus = (await _registration.RefreshStatusAsync()).Message; }
                catch { RegistrationStatus = "安装状态查询失败，当前状态未知，请稍后刷新；上方操作结果保留。"; }
            }
            catch { OperationMessage = "输入法组件操作失败，请刷新状态后重试；安装/反安装需手动以管理员运行主程序，不会自动提权。"; }
            finally { IsRegistrationBusy = false; }
        }

        private void Edited(EditedField field) { _edited |= field; OnChanged(); }
        private static bool SameReference(ModelReference a, ModelReference b)
            => a.EndpointId == b.EndpointId && a.ModelId == b.ModelId;
        private static ModelReference? CloneReference(ModelReference? value)
            => value == null ? null : new ModelReference { EndpointId = value.EndpointId, ModelId = value.ModelId };

        public sealed record ResourceOption(string Id, string DisplayName)
        {
            public override string ToString() => DisplayName;
        }
    }
}