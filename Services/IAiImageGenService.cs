using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IAiImageGenService
    {
        void SetTokenProvider(AzureTokenProvider? provider);

        /// <summary>
        /// 上传图片文件到 /openai/v1/files（purpose=assistants），返回 file_id。
        /// 可用于 vision 输入或图片编辑的参考图上传。
        /// </summary>
        Task<string> UploadImageFileAsync(AiConfig config, string filePath, CancellationToken ct);

        Task<ImageGenerationResult> GenerateImagesAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null,
            Action<int>? onProgress = null);

        Task<ImageSaveResult> GenerateAndSaveImagesAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string outputDirectory,
            CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null,
            Action<int>? onProgress = null);

        IReadOnlyList<string> GetGenerateCandidateUrlsForRoute(AiConfig config, ImageApiRouteMode routeMode);

        IReadOnlyList<string> GetApimDeploymentGenerateCandidateUrls(AiConfig config);

        Task<ImageRouteProbeResult> ProbeGenerateRouteAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            ImageApiRouteMode routeMode,
            CancellationToken ct);

        Task<ImageRouteProbeResult> ProbeGenerateCandidateUrlsAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string routeLabel,
            IReadOnlyList<string> candidateUrls,
            CancellationToken ct);
    }
}
