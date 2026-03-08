namespace TrueFluentPro.Models
{
    public sealed class AudioFileProcessingSnapshot
    {
        public string AudioPath { get; init; } = "";
        public ProcessingDisplayState State { get; init; }
        public string BadgeText { get; init; } = "";
        public string DetailText { get; init; } = "";
    }
}
