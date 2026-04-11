using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    /// <summary>SSML 高级选项（effect/silence/emphasis/phoneme 等）。</summary>
    public partial class SpeechAdvancedOptions : ObservableObject
    {
        public void ClearUnsupportedValues(VoiceInfo? voice)
        {
            if (voice == null) return;

            if (!voice.SupportsLangTag) LanguageOverride = null;
            if (!voice.SupportsProsodyControl) { Volume = null; Range = null; Contour = null; }
            if (!voice.SupportsBreakTag) { BreakStrength = null; BreakTime = null; }
            if (!voice.SupportsSilenceTag) { SilenceType = null; SilenceValue = null; }
            if (!voice.SupportsEmphasisTag) EmphasisLevel = null;
            if (!voice.SupportsPhonemeTag) { PhonemeAlphabet = null; PhonemeValue = null; }
            if (!voice.SupportsSayAsTag) { SayAsInterpretAs = null; SayAsFormat = null; SayAsDetail = null; }
            if (!voice.SupportsSubAliasTag) SubAlias = null;
        }

        [ObservableProperty] private string? _effect;
        [ObservableProperty] private string? _languageOverride;
        [ObservableProperty] private string? _volume;
        [ObservableProperty] private string? _range;
        [ObservableProperty] private string? _contour;
        [ObservableProperty] private string? _breakStrength;
        [ObservableProperty] private string? _breakTime;
        [ObservableProperty] private string? _silenceType;
        [ObservableProperty] private string? _silenceValue;
        [ObservableProperty] private string? _emphasisLevel;
        [ObservableProperty] private string? _sayAsInterpretAs;
        [ObservableProperty] private string? _sayAsFormat;
        [ObservableProperty] private string? _sayAsDetail;
        [ObservableProperty] private string? _subAlias;
        [ObservableProperty] private string? _phonemeAlphabet;
        [ObservableProperty] private string? _phonemeValue;

        public SpeechAdvancedOptions Clone()
        {
            var copy = new SpeechAdvancedOptions();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(SpeechAdvancedOptions? other)
        {
            if (other == null) return;
            Effect = other.Effect;
            LanguageOverride = other.LanguageOverride;
            Volume = other.Volume;
            Range = other.Range;
            Contour = other.Contour;
            BreakStrength = other.BreakStrength;
            BreakTime = other.BreakTime;
            SilenceType = other.SilenceType;
            SilenceValue = other.SilenceValue;
            EmphasisLevel = other.EmphasisLevel;
            SayAsInterpretAs = other.SayAsInterpretAs;
            SayAsFormat = other.SayAsFormat;
            SayAsDetail = other.SayAsDetail;
            SubAlias = other.SubAlias;
            PhonemeAlphabet = other.PhonemeAlphabet;
            PhonemeValue = other.PhonemeValue;
        }
    }
}
