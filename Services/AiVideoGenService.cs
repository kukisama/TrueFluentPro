using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 视频生成服务（Sora 异步轮询模式）
    /// </summary>
    public class AiVideoGenService : AiMediaServiceBase
    {
        private string? _lastSuccessfulDownloadUrl;

        public string LastCreateRequestUrl { get; private set; } = string.Empty;
        public IReadOnlyList<string> LastCreateAttemptedUrls { get; private set; } = Array.Empty<string>();
        public string LastPollRequestUrl { get; private set; } = string.Empty;
        public IReadOnlyList<string> LastPollAttemptedUrls { get; private set; } = Array.Empty<string>();

        private static string GetVideoLogPath()
            => PathManager.Instance.GetLogFile("video_http_debug.log");

        public static string GetVideoDebugLogPath()
            => GetVideoLogPath();

        private static string DescribeEndpointKind(AiConfig config)
        {
            if (IsAzureEndpoint(config))
                return "AzureOpenAI";
            if (IsApimGateway(config))
                return "APIM";
            return "OpenAICompatible";
        }

        private static string DescribeAuthStrategy(AiConfig config)
        {
            if (IsAzureEndpoint(config))
            {
                if (config.AzureAuthMode != AzureAuthMode.AAD && config.ApiKeyHeaderMode == ApiKeyHeaderMode.Bearer)
                    return "Authorization: Bearer (manual override)";

                return config.AzureAuthMode == AzureAuthMode.AAD
                    ? "Authorization: Bearer (Azure AAD)"
                    : "api-key (Azure API Key)";
            }

            if (config.AzureAuthMode != AzureAuthMode.AAD)
            {
                if (config.ApiKeyHeaderMode == ApiKeyHeaderMode.ApiKeyHeader)
                    return "api-key (manual override)";

                if (config.ApiKeyHeaderMode == ApiKeyHeaderMode.Bearer)
                    return "Authorization: Bearer (manual override)";
            }

            if (IsApimGateway(config) && config.AzureAuthMode != AzureAuthMode.AAD)
                return "api-key (APIM subscription header configured as api-key)";

            return "Authorization: Bearer (OpenAI Compatible)";
        }

        private static async Task AppendRequestPlanLogAsync(
            string action,
            AiConfig config,
            string primaryUrl,
            string? fallbackUrl,
            VideoApiMode apiMode,
            string? prompt,
            MediaGenConfig? genConfig,
            string? referenceImagePath,
            CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Action: {action}");
            sb.AppendLine($"EndpointKind: {DescribeEndpointKind(config)}");
            sb.AppendLine($"AuthStrategy: {DescribeAuthStrategy(config)}");
            sb.AppendLine($"ApiEndpoint: {config.ApiEndpoint}");
            sb.AppendLine($"PrimaryUrl: {primaryUrl}");
            if (!string.IsNullOrWhiteSpace(fallbackUrl))
                sb.AppendLine($"FallbackUrl: {fallbackUrl}");
            sb.AppendLine($"ApiMode: {apiMode}");
            sb.AppendLine($"Model: {genConfig?.VideoModel ?? config.ModelName}");
            if (genConfig != null)
            {
                sb.AppendLine($"Size: {genConfig.VideoWidth}x{genConfig.VideoHeight}");
                sb.AppendLine($"Seconds: {genConfig.VideoSeconds}");
                sb.AppendLine($"Variants: {genConfig.VideoVariants}");
                sb.AppendLine($"PollIntervalMs: {genConfig.VideoPollIntervalMs}");
            }
            if (prompt != null)
                sb.AppendLine($"PromptLength: {prompt.Length}");
            sb.AppendLine($"HasReferenceImage: {!string.IsNullOrWhiteSpace(referenceImagePath) && File.Exists(referenceImagePath ?? string.Empty)}");
            if (!string.IsNullOrWhiteSpace(referenceImagePath))
                sb.AppendLine($"ReferenceImagePath: {referenceImagePath}");
            sb.AppendLine($"LogPath: {GetVideoLogPath()}");

            await AppLogService.Instance.LogHttpDebugAsync("video", $"Plan-{action}", sb.ToString(), ct);
        }

        private static string FormatPreparedRequestHeaders(HttpRequestMessage request)
        {
            var sb = new StringBuilder();

            foreach (var header in request.Headers)
            {
                sb.Append(header.Key).Append(": ");

                var isSensitive = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Ocp-Apim-Subscription-Key", StringComparison.OrdinalIgnoreCase);

                if (isSensitive)
                {
                    var raw = string.Join(",", header.Value);
                    sb.Append($"<redacted,len={raw.Length}>");
                }
                else
                {
                    sb.Append(string.Join(",", header.Value));
                }

                sb.AppendLine();
            }

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    sb.Append(header.Key).Append(": ");
                    sb.Append(string.Join(",", header.Value));
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private static async Task AppendPreparedRequestLogAsync(
            string action,
            AiConfig config,
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Action: {action}");
            sb.AppendLine($"EndpointKind: {DescribeEndpointKind(config)}");
            sb.AppendLine($"AuthStrategy: {DescribeAuthStrategy(config)}");
            sb.AppendLine($"ApiKeyPresent: {!string.IsNullOrWhiteSpace(config.ApiKey)}");
            sb.AppendLine($"ApiKeyLength: {config.ApiKey?.Length ?? 0}");
            sb.AppendLine($"Method: {request.Method}");
            sb.AppendLine($"RequestUri: {SanitizeUrlForLog(request.RequestUri?.ToString())}");
            sb.AppendLine("PreparedHeaders:");
            sb.Append(FormatPreparedRequestHeaders(request));
            sb.AppendLine($"LogPath: {GetVideoLogPath()}");

            await AppLogService.Instance.LogHttpDebugAsync("video", $"Prepared-{action}", sb.ToString(), ct);
        }

        private static async Task AppendDownloadCandidatesLogAsync(
            string videoId,
            AiConfig config,
            IReadOnlyList<string> candidates,
            CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Action: DownloadCandidates videoId={videoId}");
            sb.AppendLine($"EndpointKind: {DescribeEndpointKind(config)}");
            sb.AppendLine($"AuthStrategy: {DescribeAuthStrategy(config)}");
            sb.AppendLine($"ApiEndpoint: {config.ApiEndpoint}");
            sb.AppendLine("Candidates:");

            for (var i = 0; i < candidates.Count; i++)
            {
                sb.AppendLine($"[{i + 1}] {SanitizeUrlForLog(candidates[i])}");
            }

            sb.AppendLine($"LogPath: {GetVideoLogPath()}");
            await AppLogService.Instance.LogHttpDebugAsync("video", $"DownloadCandidates videoId={videoId}", sb.ToString(), ct);
        }

        private static async Task AppendDownloadSuccessLogAsync(
            string videoId,
            string url,
            string localPath,
            CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Action: DownloadSuccess videoId={videoId}");
            sb.AppendLine($"SuccessfulUrl: {SanitizeUrlForLog(url)}");
            sb.AppendLine($"SavedTo: {localPath}");
            sb.AppendLine($"LogPath: {GetVideoLogPath()}");
            await AppLogService.Instance.LogHttpDebugAsync("video", $"DownloadSuccess videoId={videoId}", sb.ToString(), ct);
        }

        private static string? SanitizeUrlForLog(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            const string marker = "subscription-key=";
            var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return url;

            var valueStart = idx + marker.Length;
            var valueEnd = url.IndexOf('&', valueStart);
            if (valueEnd < 0)
                valueEnd = url.Length;

            return url[..valueStart] + "<redacted>" + url[valueEnd..];
        }

        /// <summary>
        /// 构造「下载内容」的候选 URL 列表（不发起网络请求）。
        /// 
        /// 用途：当 UI 已经拿到 generationId（如 gen_...）时，可立即把一个可用的下载 URL（通常是第一个候选）写入
        /// <see cref="Models.MediaGenTask.RemoteDownloadUrl"/> 并持久化，从而支持“恢复/断点续传”与更清晰的状态展示。
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> BuildDownloadCandidateUrls(
            AiConfig config,
            string videoId,
            string? generationId,
            VideoApiMode apiMode = VideoApiMode.SoraJobs)
        {
            var urlsToTry = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddUrl(string? u)
            {
                if (string.IsNullOrWhiteSpace(u))
                    return;
                if (seen.Add(u))
                    urlsToTry.Add(u);
            }

            var primaryUrls = EndpointProfileUrlBuilder.BuildVideoDownloadUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                videoId,
                apiMode);
            var contentVideoUrls = EndpointProfileUrlBuilder.BuildVideoDownloadVideoContentUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                videoId,
                apiMode);

            if (!string.IsNullOrWhiteSpace(generationId))
            {
                foreach (var url in EndpointProfileUrlBuilder.BuildVideoGenerationDownloadUrlCandidates(
                             config.ApiEndpoint,
                             config.ProfileId,
                             config.EndpointType,
                             config.ApiVersion,
                             generationId,
                             preferVideoContent: true))
                    AddUrl(url);

                foreach (var url in EndpointProfileUrlBuilder.BuildVideoGenerationDownloadUrlCandidates(
                             config.ApiEndpoint,
                             config.ProfileId,
                             config.EndpointType,
                             config.ApiVersion,
                             generationId,
                             preferVideoContent: false))
                    AddUrl(url);
            }
            else
            {
                foreach (var url in primaryUrls)
                    AddUrl(url);
                foreach (var url in contentVideoUrls)
                    AddUrl(url);
            }

            return urlsToTry;
        }

        private static async Task AppendVideoDebugLogAsync(
            string action,
            string videoId,
            string url,
            HttpResponseMessage response,
            string responseText,
            CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{action} videoId={videoId}");
            sb.AppendLine($"URL: {SanitizeUrlForLog(url)}");
            sb.AppendLine($"HTTP: {(int)response.StatusCode} {response.ReasonPhrase}");

            var ctHeader = response.Content?.Headers?.ContentType?.ToString();
            if (!string.IsNullOrWhiteSpace(ctHeader))
            {
                sb.AppendLine($"Content-Type: {ctHeader}");
            }

            sb.AppendLine("Body:");
            sb.AppendLine(responseText);

            await AppLogService.Instance.LogHttpDebugAsync("video", action, sb.ToString(), ct);
        }

        private static Task AppendPollDebugLogAsync(
            string videoId,
            string url,
            HttpResponseMessage response,
            string responseText,
            CancellationToken ct)
            => AppendVideoDebugLogAsync("Poll", videoId, url, response, responseText, ct);

        private static Task AppendDownloadDebugLogAsync(
            string videoId,
            string url,
            HttpResponseMessage response,
            string responseText,
            CancellationToken ct)
            => AppendVideoDebugLogAsync("Download", videoId, url, response, responseText, ct);

        private static Task AppendCreateDebugLogAsync(
            string videoId,
            string url,
            HttpResponseMessage response,
            string responseText,
            CancellationToken ct)
            => AppendVideoDebugLogAsync("Create", videoId, url, response, responseText, ct);


        private static string RemovePreviewApiVersion(string url)
            => url.Replace("?api-version=preview", "", StringComparison.OrdinalIgnoreCase);

        private static bool IsTerminalSuccessStatus(string status)
        {
            // Azure video job 实测返回: succeeded
            // 兼容其他可能返回: completed / success
            return status is "succeeded" or "completed" or "success";
        }

        private static bool IsTerminalFailureStatus(string status)
        {
            // 兼容常见失败/取消状态
            return status is "failed" or "error" or "cancelled" or "canceled";
        }

        public async Task<(string status, int progress, string? generationId, string? failureReason)> PollStatusDetailsAsync(
            AiConfig config, string videoId, CancellationToken ct, VideoApiMode apiMode)
        {
            var attemptedUrls = new List<string>();
            LastPollRequestUrl = string.Empty;
            LastPollAttemptedUrls = Array.Empty<string>();

            var pollCandidates = EndpointProfileUrlBuilder.BuildVideoPollUrlCandidates(
                config.ApiEndpoint,
                config.ProfileId,
                config.EndpointType,
                config.ApiVersion,
                videoId,
                apiMode);
            if (pollCandidates.Count == 0)
                throw new InvalidOperationException("当前终结点资料包未声明视频轮询路由。请补齐资料包后再试。");

            var url = pollCandidates[0];
            var altUrl = pollCandidates.Count > 1 ? pollCandidates[1] : null;

            await AppendRequestPlanLogAsync(
                $"Poll videoId={videoId}",
                config,
                url,
                altUrl,
                apiMode,
                prompt: null,
                genConfig: null,
                referenceImagePath: null,
                ct);

            async Task<(HttpResponseMessage response, string body, string urlUsed)> SendOnceAsync(string u)
            {
                attemptedUrls.Add(u);
                var req = new HttpRequestMessage(HttpMethod.Get, u);
                await SetAuthHeadersAsync(req, config, ct);
                await AppendPreparedRequestLogAsync($"Poll videoId={videoId}", config, req, ct);
                var resp = await _httpClient.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, resp, body))
                {
                    resp.Dispose();
                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(u, config.ApiKey);
                    attemptedUrls.Add(queryUrl);
                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, queryUrl);
                    await SetAuthHeadersAsync(retryReq, config, ct);
                    await AppendPreparedRequestLogAsync($"Poll-ApimQueryRetry videoId={videoId}", config, retryReq, ct);
                    resp = await _httpClient.SendAsync(retryReq, ct);
                    body = await resp.Content.ReadAsStringAsync(ct);
                    u = queryUrl;
                }

                return (resp, body, u);
            }

            HttpResponseMessage response;
            string json;
            string urlUsed;

            (response, json, urlUsed) = await SendOnceAsync(url);
            LastPollRequestUrl = urlUsed;

            try
            {
                for (var i = 1; i < pollCandidates.Count; i++)
                {
                    if (response.IsSuccessStatusCode || (int)response.StatusCode != 404)
                        break;

                    var nextUrl = pollCandidates[i];
                    if (string.Equals(urlUsed, nextUrl, StringComparison.OrdinalIgnoreCase))
                        continue;

                    response.Dispose();
                    (response, json, urlUsed) = await SendOnceAsync(nextUrl);
                    LastPollRequestUrl = urlUsed;
                }

            LastPollAttemptedUrls = attemptedUrls.ToList();

            await AppendPollDebugLogAsync(videoId, urlUsed, response, json, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"视频状态查询失败: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusElem)
                ? statusElem.GetString() ?? "unknown"
                : "unknown";

            status = status.Trim().ToLowerInvariant();

            var progress = 0;
            if (root.TryGetProperty("progress", out var progressElem))
            {
                if (progressElem.ValueKind == JsonValueKind.Number)
                    progress = progressElem.GetInt32();
            }

            string? failureReason = null;
            if (root.TryGetProperty("failure_reason", out var frElem)
                && frElem.ValueKind == JsonValueKind.String)
            {
                var fr = frElem.GetString();
                if (!string.IsNullOrWhiteSpace(fr))
                    failureReason = fr.Trim();
            }

            string? generationId = null;
            // Jobs 模式常见：generations[].id (gen_...)
            if (root.TryGetProperty("generations", out var gensElem) && gensElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gensElem.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object)
                        continue;
                    if (g.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String)
                    {
                        var id = idElem.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            generationId = id.Trim();
                            break;
                        }
                    }
                }
            }

            // Videos/OpenAI Compatible 可能使用 data[].id
            if (string.IsNullOrWhiteSpace(generationId)
                && root.TryGetProperty("data", out var dataElem)
                && dataElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in dataElem.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object)
                        continue;
                    if (g.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String)
                    {
                        var id = idElem.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            generationId = id.Trim();
                            break;
                        }
                    }
                }
            }

            // 有些实现不会返回 progress，但会返回终态 succeeded/completed。
            if (progress == 0 && IsTerminalSuccessStatus(status))
                progress = 100;

            return (status, progress, generationId, failureReason);
            }
            finally
            {
                response.Dispose();
            }
        }

        /// <summary>
        /// 创建视频生成任务，返回 video_id
        ///
        /// OpenAI 官方 sora-2 API（developers.openai.com/api/reference/resources/videos）：
        ///   POST /v1/videos  (multipart/form-data)
        ///   参数: prompt(必填), model, seconds, size, input_reference(可选 file)
        ///
        /// Azure OpenAI sora-2 也使用 /openai/v1/videos，但文档中 curl 用 JSON。
        /// Azure sora-1 使用 /openai/v1/video/generations/jobs（JSON）。
        /// </summary>
        public async Task<string> CreateVideoAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string? referenceImagePath,
            CancellationToken ct,
            IReadOnlyList<string>? createUrlCandidatesOverride = null,
            bool allowFallbacks = true,
            bool allowApimSubscriptionKeyQueryFallback = true)
        {
            var hasRefImage = !string.IsNullOrWhiteSpace(referenceImagePath) && File.Exists(referenceImagePath);

            // ── sora-2 (Videos 模式)：使用 multipart/form-data（与 OpenAI 官方一致） ──
            if (genConfig.VideoApiMode == VideoApiMode.Videos)
            {
                return await CreateVideoMultipartAsync(config, prompt, genConfig, referenceImagePath, hasRefImage, ct, createUrlCandidatesOverride, allowFallbacks, allowApimSubscriptionKeyQueryFallback);
            }

            // ── sora-1 (SoraJobs 模式)：使用 JSON body ──
            return await CreateVideoJsonAsync(config, prompt, genConfig, ct, createUrlCandidatesOverride, allowFallbacks, allowApimSubscriptionKeyQueryFallback);
        }

        /// <summary>
        /// sora-2: POST /v1/videos  multipart/form-data
        /// 严格按照 OpenAI 官方 API 文档：
        ///   curl https://api.openai.com/v1/videos \
        ///     -F "model=sora-2" \
        ///     -F "prompt=..." \
        ///     -F "input_reference=@image.png"   (可选)
        /// </summary>
        private async Task<string> CreateVideoMultipartAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string? referenceImagePath,
            bool hasRefImage,
            CancellationToken ct,
            IReadOnlyList<string>? createUrlCandidatesOverride,
            bool allowFallbacks,
            bool allowApimSubscriptionKeyQueryFallback)
        {
            var attemptedUrls = new List<string>();
            LastCreateRequestUrl = string.Empty;
            LastCreateAttemptedUrls = Array.Empty<string>();

            var createCandidates = createUrlCandidatesOverride is { Count: > 0 }
                ? createUrlCandidatesOverride
                : EndpointProfileUrlBuilder.BuildVideoCreateUrlCandidates(
                    config.ApiEndpoint,
                    config.ProfileId,
                    config.EndpointType,
                    config.ApiVersion,
                    VideoApiMode.Videos);
            if (createCandidates.Count == 0)
                throw new InvalidOperationException("当前终结点资料包未声明 Videos 创建路由。请补齐资料包后再试。");

            var url = createCandidates[0];
            var altUrl = allowFallbacks && createCandidates.Count > 1 ? createCandidates[1] : null;

            await AppendRequestPlanLogAsync(
                "CreateVideoMultipart",
                config,
                url,
                altUrl,
                VideoApiMode.Videos,
                prompt,
                genConfig,
                referenceImagePath,
                ct);

            using var formContent = new MultipartFormDataContent();
            formContent.Add(new StringContent(genConfig.VideoModel), "model");
            formContent.Add(new StringContent(prompt), "prompt");
            formContent.Add(new StringContent($"{genConfig.VideoWidth}x{genConfig.VideoHeight}"), "size");
            formContent.Add(new StringContent(genConfig.VideoSeconds.ToString()), "seconds");

            // 参考图：官方字段名 input_reference，类型是 file
            if (hasRefImage)
            {
                var imageBytes = await File.ReadAllBytesAsync(referenceImagePath!, ct);
                var ext = Path.GetExtension(referenceImagePath!).ToLowerInvariant();
                var mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/png"
                };
                var fileName = Path.GetFileName(referenceImagePath!);

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
                formContent.Add(imageContent, "input_reference", fileName);
            }

            // 发送 multipart 请求
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = formContent;
            await SetAuthHeadersAsync(request, config, ct);
            await AppendPreparedRequestLogAsync("CreateVideoMultipart", config, request, ct);

            attemptedUrls.Add(url);
            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            LastCreateRequestUrl = url;

            if (allowApimSubscriptionKeyQueryFallback && IsMissingApimSubscriptionKeyResponse(config, response, json))
            {
                response.Dispose();
                var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, queryUrl);
                retryRequest.Content = formContent;
                await SetAuthHeadersAsync(retryRequest, config, ct);
                await AppendPreparedRequestLogAsync("CreateVideoMultipart-ApimQueryRetry", config, retryRequest, ct);

                attemptedUrls.Add(queryUrl);
                response = await _httpClient.SendAsync(retryRequest, ct);
                json = await response.Content.ReadAsStringAsync(ct);
                url = queryUrl;
                LastCreateRequestUrl = url;
            }

            await AppendCreateDebugLogAsync("(create-multipart)", url, response, json, ct);

            if (!response.IsSuccessStatusCode)
            {
                var sc = (int)response.StatusCode;
                var rp = response.ReasonPhrase;
                LastCreateAttemptedUrls = attemptedUrls.ToList();
                response.Dispose();
                throw new HttpRequestException($"视频创建失败: {sc} {rp}. {json}");
            }

            LastCreateAttemptedUrls = attemptedUrls.ToList();
            using (response)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idElem))
                {
                    return idElem.GetString() ?? throw new InvalidOperationException("视频 ID 为空");
                }
                throw new InvalidOperationException($"无法解析视频 ID，响应: {json}");
            }
        }

        /// <summary>
        /// sora-1 / Azure JSON 回退：POST /v1/video/generations/jobs  或  /v1/videos  (JSON body)
        /// 此路径不支持参考图（sora-1 官方无参考图 API，Azure JSON 模式也无此字段）。
        /// </summary>
        private async Task<string> CreateVideoJsonAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            CancellationToken ct,
            IReadOnlyList<string>? createUrlCandidatesOverride,
            bool allowFallbacks,
            bool allowApimSubscriptionKeyQueryFallback)
        {
            var attemptedUrls = new List<string>();
            LastCreateRequestUrl = string.Empty;
            LastCreateAttemptedUrls = Array.Empty<string>();

            var createCandidates = createUrlCandidatesOverride is { Count: > 0 }
                ? createUrlCandidatesOverride
                : EndpointProfileUrlBuilder.BuildVideoCreateUrlCandidates(
                    config.ApiEndpoint,
                    config.ProfileId,
                    config.EndpointType,
                    config.ApiVersion,
                    genConfig.VideoApiMode);
            if (createCandidates.Count == 0)
                throw new InvalidOperationException("当前终结点资料包未声明视频创建路由。请补齐资料包后再试。");

            var url = createCandidates[0];
            var altUrl = allowFallbacks && createCandidates.Count > 1 ? createCandidates[1] : null;

            await AppendRequestPlanLogAsync(
                "CreateVideoJson",
                config,
                url,
                altUrl,
                genConfig.VideoApiMode,
                prompt,
                genConfig,
                referenceImagePath: null,
                ct);

            Dictionary<string, object> bodyObj;
            if (IsAzureEndpoint(config))
            {
                if (genConfig.VideoApiMode == VideoApiMode.Videos)
                {
                    var size = $"{genConfig.VideoWidth}x{genConfig.VideoHeight}";
                    var dict = new Dictionary<string, object>
                    {
                        ["model"] = genConfig.VideoModel,
                        ["prompt"] = prompt,
                        ["size"] = size,
                        ["seconds"] = genConfig.VideoSeconds.ToString()
                    };
                    if (genConfig.VideoVariants > 1)
                        dict["n_variants"] = genConfig.VideoVariants;
                    bodyObj = dict;
                }
                else
                {
                    bodyObj = new Dictionary<string, object>
                    {
                        ["model"] = genConfig.VideoModel,
                        ["prompt"] = prompt,
                        ["height"] = genConfig.VideoHeight,
                        ["width"] = genConfig.VideoWidth,
                        ["n_seconds"] = genConfig.VideoSeconds,
                        ["n_variants"] = genConfig.VideoVariants
                    };
                }
            }
            else
            {
                var size = $"{genConfig.VideoWidth}x{genConfig.VideoHeight}";
                bodyObj = new Dictionary<string, object>
                {
                    ["model"] = genConfig.VideoModel,
                    ["prompt"] = prompt,
                    ["size"] = size,
                    ["seconds"] = genConfig.VideoSeconds.ToString(),
                    ["n_variants"] = genConfig.VideoVariants
                };
            }

            var payloadJson = JsonSerializer.Serialize(bodyObj);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            await SetAuthHeadersAsync(request, config, ct);
            await AppendPreparedRequestLogAsync("CreateVideoJson", config, request, ct);

            attemptedUrls.Add(url);
            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            LastCreateRequestUrl = url;

            if (allowApimSubscriptionKeyQueryFallback && IsMissingApimSubscriptionKeyResponse(config, response, json))
            {
                response.Dispose();
                var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, queryUrl)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                await SetAuthHeadersAsync(retryRequest, config, ct);
                await AppendPreparedRequestLogAsync("CreateVideoJson-ApimQueryRetry", config, retryRequest, ct);

                attemptedUrls.Add(queryUrl);
                response = await _httpClient.SendAsync(retryRequest, ct);
                json = await response.Content.ReadAsStringAsync(ct);
                url = queryUrl;
                LastCreateRequestUrl = url;
            }

            // 资料包显式候选 URL → 顺序重试
            if (allowFallbacks
                && !response.IsSuccessStatusCode
                && (int)response.StatusCode == 404
                && !string.IsNullOrWhiteSpace(altUrl)
                && !string.Equals(url, altUrl, StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();

                using var req2 = new HttpRequestMessage(HttpMethod.Post, altUrl)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                await SetAuthHeadersAsync(req2, config, ct);
                await AppendPreparedRequestLogAsync("CreateVideoJson-Alt", config, req2, ct);
                attemptedUrls.Add(altUrl);
                response = await _httpClient.SendAsync(req2, ct);
                json = await response.Content.ReadAsStringAsync(ct);
                url = altUrl;
                LastCreateRequestUrl = url;
            }

            await AppendCreateDebugLogAsync("(create-json)", url, response, json, ct);

            if (!response.IsSuccessStatusCode)
            {
                var sc = (int)response.StatusCode;
                var rp = response.ReasonPhrase;
                LastCreateAttemptedUrls = attemptedUrls.ToList();
                response.Dispose();
                throw new HttpRequestException($"视频创建失败: {sc} {rp}. {json}");
            }

            LastCreateAttemptedUrls = attemptedUrls.ToList();
            using (response)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idElem))
                {
                    return idElem.GetString() ?? throw new InvalidOperationException("视频 ID 为空");
                }
                throw new InvalidOperationException($"无法解析视频 ID，响应: {json}");
            }
        }

        /// <summary>
        /// 轮询视频状态
        /// </summary>
        public async Task<(string status, int progress, string? failureReason)> PollStatusAsync(
            AiConfig config, string videoId, CancellationToken ct, VideoApiMode apiMode = VideoApiMode.SoraJobs)
        {
            var (status, progress, _, failureReason) = await PollStatusDetailsAsync(config, videoId, ct, apiMode);
            return (status, progress, failureReason);
        }

        /// <summary>
        /// 下载视频到本地文件
        /// </summary>
        public async Task<string?> DownloadVideoAsync(
            AiConfig config,
            string videoId,
            string localPath,
            CancellationToken ct,
            string? generationId = null,
            VideoApiMode apiMode = VideoApiMode.SoraJobs)
        {
            var urlsToTry = BuildDownloadCandidateUrls(config, videoId, generationId, apiMode);
            if (urlsToTry.Count == 0)
                throw new InvalidOperationException("当前终结点资料包未声明视频下载路由。请补齐资料包后再试。");

            async Task<bool> TryDownloadOnceAsync(string url)
            {
                await AppendRequestPlanLogAsync(
                    $"Download videoId={videoId}",
                    config,
                    url,
                    fallbackUrl: null,
                    apiMode,
                    prompt: null,
                    genConfig: null,
                    referenceImagePath: null,
                    ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                await SetAuthHeadersAsync(request, config, ct);
                await AppendPreparedRequestLogAsync($"Download videoId={videoId}", config, request, ct);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(ct);

                    await AppendDownloadDebugLogAsync(videoId, url, response, errorText, ct);

                    if ((int)response.StatusCode == 404)
                        return false;

                    throw new HttpRequestException(
                        $"视频下载失败: {(int)response.StatusCode} {response.ReasonPhrase}. {errorText}");
                }

                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = File.Create(localPath);
                await stream.CopyToAsync(fileStream, ct);
                _lastSuccessfulDownloadUrl = url;
                await AppendDownloadSuccessLogAsync(videoId, url, localPath, ct);
                return true;
            }

            _lastSuccessfulDownloadUrl = null;

            await AppendDownloadCandidatesLogAsync(videoId, config, urlsToTry, ct);

            // 对每个 URL 做少量重试（content 可能延迟可用）
            foreach (var url in urlsToTry)
            {
                for (var i = 0; i < 3; i++)
                {
                    if (await TryDownloadOnceAsync(url))
                        return _lastSuccessfulDownloadUrl;
                    await Task.Delay(2000, ct);
                }
            }

            throw new HttpRequestException("视频下载失败：资料包声明的候选下载地址均未命中可用资源。");
        }

        /// <summary>
        /// 完整流程：创建 → 轮询 → 下载
        /// </summary>
        public async Task<(string filePath, double generateSeconds, double downloadSeconds, string? downloadUrl)> GenerateVideoAsync(
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
            Action<double>? onSucceeded = null)
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            var videoId = await CreateVideoAsync(config, prompt, genConfig, referenceImagePath, ct);
            onVideoIdCreated?.Invoke(videoId);
            onProgress?.Invoke(0);

            var retryCount = 0;
            const int maxRetries = 3;

            string? generationId = null;
            string? failureReason = null;
            string lastStatus = "";
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var (status, progress, genId, fr) = await PollStatusDetailsAsync(
                        config, videoId, ct, genConfig.VideoApiMode);
                    onProgress?.Invoke(progress);
                    retryCount = 0; // 成功后重置重试计数

                    if (!string.IsNullOrWhiteSpace(genId) && generationId == null)
                    {
                        generationId = genId;
                        onGenerationIdResolved?.Invoke(genId);
                    }
                    else if (!string.IsNullOrWhiteSpace(genId))
                    {
                        generationId = genId;
                    }
                    if (!string.IsNullOrWhiteSpace(fr))
                        failureReason = fr;

                    // 状态变化时通知 UI
                    if (!string.IsNullOrWhiteSpace(status) && status != lastStatus)
                    {
                        lastStatus = status;
                        onStatusChanged?.Invoke(status);
                    }

                    if (IsTerminalSuccessStatus(status))
                        break;

                    if (IsTerminalFailureStatus(status))
                    {
                        var detail = string.IsNullOrWhiteSpace(failureReason)
                            ? status
                            : $"{status} ({failureReason})";
                        throw new InvalidOperationException($"视频生成失败: {detail}");
                    }

                    await Task.Delay(genConfig.VideoPollIntervalMs, ct);
                }
                catch (HttpRequestException) when (retryCount < maxRetries)
                {
                    retryCount++;
                    await Task.Delay(genConfig.VideoPollIntervalMs, ct);
                }
            }

            ct.ThrowIfCancellationRequested();

            var generateSeconds = totalSw.Elapsed.TotalSeconds;

            // 通知 UI：succeeded，开始下载
            onSucceeded?.Invoke(generateSeconds);

            var downloadSw = System.Diagnostics.Stopwatch.StartNew();
            var downloadUrl = await DownloadVideoAsync(config, videoId, outputPath, ct, generationId, genConfig.VideoApiMode);
            downloadSw.Stop();
            onProgress?.Invoke(100);

            return (outputPath, generateSeconds, downloadSw.Elapsed.TotalSeconds, downloadUrl);
        }
    }
}
