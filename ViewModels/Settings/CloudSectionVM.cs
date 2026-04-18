using System;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueFluentPro.Models.Cloud;
using TrueFluentPro.Services.Cloud;

namespace TrueFluentPro.ViewModels.Settings
{
    /// <summary>
    /// Cloud SaaS 模式配置分区 ViewModel。
    /// 管理服务模式切换、后端地址、AAD 登录参数。
    /// 完全解耦：不影响任何现有分区的行为。
    /// </summary>
    public class CloudSectionVM : SettingsSectionBase
    {
        private readonly ICloudAuthService _authService;
        private readonly IServiceModeManager _modeManager;
        private readonly ICloudApiClient _apiClient;
        private readonly RelayCommand _loginCommand;
        private readonly RelayCommand _logoutCommand;

        private int _serviceModeIndex;
        private string _backendUrl = "";
        private string _aadTenantId = "";
        private string _aadClientId = "";
        private string _aadScope = "";
        private bool _isLoggedIn;
        private string _loginStatus = "未登录";
        private string _userDisplayName = "";
        private string _healthStatus = "";
        private bool _isHealthy;

        public CloudSectionVM(
            ICloudAuthService authService,
            IServiceModeManager modeManager,
            ICloudApiClient apiClient)
        {
            _authService = authService;
            _modeManager = modeManager;
            _apiClient = apiClient;

            _loginCommand = new RelayCommand(async _ => await LoginAsync(), _ => !IsLoggedIn);
            _logoutCommand = new RelayCommand(async _ => await LogoutAsync(), _ => IsLoggedIn);
            CheckHealthCommand = new RelayCommand(async _ => await CheckHealthAsync());

            LoginCommand = _loginCommand;
            LogoutCommand = _logoutCommand;
        }

        // ═══ 绑定属性 ═══

        /// <summary>0=SelfHosted, 1=Cloud</summary>
        public int ServiceModeIndex
        {
            get => _serviceModeIndex;
            set => Set(ref _serviceModeIndex, value);
        }

        public string BackendUrl
        {
            get => _backendUrl;
            set => Set(ref _backendUrl, value);
        }

        public string AadTenantId
        {
            get => _aadTenantId;
            set => Set(ref _aadTenantId, value);
        }

        public string AadClientId
        {
            get => _aadClientId;
            set => Set(ref _aadClientId, value);
        }

        public string AadScope
        {
            get => _aadScope;
            set => Set(ref _aadScope, value);
        }

        public bool IsLoggedIn
        {
            get => _isLoggedIn;
            set
            {
                if (SetProperty(ref _isLoggedIn, value))
                {
                    _loginCommand.RaiseCanExecuteChanged();
                    _logoutCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string LoginStatus
        {
            get => _loginStatus;
            set => Set(ref _loginStatus, value, dirty: false);
        }

        public string UserDisplayName
        {
            get => _userDisplayName;
            set => Set(ref _userDisplayName, value, dirty: false);
        }

        public string HealthStatus
        {
            get => _healthStatus;
            set => Set(ref _healthStatus, value, dirty: false);
        }

        public bool IsHealthy
        {
            get => _isHealthy;
            set => Set(ref _isHealthy, value, dirty: false);
        }

        // ═══ 命令 ═══

        public ICommand LoginCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand CheckHealthCommand { get; }

        // ═══ 操作 ═══

        private async Task LoginAsync()
        {
            LoginStatus = "正在登录...";
            EnsureAuthConfigured();

            var success = await _authService.LoginAsync();
            if (success)
            {
                IsLoggedIn = true;
                UserDisplayName = _authService.DisplayName ?? "";
                LoginStatus = $"已登录: {UserDisplayName}";
            }
            else
            {
                LoginStatus = "登录失败或已取消";
            }
        }

        private async Task LogoutAsync()
        {
            await _authService.LogoutAsync();
            IsLoggedIn = false;
            UserDisplayName = "";
            LoginStatus = "已登出";
        }

        private async Task CheckHealthAsync()
        {
            HealthStatus = "正在检查...";
            var healthy = await _apiClient.CheckHealthAsync();
            IsHealthy = healthy;
            HealthStatus = healthy ? "✅ 后端可用" : "❌ 后端不可达";
        }

        private void EnsureAuthConfigured()
        {
            if (!string.IsNullOrWhiteSpace(_aadTenantId) && !string.IsNullOrWhiteSpace(_aadClientId))
            {
                _authService.Reconfigure(_aadTenantId, _aadClientId, _aadScope);
            }
        }

        // ═══ ISettingsSection ═══

        public override void LoadFrom(Models.AzureSpeechConfig config)
        {
            var cs = config.CloudSettings ?? new CloudSettings();
            _serviceModeIndex = cs.Mode == ServiceMode.Cloud ? 1 : 0;
            _backendUrl = cs.BackendUrl;
            _aadTenantId = cs.AadTenantId;
            _aadClientId = cs.AadClientId;
            _aadScope = cs.AadScope;

            OnPropertyChanged(nameof(ServiceModeIndex));
            OnPropertyChanged(nameof(BackendUrl));
            OnPropertyChanged(nameof(AadTenantId));
            OnPropertyChanged(nameof(AadClientId));
            OnPropertyChanged(nameof(AadScope));

            // 同步登录状态
            IsLoggedIn = _authService.IsLoggedIn;
            UserDisplayName = _authService.DisplayName ?? "";
            LoginStatus = IsLoggedIn ? $"已登录: {UserDisplayName}" : "未登录";
        }

        public override void ApplyTo(Models.AzureSpeechConfig config)
        {
            config.CloudSettings = new CloudSettings
            {
                Mode = _serviceModeIndex == 1 ? ServiceMode.Cloud : ServiceMode.SelfHosted,
                BackendUrl = _backendUrl,
                AadTenantId = _aadTenantId,
                AadClientId = _aadClientId,
                AadScope = _aadScope
            };
        }
    }
}
