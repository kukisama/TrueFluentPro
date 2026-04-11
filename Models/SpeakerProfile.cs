using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    /// <summary>单个发言人的完整语音配置（voice + style + prosody + advanced）。</summary>
    public partial class SpeakerProfile : ObservableObject
    {
        public SpeakerProfile(string tag, string displayName)
        {
            Tag = tag;
            DisplayName = displayName;
        }

        /// <summary>内部标识，如 "A"、"B"、"C"。</summary>
        public string Tag { get; }

        [ObservableProperty] private string _displayName;
        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private VoiceInfo? _voice;

        public bool HasVoice => Voice != null;

        partial void OnVoiceChanged(VoiceInfo? value)
        {
            OnPropertyChanged(nameof(HasVoice));
            OnPropertyChanged(nameof(SupportsExpressAs));
            OnPropertyChanged(nameof(SupportsProsodyControl));

            if (value != null)
            {
                if (!value.SupportsProsodyControl) { Rate = null; Pitch = null; }
                AdvancedOptions.ClearUnsupportedValues(value);
            }
        }

        public bool SupportsExpressAs => Voice?.SupportsExpressAs ?? false;
        public bool SupportsProsodyControl => Voice?.SupportsProsodyControl ?? false;

        [ObservableProperty] private ObservableCollection<string> _styles = new();
        [ObservableProperty] private string? _selectedStyle;
        [ObservableProperty] private double _styleDegree = 1.0;
        [ObservableProperty] private ObservableCollection<string> _roles = new();
        [ObservableProperty] private string? _selectedRole;
        [ObservableProperty] private string? _rate;
        [ObservableProperty] private string? _pitch;

        public SpeechAdvancedOptions AdvancedOptions { get; } = new();
    }
}
