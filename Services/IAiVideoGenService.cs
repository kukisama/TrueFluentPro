using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IAiVideoGenService
    {
        void SetTokenProvider(AzureTokenProvider? provider);

        Task<string> CreateVideoAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string? referenceImagePath,
            CancellationToken ct,
            IReadOnlyList<string>? createUrlCandidatesOverride = null,
            bool allowFallbacks = true,
            bool allowApimSubscriptionKeyQueryFallback = true);

        Task<(string status, int progress, string? failureReason)> PollStatusAsync(
            AiConfig config,
            string videoId,
            CancellationToken ct,
            VideoApiMode apiMode = VideoApiMode.SoraJobs);

        Task<(string status, int progress, string? generationId, string? failureReason)> PollStatusDetailsAsync(
            AiConfig config,
            string videoId,
            CancellationToken ct,
            VideoApiMode apiMode);

        Task<string?> DownloadVideoAsync(
            AiConfig config,
            string videoId,
            string localPath,
            CancellationToken ct,
            string? generationId = null,
            VideoApiMode apiMode = VideoApiMode.SoraJobs);

        Task<(string filePath, double generateSeconds, double downloadSeconds, string? downloadUrl)> GenerateVideoAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string outputPath,
            CancellationToken ct,
            string? referenceImagePath = null,
            Action<int>? onProgress = null,
            Action<string>? onVideoIdCreated = null,
            Action<string>? onStatusChanged = null,
            Action<string>? onGenerationIdResolved = null,
            Action<double>? onSucceeded = null);

        IReadOnlyList<string> BuildDownloadCandidateUrls(
            AiConfig config,
            string videoId,
            string? generationId,
            VideoApiMode apiMode = VideoApiMode.SoraJobs);
    }
}
