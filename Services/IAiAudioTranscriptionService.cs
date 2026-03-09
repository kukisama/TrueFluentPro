using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public sealed class AiAudioTranscriptionResult
    {
        public required List<SubtitleCue> Cues { get; init; }
        public required string RawJson { get; init; }
        public string FinalUrl { get; init; } = "";
    }

    public interface IAiAudioTranscriptionService
    {
        Task<AiAudioTranscriptionResult> TranscribeAsync(
            ModelRuntimeResolution runtime,
            string audioPath,
            string? sourceLanguage,
            BatchSubtitleSplitOptions splitOptions,
            CancellationToken cancellationToken);
    }
}
