using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// Azure Speech REST API 返回的语音信息 DTO。
    /// GET https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list
    /// </summary>
    public class VoiceInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("DisplayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("LocalName")]
        public string LocalName { get; set; } = string.Empty;

        [JsonPropertyName("ShortName")]
        public string ShortName { get; set; } = string.Empty;

        [JsonPropertyName("Gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("Locale")]
        public string Locale { get; set; } = string.Empty;

        [JsonPropertyName("LocaleName")]
        public string LocaleName { get; set; } = string.Empty;

        [JsonPropertyName("VoiceType")]
        public string VoiceType { get; set; } = string.Empty;

        [JsonPropertyName("Status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("SampleRateHertz")]
        public string SampleRateHertz { get; set; } = string.Empty;

        [JsonPropertyName("WordsPerMinute")]
        public string? WordsPerMinute { get; set; }

        [JsonPropertyName("StyleList")]
        public List<string>? StyleList { get; set; }

        [JsonPropertyName("RolePlayList")]
        public List<string>? RolePlayList { get; set; }

        [JsonPropertyName("SecondaryLocaleList")]
        public List<string>? SecondaryLocaleList { get; set; }

        [JsonPropertyName("ExtendedPropertyMap")]
        public Dictionary<string, string>? ExtendedPropertyMap { get; set; }

        // ── 能力检测 ────────────────────────────────

        public bool HasStyles => StyleList is { Count: > 0 };
        public int StyleCount => StyleList?.Count ?? 0;
        public bool HasRolePlay => RolePlayList is { Count: > 0 };
        public bool IsMultilingual => SecondaryLocaleList is { Count: > 0 };
        public bool IsHD => ShortName.Contains("HD", StringComparison.OrdinalIgnoreCase)
                         || DisplayName.Contains("HD", StringComparison.OrdinalIgnoreCase);
        public bool IsDragonHdVoice => ShortName.Contains(":DragonHD", StringComparison.OrdinalIgnoreCase);
        public bool IsDragonHdOmni => ShortName.Contains(":DragonHDOmni", StringComparison.OrdinalIgnoreCase);
        public bool IsDragonHdFlash => ShortName.Contains(":DragonHDFlash", StringComparison.OrdinalIgnoreCase);

        public bool SupportsHighQuality48K
        {
            get
            {
                if (TryGetExtendedPropertyBool("IsHighQuality48K", out var enabled))
                    return enabled;
                return TryParseSampleRate(SampleRateHertz, out var rate) && rate >= 48000;
            }
        }

        public bool SupportsLangTag => IsDragonHdVoice || IsMultilingual;
        public bool SupportsExpressAs => IsDragonHdOmni || HasStyles || HasRolePlay;
        public bool SupportsProsodyControl => !IsDragonHdVoice;
        public bool SupportsBreakTag => !IsDragonHdVoice;
        public bool SupportsSilenceTag => !IsDragonHdVoice;
        public bool SupportsEmphasisTag => !IsDragonHdVoice;
        public bool SupportsPhonemeTag => !IsDragonHdOmni;
        public bool SupportsSayAsTag => true;
        public bool SupportsSubAliasTag => true;

        public string PreferredDisplayName => !string.IsNullOrWhiteSpace(LocalName)
            ? LocalName
            : DisplayName;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private bool TryGetExtendedPropertyBool(string key, out bool value)
        {
            value = false;
            if (ExtendedPropertyMap == null || !ExtendedPropertyMap.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                return false;
            if (bool.TryParse(raw, out value)) return true;
            if (string.Equals(raw, "1", StringComparison.Ordinal)) { value = true; return true; }
            if (string.Equals(raw, "0", StringComparison.Ordinal)) { value = false; return true; }
            return false;
        }

        private static bool TryParseSampleRate(string? raw, out int sampleRate)
        {
            sampleRate = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return int.TryParse(new string(raw.Where(char.IsDigit).ToArray()), out sampleRate);
        }

        public override string ToString() => $"{PreferredDisplayName} ({ShortName})";
    }
}
