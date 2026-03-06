using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class AboutSectionVM : SettingsSectionBase
    {
        private bool _isAutoUpdateEnabled = true;

        public bool IsAutoUpdateEnabled { get => _isAutoUpdateEnabled; set => Set(ref _isAutoUpdateEnabled, value); }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            IsAutoUpdateEnabled = config.IsAutoUpdateEnabled;
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.IsAutoUpdateEnabled = IsAutoUpdateEnabled;
        }
    }
}
