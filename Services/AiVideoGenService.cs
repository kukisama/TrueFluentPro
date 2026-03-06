using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 视频生成服务（Sora 异步轮询模式）
    /// </summary>
    public class AiVideoGenService : AiMediaServiceBase
    {
        private string? _lastSuccessfulDownloadUrl;

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
                return config.AzureAuthMode == AzureAuthMode.AAD
                    ? "Authorization: Bearer (Azure AAD)"
                    : "api-key (Azure API Key)";
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

            // 首选下载路径（注意：不同模式含义不同）
            var primaryUrl = BuildVideoDownloadUrl(config, videoId, apiMode);
            var primaryAltUrl = BuildVideoDownloadUrlAlt(config, videoId, apiMode);

            // Azure /openai/v1/videos 示例可能不接受 api-version=preview（返回 404），对这种情况准备无参数回退。
            var primaryUrlNoApiVersion = (IsAzureEndpoint(config) && apiMode == VideoApiMode.Videos)
                ? RemovePreviewApiVersion(primaryUrl)
                : null;
            var primaryAltUrlNoApiVersion = (IsAzureEndpoint(config) && apiMode == VideoApiMode.Videos)
                ? RemovePreviewApiVersion(primaryAltUrl)
                : null;

            string? fallbackUrl = null;
            string? fallbackAltUrl = null;
            if (!string.IsNullOrWhiteSpace(generationId))
            {
                fallbackUrl = BuildVideoGenerationDownloadUrl(config, generationId);
                fallbackAltUrl = BuildVideoGenerationDownloadUrlAlt(config, generationId);
            }

            // 组装候选 URL 列表，顺序与 DownloadVideoAsync 中一致
            if (IsAzureEndpoint(config) && apiMode == VideoApiMode.SoraJobs)
            {
                // generationId 下载优先
                AddUrl(fallbackUrl);
                AddUrl(fallbackAltUrl);
                // jobs 内容作为兜底
                AddUrl(primaryUrl);
                AddUrl(primaryAltUrl);
            }
            else
            {
                AddUrl(primaryUrl);
                AddUrl(primaryAltUrl);
                AddUrl(primaryUrlNoApiVersion);
                AddUrl(primaryAltUrlNoApiVersion);
                AddUrl(fallbackUrl);
                AddUrl(fallbackAltUrl);
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
            var url = BuildVideoPollUrl(config, videoId, apiMode);
            var altUrl = (IsAzureEndpoint(config) && apiMode == VideoApiMode.Videos)
                ? BuildVideoPollUrlWithPreview(config, videoId, apiMode)
                : null;
            var previewAltUrl = (IsApimGateway(config) && apiMode == VideoApiMode.Videos)
                ? BuildVideoPollUrlWithPreview(config, videoId, apiMode)
                : null;

            await AppendRequestPlanLogAsync(
                $"Poll videoId={videoId}",
                config,
                url,
                !string.IsNullOrWhiteSpace(altUrl) ? altUrl : previewAltUrl,
                apiMode,
                prompt: null,
                genConfig: null,
                referenceImagePath: null,
                ct);

            async Task<(HttpResponseMessage response, string body, string urlUsed)> SendOnceAsync(string u)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, u);
                await SetAuthHeadersAsync(req, config, ct);
                await AppendPreparedRequestLogAsync($"Poll videoId={videoId}", config, req, ct);
                var resp = await _httpClient.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, resp, body))
                {
                    resp.Dispose();
                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(u, config.ApiKey);
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

            try
            {
                // 某些示例/后端可能不接受 api-version=preview，且会返回 404；对该情况做一次回退。
                if (!response.IsSuccessStatusCode
                    && (int)response.StatusCode == 404
                    && !string.IsNullOrWhiteSpace(altUrl)
                    && !string.Equals(url, altUrl, StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    (response, json, urlUsed) = await SendOnceAsync(altUrl);
                }

                if (!response.IsSuccessStatusCode
                    && (int)response.StatusCode == 404
                    && !string.IsNullOrWhiteSpace(previewAltUrl)
                    && !string.Equals(urlUsed, previewAltUrl, StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    (response, json, urlUsed) = await SendOnceAsync(previewAltUrl);
                }

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
            CancellationToken ct)
        {
            var hasRefImage = !string.IsNullOrWhiteSpace(referenceImagePath) && File.Exists(referenceImagePath);

            // ── sora-2 (Videos 模式)：使用 multipart/form-data（与 OpenAI 官方一致） ──
            if (genConfig.VideoApiMode == VideoApiMode.Videos)
            {
                return await CreateVideoMultipartAsync(config, prompt, genConfig, referenceImagePath, hasRefImage, ct);
            }

            // ── sora-1 (SoraJobs 模式)：使用 JSON body ──
            return await CreateVideoJsonAsync(config, prompt, genConfig, ct);
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
            CancellationToken ct)
        {
            var url = BuildVideoCreateUrl(config, VideoApiMode.Videos);
            var altUrl = (IsAzureEndpoint(config))
                ? BuildVideoCreateUrlWithPreview(config, VideoApiMode.Videos)
                : null;

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

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (IsMissingApimSubscriptionKeyResponse(config, response, json))
            {
                response.Dispose();
                var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, queryUrl);
                retryRequest.Content = formContent;
                await SetAuthHeadersAsync(retryRequest, config, ct);
                await AppendPreparedRequestLogAsync("CreateVideoMultipart-ApimQueryRetry", config, retryRequest, ct);

                response = await _httpClient.SendAsync(retryRequest, ct);
                json = await response.Content.ReadAsStringAsync(ct);
                url = queryUrl;
            }

            await AppendCreateDebugLogAsync("(create-multipart)", url, response, json, ct);

            // 如果 multipart 返回 404/415（Azure 某些版本可能不支持 multipart），回退到 JSON
            if (!response.IsSuccessStatusCode && (int)response.StatusCode is 404 or 415)
            {
                response.Dispose();

                // 回退：尝试不带 api-version 的 URL
                if (!string.IsNullOrWhiteSpace(altUrl) && !string.Equals(url, altUrl, StringComparison.OrdinalIgnoreCase))
                {
                    using var formContent2 = new MultipartFormDataContent();
                    formContent2.Add(new StringContent(genConfig.VideoModel), "model");
                    formContent2.Add(new StringContent(prompt), "prompt");
                    formContent2.Add(new StringContent($"{genConfig.VideoWidth}x{genConfig.VideoHeight}"), "size");
                    formContent2.Add(new StringContent(genConfig.VideoSeconds.ToString()), "seconds");
                    if (hasRefImage)
                    {
                        var imageBytes2 = await File.ReadAllBytesAsync(referenceImagePath!, ct);
                        var ext2 = Path.GetExtension(referenceImagePath!).ToLowerInvariant();
                        var mimeType2 = ext2 switch { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
                        var ic2 = new ByteArrayContent(imageBytes2);
                        ic2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType2);
                        formContent2.Add(ic2, "input_reference", Path.GetFileName(referenceImagePath!));
                    }

                    using var req2 = new HttpRequestMessage(HttpMethod.Post, altUrl);
                    req2.Content = formContent2;
                    await SetAuthHeadersAsync(req2, config, ct);
                    await AppendPreparedRequestLogAsync("CreateVideoMultipart-Alt", config, req2, ct);
                    response = await _httpClient.SendAsync(req2, ct);
                    json = await response.Content.ReadAsStringAsync(ct);

                    await AppendCreateDebugLogAsync("(create-multipart-alt)", altUrl, response, json, ct);
                }

                // 如果 multipart 还是不行，回退到 JSON（Azure 文档中的 curl 用 JSON）
                if (!response.IsSuccessStatusCode && (int)response.StatusCode is 404 or 415)
                {
                    response.Dispose();
                    return await CreateVideoJsonAsync(config, prompt, genConfig, ct);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var sc = (int)response.StatusCode;
                var rp = response.ReasonPhrase;
                response.Dispose();
                throw new HttpRequestException($"视频创建失败: {sc} {rp}. {json}");
            }

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
            CancellationToken ct)
        {
            var url = BuildVideoCreateUrl(config, genConfig.VideoApiMode);
            var altUrl = (IsAzureEndpoint(config) && genConfig.VideoApiMode == VideoApiMode.Videos)
                ? BuildVideoCreateUrlWithPreview(config, genConfig.VideoApiMode)
                : null;

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

            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (IsMissingApimSubscriptionKeyResponse(config, response, json))
            {
                response.Dispose();
                var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, queryUrl)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                await SetAuthHeadersAsync(retryRequest, config, ct);
                await AppendPreparedRequestLogAsync("CreateVideoJson-ApimQueryRetry", config, retryRequest, ct);

                response = await _httpClient.SendAsync(retryRequest, ct);
                json = await response.Content.ReadAsStringAsync(ct);
                url = queryUrl;
            }

            // 404 且有备用 URL → 重试
            if (!response.IsSuccessStatusCode
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
                response = await _httpClient.SendAsync(req2, ct);
                json = await response.Content.ReadAsStringAsync(ct);
                url = altUrl;
            }

            await AppendCreateDebugLogAsync("(create-json)", url, response, json, ct);

            if (!response.IsSuccessStatusCode)
            {
                var sc = (int)response.StatusCode;
                var rp = response.ReasonPhrase;
                response.Dispose();
                throw new HttpRequestException($"视频创建失败: {sc} {rp}. {json}");
            }

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
            // Azure 实测：轮询是 jobs/{taskId}，但下载 content 可能需要使用 generations[].id（gen_...）。
            // 因此下载采用“多 URL 尝试 + 对 404 做短暂重试”的策略。

            string? resolvedGenId = generationId;

            // 首选下载路径（注意：不同模式含义不同）
            var primaryUrl = BuildVideoDownloadUrl(config, videoId, apiMode);
            var primaryAltUrl = BuildVideoDownloadUrlAlt(config, videoId, apiMode);
            var primaryVideoContentUrl = BuildVideoDownloadUrlVideoContent(config, videoId, apiMode);
            var primaryPreviewUrl = ((IsApimGateway(config) || IsAzureEndpoint(config)) && apiMode == VideoApiMode.Videos)
                ? BuildVideoDownloadUrlWithPreview(config, videoId, apiMode)
                : null;
            var primaryVideoContentPreviewUrl = ((IsApimGateway(config) || IsAzureEndpoint(config)) && apiMode == VideoApiMode.Videos)
                ? BuildVideoDownloadUrlVideoContentWithPreview(config, videoId, apiMode)
                : null;

            // AOAI 现在主路不带 api-version；保留 preview 版本作为回退。
            var primaryUrlNoApiVersion = (IsAzureEndpoint(config) && apiMode == VideoApiMode.Videos)
                ? BuildVideoDownloadUrlWithPreview(config, videoId, apiMode)
                : null;
            var primaryAltUrlNoApiVersion = (IsAzureEndpoint(config) && apiMode == VideoApiMode.Videos)
                ? BuildVideoDownloadUrlVideoContentWithPreview(config, videoId, apiMode)
                : null;

            // 如果未提供 generationId，先尝试从轮询响应解析出来。
            // 备注：即使 status 不是终态，部分后端也可能已经返回 generations[].id。
            if (IsAzureEndpoint(config)
                && apiMode == VideoApiMode.SoraJobs
                && string.IsNullOrWhiteSpace(resolvedGenId))
            {
                try
                {
                    var (st, _, genId, _) = await PollStatusDetailsAsync(config, videoId, ct, apiMode);
                    if (!string.IsNullOrWhiteSpace(genId))
                        resolvedGenId = genId;
                }
                catch
                {
                    // 解析失败不阻断后续下载尝试
                }
            }

            string? fallbackUrl = null;
            string? fallbackAltUrl = null;
            if (!string.IsNullOrWhiteSpace(resolvedGenId))
            {
                fallbackUrl = BuildVideoGenerationDownloadUrl(config, resolvedGenId);
                fallbackAltUrl = BuildVideoGenerationDownloadUrlAlt(config, resolvedGenId);
            }

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

            // 组装待尝试 URL 列表：
            // - Azure + SoraJobs：优先尝试 generations/{genId}/content/video（更可靠）；jobs/{taskId}/content 作为兜底。
            // - 其他：按 primary → alt → 无 api-version 回退的顺序。
            var urlsToTry = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddUrl(string? u)
            {
                if (string.IsNullOrWhiteSpace(u))
                    return;
                if (seen.Add(u))
                    urlsToTry.Add(u);
            }

            if (IsAzureEndpoint(config) && apiMode == VideoApiMode.SoraJobs)
            {
                // generationId 下载优先
                AddUrl(fallbackUrl);
                AddUrl(fallbackAltUrl);
                // jobs 内容作为兜底（部分环境会一直 404）
                AddUrl(primaryUrl);
                AddUrl(primaryAltUrl);
            }
            else
            {
                AddUrl(primaryUrl);
                AddUrl(primaryAltUrl);
                AddUrl(primaryVideoContentUrl);
                AddUrl(primaryPreviewUrl);
                AddUrl(primaryVideoContentPreviewUrl);
                AddUrl(primaryUrlNoApiVersion);
                AddUrl(primaryAltUrlNoApiVersion);
                AddUrl(fallbackUrl);
                AddUrl(fallbackAltUrl);
            }

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

            throw new HttpRequestException(
                "视频下载失败: 404 Resource Not Found（已尝试 jobs/{taskId}/content，并回退尝试 generations/{genId}/content/video 与 generations/{genId}/content）");
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
