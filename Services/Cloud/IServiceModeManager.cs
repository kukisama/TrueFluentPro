using System;
using TrueFluentPro.Models.Cloud;

namespace TrueFluentPro.Services.Cloud
{
    /// <summary>
    /// 管理当前应用的服务模式（SelfHosted / Cloud）。
    /// 所有需要感知模式的组件通过此接口判断当前走直连还是代理。
    /// </summary>
    public interface IServiceModeManager
    {
        /// <summary>当前服务模式</summary>
        ServiceMode CurrentMode { get; }

        /// <summary>是否处于 Cloud 模式</summary>
        bool IsCloudMode { get; }

        /// <summary>Cloud 模式的后端 API 地址</summary>
        string? BackendUrl { get; }

        /// <summary>Cloud 设置变更时触发</summary>
        event Action? ModeChanged;

        /// <summary>应用 Cloud 设置（从配置加载或用户更改）</summary>
        void ApplySettings(CloudSettings settings);

        /// <summary>获取当前 Cloud 设置的快照</summary>
        CloudSettings GetCurrentSettings();
    }
}
