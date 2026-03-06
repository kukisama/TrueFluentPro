using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

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

        private static bool LooksLikeAzureOpenAiEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)
                || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 媒体链路中的 AOAI 判定遵循当前约定：仅官方 Azure OpenAI 主机名（*.openai.azure.com）视为 AOAI。
        /// APIM / 其他代理即使承载 Azure 资源，也按 OpenAI Compatible 访问路径处理。
        /// </summary>
        protected static bool IsAzureEndpoint(AiConfig config)
            => LooksLikeAzureOpenAiEndpoint(config.ApiEndpoint);

        /// <summary>
        /// 判断是否为 APIM 网关前门。
        /// 这类终结点常暴露 OpenAI Compatible 路径（如 /openai/v1/...），
        /// 但在 API Key 模式下要求客户端携带 APIM 订阅键头，而不是 Bearer。
        /// </summary>
        protected static bool IsApimGateway(AiConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.ApiEndpoint)
                || !Uri.TryCreate(config.ApiEndpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.EndsWith(".azure-api.net", StringComparison.OrdinalIgnoreCase);
        }

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
            else if (IsAzureEndpoint(config) || IsApimGateway(config))
            {
                request.Headers.TryAddWithoutValidation("api-key", config.ApiKey);
            }
            else
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
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
            else if (IsAzureEndpoint(config) || IsApimGateway(config))
            {
                request.Headers.TryAddWithoutValidation("api-key", config.ApiKey);
            }
            else
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
            }
        }

        /// <summary>
        /// 判断 APIM 是否返回“缺少订阅键”。
        /// </summary>
        protected static bool IsMissingApimSubscriptionKeyResponse(AiConfig config, HttpResponseMessage response, string? body)
        {
            if (!IsApimGateway(config))
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

        /// <summary>
        /// 构建 Images API URL（生成）
        /// </summary>
        protected static string BuildImageUrl(AiConfig config)
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                // 旧方式（传统 Azure 部署路径）：
                // return $"{baseUrl}/openai/deployments/{config.DeploymentName}/images/generations?api-version={config.ApiVersion}";

                // 新方式：走 OpenAI 兼容路径
                return $"{baseUrl}/openai/v1/images/generations";
            }

            // OpenAI Compatible
            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/images/generations";
            return $"{baseUrl}/v1/images/generations";
        }

        /// <summary>
        /// 构建 Images Edits API URL（编辑/参考图）
        /// gpt-image-1.5 附加参考图时使用 /images/edits 终结点，且需要 multipart/form-data。
        /// </summary>
        protected static string BuildImageEditUrl(AiConfig config)
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                // 旧方式（传统 Azure 部署路径，/images/edits 返回 404）：
                // return $"{baseUrl}/openai/deployments/{config.DeploymentName}/images/edits?api-version={config.ApiVersion}";

                // 新方式：走 OpenAI 兼容路径
                return $"{baseUrl}/openai/v1/images/edits";
            }

            // OpenAI Compatible
            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/images/edits";
            return $"{baseUrl}/v1/images/edits";
        }

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
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                if (apiMode == VideoApiMode.Videos)
                {
                    return $"{baseUrl}/openai/v1/videos";
                }

                return $"{baseUrl}/openai/v1/video/generations/jobs?api-version=preview";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos";
            return $"{baseUrl}/v1/videos";
        }

        protected static string? BuildVideoCreateUrlWithPreview(AiConfig config, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            var url = BuildVideoCreateUrl(config, apiMode);
            return url.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? url
                : $"{url}?api-version=preview";
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
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                if (apiMode == VideoApiMode.Videos)
                {
                    return $"{baseUrl}/openai/v1/videos/{videoId}";
                }

                return $"{baseUrl}/openai/v1/video/generations/jobs/{videoId}?api-version=preview";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos/{videoId}";
            return $"{baseUrl}/v1/videos/{videoId}";
        }

        /// <summary>
        /// 构建 Videos API URL（轮询状态备用路径，带 preview query）。
        /// 用于 APIM/代理层只导入了带 api-version 的视频操作定义时回退尝试。
        /// </summary>
        protected static string? BuildVideoPollUrlWithPreview(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            var url = BuildVideoPollUrl(config, videoId, apiMode);
            return url.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? url
                : $"{url}?api-version=preview";
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
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                if (apiMode == VideoApiMode.Videos)
                {
                    return $"{baseUrl}/openai/v1/videos/{videoId}/content";
                }

                return $"{baseUrl}/openai/v1/video/generations/jobs/{videoId}/content?api-version=preview";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos/{videoId}/content";
            return $"{baseUrl}/v1/videos/{videoId}/content";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径）。
        /// 
        /// 备注：部分实现可能使用 /content/video。
        /// </summary>
        protected static string BuildVideoDownloadUrlAlt(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                if (apiMode == VideoApiMode.Videos)
                {
                    return $"{baseUrl}/openai/v1/videos/{videoId}/content/video";
                }

                return $"{baseUrl}/openai/v1/video/generations/jobs/{videoId}/content?api-version=preview";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos/{videoId}/content";
            return $"{baseUrl}/v1/videos/{videoId}/content";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径，/content/video）。
        /// 某些 APIM / 代理后端会暴露该形式而不是 /content。
        /// </summary>
        protected static string? BuildVideoDownloadUrlVideoContent(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                return $"{baseUrl}/openai/v1/videos/{videoId}/content/video";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos/{videoId}/content/video";
            return $"{baseUrl}/v1/videos/{videoId}/content/video";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径，带 preview query）。
        /// 用于 APIM/代理层只导入带 api-version 的视频下载操作定义时回退尝试。
        /// </summary>
        protected static string? BuildVideoDownloadUrlWithPreview(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            if (apiMode != VideoApiMode.Videos)
                return null;

            var url = BuildVideoDownloadUrl(config, videoId, apiMode);
            return url.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? url
                : $"{url}?api-version=preview";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容备用路径，/content/video + preview query）。
        /// </summary>
        protected static string? BuildVideoDownloadUrlVideoContentWithPreview(AiConfig config, string videoId, VideoApiMode apiMode)
        {
            var url = BuildVideoDownloadUrlVideoContent(config, videoId, apiMode);
            if (string.IsNullOrWhiteSpace(url))
                return null;

            return url.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
                ? url
                : $"{url}?api-version=preview";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容，使用 generationId）。
        /// 
        /// 说明：Azure 视频任务轮询返回的 generations[].id（gen_...）在部分实现中才是内容下载的标识。
        /// 若 jobs/{taskId}/content 返回 404，可回退尝试此路径。
        /// </summary>
        protected static string BuildVideoGenerationDownloadUrl(AiConfig config, string generationId)
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                // 官方示例：/openai/v1/video/generations/{generationId}/content/video?api-version=preview
                return $"{baseUrl}/openai/v1/video/generations/{generationId}/content/video";
            }

            // OpenAI Compatible：目前项目只实现 /videos/{id}/content；如后续 provider 返回 generationId，可在此扩展。
            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos/{generationId}/content";
            return $"{baseUrl}/v1/videos/{generationId}/content";
        }

        /// <summary>
        /// 构建 Videos API URL（下载内容，使用 generationId，非 /video 子路径）。
        /// 
        /// 备注：不同后端/版本可能会把内容端点暴露为 /content 或 /content/video。
        /// </summary>
        protected static string BuildVideoGenerationDownloadUrlAlt(AiConfig config, string generationId)
        {
            var baseUrl = config.ApiEndpoint.TrimEnd('/');

            if (IsAzureEndpoint(config))
            {
                return $"{baseUrl}/openai/v1/video/generations/{generationId}/content";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/videos/{generationId}/content";
            return $"{baseUrl}/v1/videos/{generationId}/content";
        }
    }
}
