using System;

namespace TrueFluentPro.Models
{
    public class SubtitleCue
    {
        private const int MaxDisplayTextLength = 240;

        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; } = "";

        public string RangeText => $"{Start:hh\\:mm\\:ss\\.fff} - {End:hh\\:mm\\:ss\\.fff}";

        public string DisplayText => BuildDisplayText(Text);

        public string ToolTipText => string.IsNullOrWhiteSpace(Text) ? "(空字幕)" : Text;

        private static string BuildDisplayText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text
                .Replace("\r", " ")
                .Replace("\n", " ");
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();

            if (normalized.Length <= MaxDisplayTextLength)
            {
                return normalized;
            }

            return normalized[..MaxDisplayTextLength] + "…";
        }
    }
}
