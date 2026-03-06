using System;
using System.Runtime.CompilerServices;

namespace TrueFluentPro.ViewModels.Settings
{
    /// <summary>
    /// 配置分区 ViewModel 基类，提供 Changed 事件和自动触发脏标记的属性 setter。
    /// </summary>
    public abstract class SettingsSectionBase : ViewModelBase, ISettingsSection
    {
        public event Action? Changed;

        protected void OnChanged() => Changed?.Invoke();

        /// <summary>设置属性值并触发 Changed 事件（若值有变化）</summary>
        protected bool Set<T>(ref T field, T value, bool dirty = true, Action? then = null,
            [CallerMemberName] string? name = null)
        {
            if (!SetProperty(ref field, value, name)) return false;
            then?.Invoke();
            if (dirty) OnChanged();
            return true;
        }

        public abstract void LoadFrom(Models.AzureSpeechConfig config);
        public abstract void ApplyTo(Models.AzureSpeechConfig config);
    }
}
