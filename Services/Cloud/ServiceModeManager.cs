using System;
using System.Diagnostics;
using TrueFluentPro.Models.Cloud;

namespace TrueFluentPro.Services.Cloud
{
    /// <summary>
    /// 服务模式管理器。管理 SelfHosted / Cloud 模式切换。
    /// 默认为 SelfHosted 模式（保持现有行为不变）。
    /// </summary>
    public sealed class ServiceModeManager : IServiceModeManager
    {
        private CloudSettings _settings = new();

        public ServiceMode CurrentMode => _settings.Mode;
        public bool IsCloudMode => _settings.Mode == ServiceMode.Cloud;
        public string? BackendUrl => string.IsNullOrWhiteSpace(_settings.BackendUrl) ? null : _settings.BackendUrl;

        public event Action? ModeChanged;

        public void ApplySettings(CloudSettings settings)
        {
            _settings = settings ?? new CloudSettings();
            Debug.WriteLine($"[ServiceMode] Mode={_settings.Mode}, Backend={_settings.BackendUrl}");
            ModeChanged?.Invoke();
        }

        public CloudSettings GetCurrentSettings()
        {
            return new CloudSettings
            {
                Mode = _settings.Mode,
                BackendUrl = _settings.BackendUrl,
                AadTenantId = _settings.AadTenantId,
                AadClientId = _settings.AadClientId,
                AadScope = _settings.AadScope
            };
        }
    }
}
