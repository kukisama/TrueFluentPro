using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 图片/视频生成服务的共享基类（HttpClient、认证、URL 构建）
    /// </summary>
    public abstract class AiMediaServiceBase
    {
        protected static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        protected AzureTokenProvider? TokenProvider { get; set; }

        public void SetTokenProvider(AzureTokenProvider? provider)
        {
            TokenProvider = provider;
        }

        /// <summary>
        /// 媒体链路中的 AOAI 判定只遵循终结点类型。
        /// </summary>
        protected static bool IsAzureEndpoint(AiConfig config)
            => config.EndpointType == EndpointApiType.AzureOpenAi;

        /// <summary>
        /// 判断是否为 APIM 网关前门。
        /// 这类终结点常暴露 OpenAI Compatible 路径（如 /openai/v1/...），
        /// 但在 API Key 模式下要求客户端携带 APIM 订阅键头，而不是 Bearer。
        /// </summary>
        protected static bool IsApimGateway(AiConfig config)
            => config.EndpointType == EndpointApiType.ApiManagementGateway;

        /// <summary>
        /// 构建 API 基础 URL（去掉末尾的 /v1 等）
        /// </summary>
        protected static string BuildBaseUrl(AiConfig config)
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');
            if (baseUrl.EndsWith("/v1"))
                baseUrl = baseUrl[..^3];
            return baseUrl;
        }

        /// <summary>
        /// 设置认证头。
        /// 媒体链路中的认证只分两类：AAD(Bearer Token) / api-key。
        /// - AOAI (*.openai.azure.com) 使用 api-key 或 AAD Bearer
        /// - APIM 使用 api-key
        /// - 其他 OpenAI Compatible 使用 Bearer api-key
        /// - 若终结点显式配置了 API Key 发送方式，则优先使用显式值
        /// </summary>
        protected async Task SetAuthHeadersAsync(HttpRequestMessage request, AiConfig config, CancellationToken ct = default)
        {
            if (config.AzureAuthMode == AzureAuthMode.AAD)
            {
                if (TokenProvider?.IsLoggedIn != true)
                    throw new InvalidOperationException(
                        "Azure AAD 认证未登录。请先在设置中完成 AAD 登录，或切换为 API Key 认证模式。");

                var token = await TokenProvider.GetTokenAsync(ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else
            {
                ApplyApiKeyHeader(request, config);
            }
        }

        /// <summary>
        /// 设置认证头（同步版本，仅用于 api-key 场景）。
        /// </summary>
        protected static void SetAuthHeaders(HttpRequestMessage request, AiConfig config)
        {
            if (config.AzureAuthMode == AzureAuthMode.AAD)
            {
                throw new InvalidOperationException("AAD 认证需要异步 TokenProvider，不能使用同步认证设置。");
            }
            else
            {
                ApplyApiKeyHeader(request, config);
            }
        }

        private static void ApplyApiKeyHeader(HttpRequestMessage request, AiConfig config)
        {
            var mode = GetEffectiveApiKeyHeaderMode(config);

            if (mode == ApiKeyHeaderMode.ApiKeyHeader)
            {
                request.Headers.TryAddWithoutValidation("api-key", config.ApiKey);
                return;
            }

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        /// <summary>
        /// 判断 APIM 是否返回“缺少订阅键”。
        /// </summary>
        protected static bool IsMissingApimSubscriptionKeyResponse(AiConfig config, HttpResponseMessage response, string? body)
        {
            if (!SupportsSubscriptionKeyQueryFallback(config))
                return false;

            return (int)response.StatusCode == 401
                && !string.IsNullOrWhiteSpace(body)
                && body.IndexOf("missing subscription key", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 为 APIM 兼容场景构造带 subscription-key query 的 URL。
        /// </summary>
        protected static string BuildApimSubscriptionKeyQueryUrl(string url, string apiKey)
        {
            var separator = url.Contains('?') ? '&' : '?';
            return $"{url}{separator}subscription-key={Uri.EscapeDataString(apiKey)}";
        }

        protected static string DescribeMediaAuthStrategy(AiConfig config)
        {
            if (config.AzureAuthMode == AzureAuthMode.AAD)
                return "Authorization: Bearer (Azure AAD)";

            var mode = GetEffectiveApiKeyHeaderMode(config);

            return mode == ApiKeyHeaderMode.ApiKeyHeader
                ? "api-key Header"
                : "Authorization: Bearer";
        }

        /// <summary>
        /// 构建 Images API URL（生成）
        /// </summary>
        protected static string BuildImageUrl(AiConfig config)
            => EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ImageApiRouteMode,
                config.DeploymentName,
                config.ApiVersion)[0];

        protected static IReadOnlyList<string> BuildImageGenerateCandidateUrls(AiConfig config)
            => EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ImageApiRouteMode,
                config.DeploymentName,
                config.ApiVersion);

        protected static IReadOnlyList<string> BuildApimDeploymentImageGenerateCandidateUrls(AiConfig config)
            => EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                ImageApiRouteMode.Auto,
                config.DeploymentName,
                config.ApiVersion)
                .Where(url => url.IndexOf("/deployments/", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

        protected static IReadOnlyList<string> BuildImageGenerateCandidateUrlsForRoute(AiConfig config, ImageApiRouteMode routeMode)
            => EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidatesForRoute(
                config.ApiEndpoint,
                config.ApiVersion,
                config.EndpointType,
                routeMode);

        /// <summary>
        /// 构建 Images Edits API URL（编辑/参考图）
        /// gpt-image-1.5 附加参考图时使用 /images/edits 终结点，且需要 multipart/form-data。
        /// </summary>
        protected static string BuildImageEditUrl(AiConfig config)
            => EndpointProfileUrlBuilder.BuildImageEditUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ImageApiRouteMode,
                config.ApiVersion)[0];

        protected static IReadOnlyList<string> BuildImageEditCandidateUrls(AiConfig config)
            => EndpointProfileUrlBuilder.BuildImageEditUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ImageApiRouteMode,
                config.ApiVersion);

        private static EndpointProfileDefinition? ResolveProfile(AiConfig config)
            => EndpointProfileRuntimeResolver.Resolve(config.ProfileId, config.EndpointType);

        private static ApiKeyHeaderMode GetEffectiveApiKeyHeaderMode(AiConfig config)
        {
            if (config.ApiKeyHeaderMode != ApiKeyHeaderMode.Auto)
                return config.ApiKeyHeaderMode;

            var profileDefault = ResolveProfile(config)?.Defaults.ApiKeyHeaderMode ?? ApiKeyHeaderMode.Auto;
            if (profileDefault != ApiKeyHeaderMode.Auto)
                return profileDefault;

            return ApiKeyHeaderMode.Bearer;
        }

        private static bool SupportsSubscriptionKeyQueryFallback(AiConfig config)
            => ResolveProfile(config)?.Auth.SupportsSubscriptionKeyQueryFallback == true;

        /// <summary>
        /// 构建 Videos API URL（创建）
        /// </summary>
        protected static string BuildVideoCreateUrl(AiConfig config)
        {
            return BuildVideoCreateUrl(config, VideoApiMode.SoraJobs);
        }

        /// <summary>
        /// 构建 Videos API URL（创建）
        /// 
        /// - Azure OpenAI:
        ///   - SoraJobs: /openai/v1/video/generations/jobs?api-version=preview
        ///   - Videos:   /openai/v1/videos(?api-version=preview)
        /// - OpenAI Compatible: /v1/videos
        /// </summary>
        protected static string BuildVideoCreateUrl(AiConfig config, VideoApiMode apiMode)
            => EndpointProfileUrlBuilder.BuildVideoCreateUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                apiMode)[0];

        protected static string? BuildVideoCreateUrlWithPreview(AiConfig config, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            foreach (var url in EndpointProfileUrlBuilder.BuildVideoCreateUrlCandidates(
                         config.ApiEndpoint,
                         config.ProfileId,
                         config.EndpointType,
                         config.ApiVersion,
                         apiMode))
            {
                if (url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
                    return url;
            }

            var fallbackUrl = BuildVideoCreateUrl(config, apiMode);
            return fallbackUrl.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? fallbackUrl
                : $"{fallbackUrl}?api-version=preview";
        }

        /// <summary>
        /// 构建 Videos API URL（轮询状态）
        /// </summary>
        protected static string BuildVideoPollUrl(AiConfig config, string videoId)
        {
            return BuildVideoPollUrl(config, videoId, VideoApiMode.SoraJobs);
        }

        /// <summary>
        /// 构建 Videos API URL（轮询状态）
        /// </summary>
        protected static string BuildVideoPollUrl(AiConfig config, string videoId, VideoApiMode apiMode)
            => EndpointProfileUrlBuilder.BuildVideoPollUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                videoId,
                apiMode)[0];

        /// <summary>
        /// 构建 Videos API URL（轮询状态备用路径，带 preview query）。
        /// 用于 APIM/代理层只导入了带 api-version 的视频操作定义时回退尝试。
        /// </summary>
        protected static string? BuildVideoPollUrlWithPreview(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            foreach (var url in EndpointProfileUrlBuilder.BuildVideoPollUrlCandidates(
                         config.ApiEndpoint,
                         config.ProfileId,
                         config.EndpointType,
                         config.ApiVersion,
                         videoId,
                         apiMode))
            {
                if (url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
                    return url;
            }

            var fallbackUrl = BuildVideoPollUrl(config, videoId, apiMode);
            return fallbackUrl.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? fallbackUrl
                : $"{fallbackUrl}?api-version=preview";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容）
        /// </summary>
        protected static string BuildVideoDownloadUrl(AiConfig config, string videoId)
        {
            return BuildVideoDownloadUrl(config, videoId, VideoApiMode.SoraJobs);
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容）
        /// </summary>
        protected static string BuildVideoDownloadUrl(AiConfig config, string videoId, VideoApiMode apiMode)
            => EndpointProfileUrlBuilder.BuildVideoDownloadUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                videoId,
                apiMode)[0];

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径）。
        /// 
        /// 备注：部分实现可能使用 /content/video。
        /// </summary>
        protected static string BuildVideoDownloadUrlAlt(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            var contentVideoCandidates = EndpointProfileUrlBuilder.BuildVideoDownloadVideoContentUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                videoId,
                apiMode);

            if (contentVideoCandidates.Count > 0)
                return contentVideoCandidates[0];

            return BuildVideoDownloadUrl(config, videoId, apiMode);
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径，/content/video）。
        /// 某些 APIM / 代理后端会暴露该形式而不是 /content。
        /// </summary>
        protected static string? BuildVideoDownloadUrlVideoContent(AiConfig config, string videoId, VideoApiMode apiMode)
            => apiMode != VideoApiMode.Videos
                ? null
                : EndpointProfileUrlBuilder.BuildVideoDownloadVideoContentUrlCandidates(
                    config.ApiEndpoint,
                    config.ProfileId,
                    config.EndpointType,
                    config.ApiVersion,
                    videoId,
                    apiMode)[0];

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径，带 preview query）。
        /// 用于 APIM/代理层只导入带 api-version 的视频下载操作定义时回退尝试。
        /// </summary>
        protected static string? BuildVideoDownloadUrlWithPreview(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            foreach (var url in EndpointProfileUrlBuilder.BuildVideoDownloadUrlCandidates(
                         config.ApiEndpoint,
                         config.ProfileId,
                         config.EndpointType,
                         config.ApiVersion,
                         videoId,
                         apiMode))
            {
                if (url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
                    return url;
            }

            var fallbackUrl = BuildVideoDownloadUrl(config, videoId, apiMode);
            return fallbackUrl.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? fallbackUrl
                : $"{fallbackUrl}?api-version=preview";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径，/content/video + preview query）。
        /// </summary>
        protected static string? BuildVideoDownloadUrlVideoContentWithPreview(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            foreach (var url in EndpointProfileUrlBuilder.BuildVideoDownloadVideoContentUrlCandidates(
                         config.ApiEndpoint,
                         config.ProfileId,
                         config.EndpointType,
                         config.ApiVersion,
                         videoId,
                         apiMode))
            {
                if (url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
                    return url;
            }

            var fallbackUrl = BuildVideoDownloadUrlVideoContent(config, videoId, apiMode);
            if (string.IsNullOrWhiteSpace(fallbackUrl))
                return null;

            return fallbackUrl.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? fallbackUrl
                : $"{fallbackUrl}?api-version=preview";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容，使用 generationId）。
        /// 
        /// 说明：Azure 视频任务轮询返回的 generations[].id（gen_...）在部分实现中才是内容下载的标识。
        /// 若 jobs/{taskId}/content 返回 404，可回退尝试此路径。
        /// </summary>
        protected static string BuildVideoGenerationDownloadUrl(AiConfig config, string generationId)
            => EndpointProfileUrlBuilder.BuildVideoGenerationDownloadUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                generationId,
                preferVideoContent: true)[0];

        /// <summary>
        /// 构建 Videos API URL（下载内容，使用 generationId，非 /video 子路径）。
        /// 
        /// 备注：不同后端/版本可能会把内容端点暴露为 /content 或 /content/video。
        /// </summary>
        protected static string BuildVideoGenerationDownloadUrlAlt(AiConfig config, string generationId)
            => EndpointProfileUrlBuilder.BuildVideoGenerationDownloadUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                generationId,
                preferVideoContent: false)[0];
    }
}
