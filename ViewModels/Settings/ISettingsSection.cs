using System;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    /// <summary>
    /// 配置分区 ViewModel 接口。
    /// 每个分区实现此接口，宿主 SettingsViewModel 统一编排加载/保存。
    /// </summary>
    public interface ISettingsSection
    {
        /// <summary>分区变更通知（宿主订阅后触发自动保存）</summary>
        event Action? Changed;

        /// <summary>从配置模型加载分区状态</summary>
        void LoadFrom(AzureSpeechConfig config);

        /// <summary>将分区状态写回配置模型</summary>
        void ApplyTo(AzureSpeechConfig config);
    }
}
