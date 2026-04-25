using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 图片生成结果（含耗时信息）
    /// </summary>
    public class ImageGenerationResult
    {
        public List<byte[]> Images { get; set; } = new();
        /// <summary>服务端生成耗时（秒）：从发送请求到 headers 返回</summary>
        public double GenerateSeconds { get; set; }
        /// <summary>下载传输耗时（秒）：从 headers 返回到 body 读取完毕</summary>
        public double DownloadSeconds { get; set; }
        public string RequestUrl { get; set; } = "";
        public IReadOnlyList<string> AttemptedUrls { get; set; } = Array.Empty<string>();
        /// <summary>Responses API 返回的 response id，可用于 previous_response_id 多轮改图</summary>
        public string? ResponseId { get; set; }

        // ─── API response.usage 实际 token ───
        public int? ActualInputTokens { get; set; }
        public int? ActualOutputTokens { get; set; }
        public int? ActualImageInputTokens { get; set; }
        public int? ActualImageOutputTokens { get; set; }
        public int? ActualCachedTokens { get; set; }
    }

    /// <summary>
    /// 图片路由探测结果
    /// </summary>
    public sealed class ImageRouteProbeResult
    {
        public required string RouteLabel { get; init; }
        public required IReadOnlyList<string> AttemptedUrls { get; init; }
        public bool IsSuccess { get; init; }
        public string? SuccessfulUrl { get; init; }
        public int ImageCount { get; init; }
        public int? StatusCode { get; init; }
        public string? ReasonPhrase { get; init; }
        public string? ErrorText { get; init; }
    }

    /// <summary>
    /// 图片生成+保存结果（含文件路径和耗时）
    /// </summary>
    public class ImageSaveResult
    {
        public List<string> FilePaths { get; set; } = new();
        public double GenerateSeconds { get; set; }
        public double DownloadSeconds { get; set; }
        /// <summary>Responses API 返回的 response id，可用于 previous_response_id 多轮改图</summary>
        public string? ResponseId { get; set; }

        // ─── 实际生成图片尺寸 ───
        public int? ActualWidth { get; set; }
        public int? ActualHeight { get; set; }

        // ─── API response.usage 实际 token ───
        public int? ActualInputTokens { get; set; }
        public int? ActualOutputTokens { get; set; }
        public int? ActualImageInputTokens { get; set; }
        public int? ActualImageOutputTokens { get; set; }
        public int? ActualCachedTokens { get; set; }
    }

    /// <summary>
    /// 图片生成服务（OpenAI Compatible Images API）
    /// </summary>
    public class AiImageGenService : AiMediaServiceBase, IAiImageGenService
    {
        private readonly FileIdCache _fileIdCache;

        public AiImageGenService(FileIdCache fileIdCache)
        {
            _fileIdCache = fileIdCache;
        }

        private sealed class ImageAttemptResult
        {
            public required HttpResponseMessage Response { get; init; }
            public required string Url { get; init; }
            public string? ErrorText { get; init; }
            public IReadOnlyList<string> AttemptedUrls { get; init; } = Array.Empty<string>();
        }

        private static string FormatResponseHeaders(HttpResponseMessage response)
        {
            var sb = new StringBuilder();

            foreach (var h in response.Headers)
            {
                sb.Append(h.Key);
                sb.Append('=').AppendLine(string.Join(",", h.Value));
            }

            if (response.Content != null)
            {
                foreach (var h in response.Content.Headers)
                {
                    sb.Append(h.Key);
                    sb.Append('=').AppendLine(string.Join(",", h.Value));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成图片，返回 base64 解码后的字节数组列表及耗时信息。
        /// onProgress: 0-49 = 等待服务端生成, 50-95 = 下载响应体, 100 = 完成
        /// </summary>
        public async Task<ImageGenerationResult> GenerateImagesAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null,
            Action<int>? onProgress = null)
        {
            onProgress?.Invoke(0);
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            HttpResponseMessage? response = null;
            var requestUrl = string.Empty;
            IReadOnlyList<string> attemptedUrls = Array.Empty<string>();

            var validReferenceImages = (referenceImagePaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .ToList();

            await AppLogService.Instance.LogHttpDebugAsync(
                "image",
                "ImageRequest.Start",
                $"Mode={(validReferenceImages.Count == 0 ? "Generate" : "Edit")}\n" +
                $"ProviderType={config.ProviderType}\n" +
                $"ApiEndpoint={config.ApiEndpoint}\n" +
                $"DeploymentName={config.DeploymentName}\n" +
                $"ApiVersion={config.ApiVersion}\n" +
                $"ImageModel={genConfig.ImageModel}\n" +
                $"ImageSize={genConfig.ImageSize}\n" +
                $"ImageQuality={genConfig.ImageQuality}\n" +
                $"ImageFormat={genConfig.ImageFormat}\n" +
                $"ImageCount={genConfig.ImageCount}\n" +
                $"ReferenceImageCount={validReferenceImages.Count}\n" +
                $"ReferenceImages={string.Join(";", validReferenceImages)}",
                ct);

            if (validReferenceImages.Count > 0)
            {
                // ── 有参考图：自动路由 ──
                // 优先 Responses API（需要 TextModelForResponses），否则 fallback 到 V1 multipart
                var authModeText = DescribeMediaAuthStrategy(config);
                var imageNames = validReferenceImages
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToList();

                ImageAttemptResult editAttempt;
                // 查询模型能力声明（用于 size 智能过滤）
                var modelCaps = App.Services?.GetService<ImageModelCatalogService>()?.GetCapabilities(genConfig.ImageModel);

                if (ShouldUseResponsesApi(genConfig))
                {
                    editAttempt = await SendImageEditV2RequestAsync(config, prompt, genConfig, validReferenceImages, authModeText, imageNames, ct, modelCaps);
                }
                else
                {
                    editAttempt = await SendImageEditRequestAsync(config, prompt, genConfig, validReferenceImages, authModeText, imageNames, ct, modelCaps);
                }
                response = editAttempt.Response;
                requestUrl = editAttempt.Url;
                attemptedUrls = editAttempt.AttemptedUrls;

                // 不回退，直接暴露错误
                if (response == null)
                {
                    throw new HttpRequestException("图片编辑失败: 未发送请求");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = editAttempt.ErrorText ?? await response.Content.ReadAsStringAsync(ct);
                    var sc = response.StatusCode; var rp = response.ReasonPhrase;
                    var oneapiRequestId = response.Headers.TryGetValues("X-Oneapi-Request-Id", out var ridVals)
                        ? string.Join(",", ridVals)
                        : string.Empty;
                    await AppLogService.Instance.LogHttpDebugAsync(
                        "image",
                        "ImageEdit.Error",
                        $"HTTP={(int)sc} {rp}\n" +
                        $"URL={editAttempt.Url}\n" +
                        $"Headers:\n{FormatResponseHeaders(response)}\n" +
                        $"Body={errorText}",
                        ct);
                    response.Dispose();
                    throw new HttpRequestException(
                        $"图片编辑失败: {(int)sc} {rp}. " +
                        (string.IsNullOrWhiteSpace(oneapiRequestId)
                            ? string.Empty
                            : $"[X-Oneapi-Request-Id: {oneapiRequestId}] ") +
                        $"{errorText}");
                }
            }
            else
            {
                // ── 无参考图 ──
                // 自动路由：TextModelForResponses 可用时走 Responses API，否则 fallback /images/generations
                // 查询模型能力（用于 size 智能过滤）
                var modelCaps = App.Services?.GetService<ImageModelCatalogService>()?.GetCapabilities(genConfig.ImageModel);

                if (ShouldUseResponsesApi(genConfig))
                {
                    var authModeText = DescribeMediaAuthStrategy(config);
                    var responsesAttempt = await SendPureTextGenV2RequestAsync(config, prompt, genConfig, authModeText, ct, modelCaps);
                    response = responsesAttempt.Response;
                    requestUrl = responsesAttempt.Url;
                    attemptedUrls = responsesAttempt.AttemptedUrls;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorText = responsesAttempt.ErrorText ?? await response.Content.ReadAsStringAsync(ct);
                        var sc = response.StatusCode; var rp = response.ReasonPhrase;
                        await AppLogService.Instance.LogHttpDebugAsync(
                            "image", "ImageGenerateV2-ResponsesApi.Error",
                            $"HTTP={(int)sc} {rp}\nURL={responsesAttempt.Url}\nBody={errorText}", ct);
                        response.Dispose();
                        throw new HttpRequestException($"图片生成失败(Responses API): {(int)sc} {rp}. {errorText}");
                    }
                }
                else
                {
                    // /images/generations 直接路径
                    // size 按模型能力过滤：FreeForm 传任意值，Fixed 仅传声明值，否则回退 auto
                    var effectiveSize = ResolveEffectiveSize(genConfig.ImageSize, modelCaps);

                    var bodyObj = new Dictionary<string, object>
                    {
                        ["prompt"] = prompt,
                        ["model"] = genConfig.ImageModel,
                        ["size"] = effectiveSize,
                        ["quality"] = genConfig.ImageQuality,
                        ["output_format"] = genConfig.ImageFormat
                    };
                    if (genConfig.ImageCount > 1)
                        bodyObj["n"] = genConfig.ImageCount;
                    if (!string.IsNullOrWhiteSpace(genConfig.ImageBackground) && genConfig.ImageBackground != "auto")
                        bodyObj["background"] = genConfig.ImageBackground;
                    var authModeText = DescribeMediaAuthStrategy(config);
                    var generateAttempt = await SendImageGenerateRequestAsync(config, bodyObj, authModeText, ct);
                    response = generateAttempt.Response;
                    requestUrl = generateAttempt.Url;
                    attemptedUrls = generateAttempt.AttemptedUrls;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorText = generateAttempt.ErrorText ?? await response.Content.ReadAsStringAsync(ct);
                        var statusCode = response.StatusCode;
                        var reasonPhrase = response.ReasonPhrase;
                        var oneapiRequestId = response.Headers.TryGetValues("X-Oneapi-Request-Id", out var ridVals)
                            ? string.Join(",", ridVals)
                            : string.Empty;
                        await AppLogService.Instance.LogHttpDebugAsync(
                            "image",
                            "ImageGenerate.Error",
                            $"HTTP={(int)statusCode} {reasonPhrase}\n" +
                            $"URL={generateAttempt.Url}\n" +
                            $"Headers:\n{FormatResponseHeaders(response)}\n" +
                            $"Body={errorText}",
                            ct);
                        response.Dispose();
                        throw new HttpRequestException(
                            $"图片生成失败: {(int)statusCode} {reasonPhrase}. " +
                            (string.IsNullOrWhiteSpace(oneapiRequestId)
                                ? string.Empty
                                : $"[X-Oneapi-Request-Id: {oneapiRequestId}] ") +
                            $"{errorText}");
                    }
                }
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"图片生成失败: {(int)response.StatusCode} {response.ReasonPhrase}. {errorText}");
                }

                // 服务端已返回 header → 生成完毕，进入下载阶段
                var generateSeconds = totalStopwatch.Elapsed.TotalSeconds;
                onProgress?.Invoke(50);

                // 分块读取响应体，追踪下载进度
                var contentLength = response.Content.Headers.ContentLength; // 可能为 null
                using var responseStream = await response.Content.ReadAsStreamAsync(ct);

            using var ms = new MemoryStream();
            var buffer = new byte[81920]; // 80KB 每块
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await responseStream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (contentLength is > 0)
                {
                    // 下载进度映射到 50-95
                    var downloadPercent = (int)(totalRead * 45 / contentLength.Value);
                    onProgress?.Invoke(50 + Math.Min(downloadPercent, 45));
                }
            }

                onProgress?.Invoke(96); // 下载完毕，开始解析
                var downloadSeconds = totalStopwatch.Elapsed.TotalSeconds - generateSeconds;

            var json = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var results = new List<byte[]>();

            if (root.TryGetProperty("data", out var dataArray))
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    if (item.TryGetProperty("b64_json", out var b64Elem))
                    {
                        var b64 = b64Elem.GetString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            results.Add(Convert.FromBase64String(b64));
                        }
                    }
                    else if (item.TryGetProperty("url", out var urlElem))
                    {
                        var imageUrl = urlElem.GetString();
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl, ct);
                            results.Add(imageBytes);
                        }
                    }
                }
            }
            // Responses API 格式：output[] 中 type=image_generation_call 的 result 字段为 base64
            else if (root.TryGetProperty("output", out var outputArray))
            {
                foreach (var item in outputArray.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeElem)
                        && typeElem.GetString() == "image_generation_call"
                        && item.TryGetProperty("result", out var resultElem))
                    {
                        var b64 = resultElem.GetString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            results.Add(Convert.FromBase64String(b64));
                        }
                    }
                }
            }

            // 提取 Responses API 的 response id（用于 previous_response_id 多轮改图）
            string? responseId = null;
            if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String)
            {
                responseId = idElem.GetString();
            }

            // 解析 response.usage 实际 token（Responses API 和 Images API 均可能返回）
            int? actualInputTokens = null, actualOutputTokens = null;
            int? actualImageInputTokens = null, actualImageOutputTokens = null, actualCachedTokens = null;
            if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
            {
                if (usageElem.TryGetProperty("input_tokens", out var it)) actualInputTokens = it.GetInt32();
                if (usageElem.TryGetProperty("output_tokens", out var ot)) actualOutputTokens = ot.GetInt32();
                // Responses API 图片专用细分
                if (usageElem.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object)
                {
                    if (itd.TryGetProperty("image_tokens", out var iit)) actualImageInputTokens = iit.GetInt32();
                    if (itd.TryGetProperty("cached_tokens", out var ct2)) actualCachedTokens = ct2.GetInt32();
                }
                if (usageElem.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object)
                {
                    if (otd.TryGetProperty("image_tokens", out var oit)) actualImageOutputTokens = oit.GetInt32();
                }
            }

                onProgress?.Invoke(100);
                return new ImageGenerationResult
                {
                    Images = results,
                    GenerateSeconds = generateSeconds,
                    DownloadSeconds = downloadSeconds,
                    RequestUrl = requestUrl,
                    AttemptedUrls = attemptedUrls,
                    ResponseId = responseId,
                    ActualInputTokens = actualInputTokens,
                    ActualOutputTokens = actualOutputTokens,
                    ActualImageInputTokens = actualImageInputTokens,
                    ActualImageOutputTokens = actualImageOutputTokens,
                    ActualCachedTokens = actualCachedTokens
                };
            }
        }

        public async Task<ImageRouteProbeResult> ProbeGenerateRouteAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            ImageApiRouteMode routeMode,
            CancellationToken ct)
        {
            var label = routeMode switch
            {
                ImageApiRouteMode.V1Images => "/v1/images/*",
                ImageApiRouteMode.ImagesRaw => "/images/*",
                _ => routeMode.ToString()
            };

            return await ProbeGenerateCandidateUrlsAsync(
                config,
                prompt,
                genConfig,
                label,
                BuildImageGenerateCandidateUrlsForRoute(config, routeMode),
                ct);
        }

        public async Task<ImageRouteProbeResult> ProbeGenerateCandidateUrlsAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string routeLabel,
            IReadOnlyList<string> candidateUrls,
            CancellationToken ct)
        {
            var modelCaps = App.Services?.GetService<ImageModelCatalogService>()?.GetCapabilities(genConfig.ImageModel);
            var effectiveSize = ResolveEffectiveSize(genConfig.ImageSize, modelCaps);

            var bodyObj = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["model"] = genConfig.ImageModel,
                ["size"] = effectiveSize,
                ["quality"] = genConfig.ImageQuality,
                ["output_format"] = genConfig.ImageFormat
            };
            if (genConfig.ImageCount > 1)
                bodyObj["n"] = genConfig.ImageCount;
            if (!string.IsNullOrWhiteSpace(genConfig.ImageBackground) && genConfig.ImageBackground != "auto")
                bodyObj["background"] = genConfig.ImageBackground;

            var authModeText = DescribeMediaAuthStrategy(config);
            var jsonBody = JsonSerializer.Serialize(bodyObj);
            var attempt = await SendImageGenerateRequestAsync(config, jsonBody, authModeText, candidateUrls, ct);

            using var response = attempt.Response;

            if (!response.IsSuccessStatusCode)
            {
                var errorText = attempt.ErrorText ?? await response.Content.ReadAsStringAsync(ct);
                return new ImageRouteProbeResult
                {
                    RouteLabel = routeLabel,
                    AttemptedUrls = candidateUrls.ToList(),
                    IsSuccess = false,
                    SuccessfulUrl = null,
                    ImageCount = 0,
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    ErrorText = errorText
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var imageCount = CountImageResults(json);

            return new ImageRouteProbeResult
            {
                RouteLabel = routeLabel,
                AttemptedUrls = candidateUrls.ToList(),
                IsSuccess = imageCount > 0,
                SuccessfulUrl = attempt.Url,
                ImageCount = imageCount,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                ErrorText = imageCount > 0 ? null : "接口返回成功，但响应中没有可用图片数据。"
            };
        }

        public IReadOnlyList<string> GetApimDeploymentGenerateCandidateUrls(AiConfig config)
            => BuildApimDeploymentImageGenerateCandidateUrls(config);

        public IReadOnlyList<string> GetGenerateCandidateUrlsForRoute(AiConfig config, ImageApiRouteMode routeMode)
            => BuildImageGenerateCandidateUrlsForRoute(config, routeMode);

        private async Task<ImageAttemptResult> SendImageGenerateRequestAsync(
            AiConfig config,
            Dictionary<string, object> bodyObj,
            string authModeText,
            CancellationToken ct)
        {
            var jsonBody = JsonSerializer.Serialize(bodyObj);
            return await SendImageGenerateRequestAsync(
                config,
                jsonBody,
                authModeText,
                BuildImageGenerateCandidateUrls(config),
                ct);
        }

        private async Task<ImageAttemptResult> SendImageGenerateRequestAsync(
            AiConfig config,
            string jsonBody,
            string authModeText,
            IReadOnlyList<string> candidateUrls,
            CancellationToken ct)
        {
            ImageAttemptResult? lastAttempt = null;
            var attemptedUrls = new List<string>();

            foreach (var url in candidateUrls)
            {
                attemptedUrls.Add(url);
                var response = await SendJsonImageRequestAsync(config, url, jsonBody, authModeText, ct, "ImageGenerate");

                if (response.IsSuccessStatusCode)
                {
                    return new ImageAttemptResult { Response = response, Url = url, AttemptedUrls = attemptedUrls.ToList() };
                }

                var errorText = await response.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, response, errorText))
                {
                    response.Dispose();

                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                    attemptedUrls.Add(queryUrl);
                    response = await SendJsonImageRequestAsync(config, queryUrl, jsonBody, authModeText, ct, "ImageGenerate-ApimQueryRetry");

                    if (response.IsSuccessStatusCode)
                    {
                        return new ImageAttemptResult { Response = response, Url = queryUrl, AttemptedUrls = attemptedUrls.ToList() };
                    }

                    errorText = await response.Content.ReadAsStringAsync(ct);
                    lastAttempt = new ImageAttemptResult { Response = response, Url = queryUrl, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                    if (ShouldTryNextImageCandidate(response))
                    {
                        continue;
                    }

                    return lastAttempt;
                }

                lastAttempt = new ImageAttemptResult { Response = response, Url = url, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                if (ShouldTryNextImageCandidate(response))
                    continue;

                return lastAttempt;
            }

            return lastAttempt ?? throw new HttpRequestException("图片生成失败: 未发送请求");
        }

        private static int CountImageResults(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataArray)
                || dataArray.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            return dataArray.GetArrayLength();
        }

        private async Task<ImageAttemptResult> SendImageEditRequestAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            IReadOnlyList<string> validReferenceImages,
            string authModeText,
            IReadOnlyList<string> imageNames,
            CancellationToken ct,
            ImageModelCapabilities? modelCaps = null)
        {
            ImageAttemptResult? lastAttempt = null;
            var attemptedUrls = new List<string>();

            foreach (var url in BuildImageEditCandidateUrls(config))
            {
                attemptedUrls.Add(url);
                var response = await SendEditImageRequestAsync(
                    config,
                    url,
                    prompt,
                    genConfig,
                    validReferenceImages,
                    authModeText,
                    imageNames,
                    ct,
                    "ImageEdit",
                    modelCaps);

                if (response.IsSuccessStatusCode)
                {
                    return new ImageAttemptResult { Response = response, Url = url, AttemptedUrls = attemptedUrls.ToList() };
                }

                var errorText = await response.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, response, errorText))
                {
                    response.Dispose();

                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                    attemptedUrls.Add(queryUrl);
                    response = await SendEditImageRequestAsync(
                        config,
                        queryUrl,
                        prompt,
                        genConfig,
                        validReferenceImages,
                        authModeText,
                        imageNames,
                        ct,
                        "ImageEdit-ApimQueryRetry",
                        modelCaps);

                    if (response.IsSuccessStatusCode)
                    {
                        return new ImageAttemptResult { Response = response, Url = queryUrl, AttemptedUrls = attemptedUrls.ToList() };
                    }

                    errorText = await response.Content.ReadAsStringAsync(ct);
                    lastAttempt = new ImageAttemptResult { Response = response, Url = queryUrl, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                    if (ShouldTryNextImageCandidate(response))
                    {
                        continue;
                    }

                    return lastAttempt;
                }

                lastAttempt = new ImageAttemptResult { Response = response, Url = url, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                if (ShouldTryNextImageCandidate(response))
                    continue;

                return lastAttempt;
            }

            return lastAttempt ?? throw new HttpRequestException("图片编辑失败: 未发送请求");
        }

        private async Task<HttpResponseMessage> SendJsonImageRequestAsync(
            AiConfig config,
            string url,
            string jsonBody,
            string authModeText,
            CancellationToken ct,
            string logPrefix,
            IReadOnlyDictionary<string, string>? additionalHeaders = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            await SetAuthHeadersAsync(request, config, ct);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.ExpectContinue = false;

            if (additionalHeaders is not null)
            {
                foreach (var (key, value) in additionalHeaders)
                    request.Headers.TryAddWithoutValidation(key, value);
            }

            await AppLogService.Instance.LogHttpDebugAsync(
                "image",
                $"{logPrefix}.Request",
                $"URL={url}\n" +
                $"AuthMode={authModeText}\n" +
                $"Json={jsonBody}",
                ct);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            await AppLogService.Instance.LogHttpDebugAsync(
                "image",
                $"{logPrefix}.Response",
                $"URL={url}\n" +
                $"HTTP={(int)response.StatusCode} {response.ReasonPhrase}\n" +
                $"Headers:\n{FormatResponseHeaders(response)}",
                ct);

            return response;
        }

        private async Task<HttpResponseMessage> SendEditImageRequestAsync(
            AiConfig config,
            string url,
            string prompt,
            MediaGenConfig genConfig,
            IReadOnlyList<string> validReferenceImages,
            string authModeText,
            IReadOnlyList<string> imageNames,
            CancellationToken ct,
            string logPrefix,
            ImageModelCapabilities? modelCaps = null)
        {
            using var formContent = new MultipartFormDataContent();
            foreach (var imagePath in validReferenceImages)
            {
                var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
                var ext = Path.GetExtension(imagePath).ToLowerInvariant();
                var mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/png"
                };
                var fileName = Path.GetFileName(imagePath);

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                formContent.Add(imageContent, "image", fileName);
            }

            formContent.Add(new StringContent(prompt), "prompt");
            formContent.Add(new StringContent(genConfig.ImageModel), "model");

            // size — 按模型能力过滤（与 generations 路径一致）
            var effectiveSize = ResolveEffectiveSize(genConfig.ImageSize, modelCaps);
            if (effectiveSize != "auto")
                formContent.Add(new StringContent(effectiveSize), "size");

            if (!string.IsNullOrWhiteSpace(genConfig.ImageQuality))
                formContent.Add(new StringContent(genConfig.ImageQuality), "quality");

            // background — 仅在模型声明支持透明时传 transparent
            var bg = genConfig.ImageBackground;
            if (!string.IsNullOrWhiteSpace(bg) && bg != "auto")
            {
                if (bg == "transparent" && modelCaps?.SupportsTransparentBackground == true)
                    formContent.Add(new StringContent("transparent"), "background");
                else if (bg == "opaque")
                    formContent.Add(new StringContent("opaque"), "background");
            }

            // input_fidelity — 仅在模型声明支持时传递
            var fidelity = genConfig.InputFidelity;
            if (!string.IsNullOrWhiteSpace(fidelity) && fidelity != "auto" && modelCaps?.SupportsInputFidelity == true)
                formContent.Add(new StringContent(fidelity), "input_fidelity");

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = formContent;
            await SetAuthHeadersAsync(request, config, ct);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.ExpectContinue = false;

            await AppLogService.Instance.LogHttpDebugAsync(
                "image",
                $"{logPrefix}.Request",
                $"URL={url}\n" +
                $"AuthMode={authModeText}\n" +
                $"FormFields=prompt,model,image\n" +
                $"ImageCount={validReferenceImages.Count}\n" +
                $"ImageFiles={string.Join(",", imageNames)}",
                ct);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            await AppLogService.Instance.LogHttpDebugAsync(
                "image",
                $"{logPrefix}.Response",
                $"URL={url}\n" +
                $"HTTP={(int)response.StatusCode} {response.ReasonPhrase}\n" +
                $"Headers:\n{FormatResponseHeaders(response)}",
                ct);

            return response;
        }

        private static bool ShouldTryNextImageCandidate(HttpResponseMessage response)
            => (int)response.StatusCode is 404 or 405;

        // ── V2: 通过 Responses API（/responses 端点 + image_generation 工具）编辑图片 ──

        /// <summary>
        /// 上传文件到 /openai/v1/files，返回 file_id。
        /// 固定使用 purpose=assistants（当前 APIM 下唯一可用值）。
        /// </summary>
        /// <summary>
        /// 上传图片文件到 /openai/v1/files（purpose=assistants），返回 file_id。
        /// 可用于 vision 输入或图片编辑的参考图上传。
        /// </summary>
        public async Task<string> UploadImageFileAsync(
            AiConfig config,
            string filePath,
            CancellationToken ct)
        {
            return await UploadFileAsync(config, filePath, config.AzureAuthMode.ToString(), ct);
        }

        private async Task<string> UploadFileAsync(
            AiConfig config,
            string filePath,
            string authModeText,
            CancellationToken ct)
        {
            // FileIdCache: 命中缓存则跳过上传
            var endpointUrl = config.ApiEndpoint ?? "";
            var cachedFileId = _fileIdCache.TryGet(endpointUrl, filePath);
            if (cachedFileId != null)
            {
                await AppLogService.Instance.LogHttpDebugAsync(
                    "image", "FileUpload.CacheHit", $"FileId={cachedFileId}\nFile={filePath}", ct);
                return cachedFileId;
            }

            var uploadUrls = BuildFileUploadCandidateUrls(config);
            if (uploadUrls.Count == 0)
                throw new HttpRequestException("文件上传失败: 无法构建 Files API URL，请检查终结点配置");

            var imageBytes = await File.ReadAllBytesAsync(filePath, ct);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mimeType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/png"
            };
            var fileName = Path.GetFileName(filePath);

            foreach (var url in uploadUrls)
            {
                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(imageBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                form.Add(fileContent, "file", fileName);
                form.Add(new StringContent("assistants"), "purpose");

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = form;
                await SetAuthHeadersAsync(request, config, ct);

                await AppLogService.Instance.LogHttpDebugAsync(
                    "image", "FileUpload.Request",
                    $"URL={url}\nAuthMode={authModeText}\nFile={fileName}", ct);

                using var response = await _httpClient.SendAsync(request, ct);

                await AppLogService.Instance.LogHttpDebugAsync(
                    "image", "FileUpload.Response",
                    $"URL={url}\nHTTP={(int)response.StatusCode} {response.ReasonPhrase}", ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(ct);
                    if (ShouldTryNextImageCandidate(response)) continue;
                    throw new HttpRequestException($"文件上传失败: {(int)response.StatusCode} {response.ReasonPhrase}. {errorText}");
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("id", out var idElem))
                {
                    var fileId = idElem.GetString();
                    if (!string.IsNullOrEmpty(fileId))
                    {
                        await AppLogService.Instance.LogHttpDebugAsync(
                            "image", "FileUpload.Success", $"FileId={fileId}", ct);
                        _fileIdCache.Set(endpointUrl, filePath, fileId);
                        return fileId;
                    }
                }

                throw new HttpRequestException($"文件上传成功但未返回 file_id: {body}");
            }

            throw new HttpRequestException("文件上传失败: 所有候选 URL 均失败");
        }

        /// <summary>
        /// V2 编辑：先上传参考图到 /openai/v1/files 拿 file_id，
        /// 再通过 Responses API + file_id 引用 + image_generation 工具编辑图片。
        /// </summary>
        private async Task<ImageAttemptResult> SendImageEditV2RequestAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            IReadOnlyList<string> validReferenceImages,
            string authModeText,
            IReadOnlyList<string> imageNames,
            CancellationToken ct,
            ImageModelCapabilities? modelCaps = null)
        {
            // 1) 构建 Responses API URL 候选
            var responsesUrls = BuildImageResponsesCandidateUrls(config);
            if (responsesUrls.Count == 0)
                throw new HttpRequestException("图片编辑(V2-ResponsesApi)失败: 无法构建 Responses API URL，请检查终结点配置");

            // 2) 上传参考图，收集 file_id
            var fileIds = new List<string>();
            foreach (var imagePath in validReferenceImages)
            {
                var fileId = await UploadFileAsync(config, imagePath, authModeText, ct);
                fileIds.Add(fileId);
            }

            // 3) 构建 input content：文本提示 + 参考图（file_id 引用）
            var contentItems = new List<object>();
            contentItems.Add(new Dictionary<string, object>
            {
                ["type"] = "input_text",
                ["text"] = prompt
            });

            foreach (var fileId in fileIds)
            {
                contentItems.Add(new Dictionary<string, object>
                {
                    ["type"] = "input_image",
                    ["file_id"] = fileId
                });
            }

            // 4) 构建 Responses API JSON body
            //    model 必须是文本模型（如 gpt-4o / gpt-5.4），图片模型通过 HTTP 头传递
            //    优先使用 genConfig.TextModelForResponses（由 ViewModel 从聊天模型注入）
            var textModel = !string.IsNullOrWhiteSpace(genConfig.TextModelForResponses)
                ? genConfig.TextModelForResponses
                : config.ModelName;
            var bodyObj = new Dictionary<string, object>
            {
                ["model"] = textModel,
                ["input"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = contentItems
                    }
                },
                ["tools"] = new[]
                {
                    BuildImageGenerationToolDefinition(genConfig, hasReferenceInput: true, modelCaps: modelCaps)
                }
            };

            // 多图：通过 instructions 引导精确数量（与纯文生图一致）
            if (genConfig.ImageCount > 1)
            {
                bodyObj["instructions"] = $"When generating images, always produce EXACTLY {genConfig.ImageCount} images. Each image should be a distinct variation.";
            }

            // 多轮改图：传递 previous_response_id
            if (!string.IsNullOrWhiteSpace(genConfig.PreviousResponseId))
                bodyObj["previous_response_id"] = genConfig.PreviousResponseId;

            var jsonBody = JsonSerializer.Serialize(bodyObj);

            // Azure OpenAI 需通过 x-ms-oai-image-generation-deployment 头指定图片模型部署名
            var responsesHeaders = new Dictionary<string, string>
            {
                ["x-ms-oai-image-generation-deployment"] = genConfig.ImageModel
            };

            await AppLogService.Instance.LogHttpDebugAsync(
                "image",
                "ImageEditV2-ResponsesApi.Build",
                $"TextModel={textModel}\n" +
                $"ImageDeployment={genConfig.ImageModel}\n" +
                $"ReferenceImageCount={validReferenceImages.Count}\n" +
                $"FileIds={string.Join(",", fileIds)}\n" +
                $"ImageFiles={string.Join(",", imageNames)}\n" +
                $"PreviousResponseId={genConfig.PreviousResponseId ?? "(none)"}",
                ct);

            // 5) 遍历 Responses API URL candidates 发送请求
            ImageAttemptResult? lastAttempt = null;
            var attemptedUrls = new List<string>();

            foreach (var url in responsesUrls)
            {
                attemptedUrls.Add(url);
                var response = await SendJsonImageRequestAsync(
                    config, url, jsonBody, authModeText, ct, "ImageEditV2-ResponsesApi", responsesHeaders);

                if (response.IsSuccessStatusCode)
                {
                    return new ImageAttemptResult { Response = response, Url = url, AttemptedUrls = attemptedUrls.ToList() };
                }

                var errorText = await response.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, response, errorText))
                {
                    response.Dispose();
                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                    attemptedUrls.Add(queryUrl);
                    response = await SendJsonImageRequestAsync(
                        config, queryUrl, jsonBody, authModeText, ct, "ImageEditV2-ResponsesApi-ApimQueryRetry", responsesHeaders);

                    if (response.IsSuccessStatusCode)
                    {
                        return new ImageAttemptResult { Response = response, Url = queryUrl, AttemptedUrls = attemptedUrls.ToList() };
                    }

                    errorText = await response.Content.ReadAsStringAsync(ct);
                    lastAttempt = new ImageAttemptResult { Response = response, Url = queryUrl, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                    if (ShouldTryNextImageCandidate(response))
                        continue;

                    return lastAttempt;
                }

                lastAttempt = new ImageAttemptResult { Response = response, Url = url, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                if (ShouldTryNextImageCandidate(response))
                    continue;

                return lastAttempt;
            }

            return lastAttempt ?? throw new HttpRequestException("图片编辑(V2-ResponsesApi)失败: 未发送请求");
        }

        /// <summary>
        /// 生成图片并保存到指定目录，返回文件路径列表及耗时信息
        /// </summary>
        public async Task<ImageSaveResult> GenerateAndSaveImagesAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string outputDirectory,
            CancellationToken ct,
            IReadOnlyList<string>? referenceImagePaths = null,
            Action<int>? onProgress = null)
        {
            var genResult = await GenerateImagesAsync(config, prompt, genConfig, ct, referenceImagePaths, onProgress);
            Directory.CreateDirectory(outputDirectory);

            var filePaths = new List<string>();
            var seq = 1;
            foreach (var data in genResult.Images)
            {
                var randomId = Guid.NewGuid().ToString("N")[..8];
                var ext = genConfig.ImageFormat;
                var fileName = $"img_{seq:D3}_{randomId}.{ext}";
                var filePath = Path.Combine(outputDirectory, fileName);
                var tmpPath = filePath + ".tmp";

                try
                {
                    await File.WriteAllBytesAsync(tmpPath, data, ct);
                    File.Move(tmpPath, filePath, overwrite: true);
                }
                finally
                {
                    // 异常时清理 .tmp 残留
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }

                filePaths.Add(filePath);
                seq++;
            }

            return new ImageSaveResult
            {
                FilePaths = filePaths,
                GenerateSeconds = genResult.GenerateSeconds,
                DownloadSeconds = genResult.DownloadSeconds,
                ResponseId = genResult.ResponseId,
                ActualWidth = ReadImageWidth(genResult.Images.FirstOrDefault()),
                ActualHeight = ReadImageHeight(genResult.Images.FirstOrDefault()),
                ActualInputTokens = genResult.ActualInputTokens,
                ActualOutputTokens = genResult.ActualOutputTokens,
                ActualImageInputTokens = genResult.ActualImageInputTokens,
                ActualImageOutputTokens = genResult.ActualImageOutputTokens,
                ActualCachedTokens = genResult.ActualCachedTokens
            };
        }

        private static int? ReadImageWidth(byte[]? data)
        {
            if (data == null || data.Length < 24) return null;
            try
            {
                using var ms = new MemoryStream(data);
                using var codec = SkiaSharp.SKCodec.Create(ms);
                return codec?.Info.Width;
            }
            catch { return null; }
        }

        private static int? ReadImageHeight(byte[]? data)
        {
            if (data == null || data.Length < 24) return null;
            try
            {
                using var ms = new MemoryStream(data);
                using var codec = SkiaSharp.SKCodec.Create(ms);
                return codec?.Info.Height;
            }
            catch { return null; }
        }

        /// <summary>
        /// 纯文生图走 Responses API（§16 统一路线）。
        /// 无参考图时 content 只有 input_text，多图通过 instructions 引导。
        /// </summary>
        private async Task<ImageAttemptResult> SendPureTextGenV2RequestAsync(
            AiConfig config,
            string prompt,
            MediaGenConfig genConfig,
            string authModeText,
            CancellationToken ct,
            ImageModelCapabilities? modelCaps = null)
        {
            var responsesUrls = BuildImageResponsesCandidateUrls(config);
            if (responsesUrls.Count == 0)
                throw new HttpRequestException("纯文生图(Responses API)失败: 无法构建 URL");

            var contentItems = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "input_text",
                    ["text"] = prompt
                }
            };

            var textModel = !string.IsNullOrWhiteSpace(genConfig.TextModelForResponses)
                ? genConfig.TextModelForResponses
                : config.ModelName;

            var bodyObj = new Dictionary<string, object>
            {
                ["model"] = textModel,
                ["input"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = contentItems
                    }
                },
                ["tools"] = new[]
                {
                    BuildImageGenerationToolDefinition(genConfig, modelCaps: modelCaps)
                }
            };

            // 多图：通过 instructions（developer 角色）引导精确数量
            if (genConfig.ImageCount > 1)
            {
                bodyObj["instructions"] = $"When generating images, always produce EXACTLY {genConfig.ImageCount} images. Each image should be a distinct variation.";
            }

            // 多轮改图：传递 previous_response_id
            if (!string.IsNullOrWhiteSpace(genConfig.PreviousResponseId))
                bodyObj["previous_response_id"] = genConfig.PreviousResponseId;

            var jsonBody = JsonSerializer.Serialize(bodyObj);

            var responsesHeaders = new Dictionary<string, string>
            {
                ["x-ms-oai-image-generation-deployment"] = genConfig.ImageModel
            };

            await AppLogService.Instance.LogHttpDebugAsync(
                "image", "PureTextGenV2-ResponsesApi.Build",
                $"TextModel={textModel}\nImageDeployment={genConfig.ImageModel}\nImageCount={genConfig.ImageCount}", ct);

            ImageAttemptResult? lastAttempt = null;
            var attemptedUrls = new List<string>();

            foreach (var url in responsesUrls)
            {
                attemptedUrls.Add(url);
                var response = await SendJsonImageRequestAsync(
                    config, url, jsonBody, authModeText, ct, "PureTextGenV2-ResponsesApi", responsesHeaders);

                if (response.IsSuccessStatusCode)
                    return new ImageAttemptResult { Response = response, Url = url, AttemptedUrls = attemptedUrls.ToList() };

                var errorText = await response.Content.ReadAsStringAsync(ct);
                lastAttempt = new ImageAttemptResult { Response = response, Url = url, ErrorText = errorText, AttemptedUrls = attemptedUrls.ToList() };

                if (ShouldTryNextImageCandidate(response))
                    continue;

                return lastAttempt;
            }

            return lastAttempt ?? throw new HttpRequestException("纯文生图(Responses API)失败: 未发送请求");
        }

        /// <summary>
        /// 构建 image_generation tool 定义。
        /// 支持编程化参数：quality, size, background, output_format。
        /// Azure APIM size 仅允许 1024x1024/1024x1536/1536x1024/auto 四种固定值。
        /// </summary>
        internal static Dictionary<string, object> BuildImageGenerationToolDefinition(
            MediaGenConfig genConfig, bool hasReferenceInput = false, ImageModelCapabilities? modelCaps = null)
        {
            var tool = new Dictionary<string, object> { ["type"] = "image_generation" };

            // quality
            if (!string.IsNullOrWhiteSpace(genConfig.ImageQuality) && genConfig.ImageQuality != "auto")
                tool["quality"] = genConfig.ImageQuality;

            // size — 按模型能力分级处理
            var size = genConfig.ImageSize;
            if (!string.IsNullOrWhiteSpace(size) && size != "auto")
            {
                if (modelCaps?.ResolutionMode == ResolutionMode.FreeForm)
                {
                    // gpt-image-2 类模型：支持灵活尺寸，直接传给 API
                    tool["size"] = size;
                }
                else if (modelCaps?.ResolutionMode == ResolutionMode.Fixed)
                {
                    // gpt-image-1.5 类模型：仅允许 FixedSizes 中声明的值
                    if (modelCaps.FixedSizes.Contains(size))
                        tool["size"] = size;
                }
                else
                {
                    // 无能力声明时保守处理：仅传标准安全值
                    if (size is "1024x1024" or "1024x1536" or "1536x1024")
                        tool["size"] = size;
                }
            }

            // background — transparent 需配合 png 格式
            var bg = genConfig.ImageBackground;
            if (!string.IsNullOrWhiteSpace(bg) && bg != "auto")
            {
                // transparent 仅在 png 格式下有效
                if (bg == "transparent" && genConfig.ImageFormat is "png" or "auto" or null or "")
                    tool["background"] = "transparent";
                else if (bg == "opaque")
                    tool["background"] = "opaque";
            }

            // output_format
            if (!string.IsNullOrWhiteSpace(genConfig.ImageFormat) && genConfig.ImageFormat != "auto")
                tool["output_format"] = genConfig.ImageFormat;

            // input_fidelity — gpt-image-1.5 可调（low/high），gpt-image-2 不支持
            var fidelity = genConfig.InputFidelity;
            if (!string.IsNullOrWhiteSpace(fidelity) && fidelity != "auto")
                tool["input_fidelity"] = fidelity;

            // action — 有参考图时为 edit，否则为 generate
            if (hasReferenceInput)
                tool["action"] = "edit";

            return tool;
        }

        /// <summary>
        /// 自动路由判定：TextModelForResponses 可用且 ImageEditMode 未强制 V1 时走 Responses API。
        /// </summary>
        private static bool ShouldUseResponsesApi(MediaGenConfig genConfig)
        {
            // 用户显式选 V1 时尊重选择（保留手动覆盖能力）
            if (genConfig.ImageEditMode == ImageEditMode.V1Multipart)
                return false;
            // TextModelForResponses 可用即自动启用 Responses API
            return !string.IsNullOrWhiteSpace(genConfig.TextModelForResponses);
        }

        /// <summary>
        /// 按模型能力解析有效 size：FreeForm 传任意值，Fixed 仅传声明值，否则回退 auto。
        /// </summary>
        internal static string ResolveEffectiveSize(string? requestedSize, ImageModelCapabilities? modelCaps)
        {
            if (string.IsNullOrWhiteSpace(requestedSize) || requestedSize == "auto")
                return "auto";

            if (modelCaps == null)
                return requestedSize; // 无能力声明，原样传（API 自行校验）

            if (modelCaps.ResolutionMode == Models.ResolutionMode.FreeForm)
                return requestedSize; // 自由画布，任意尺寸

            if (modelCaps.ResolutionMode == Models.ResolutionMode.Fixed)
                return modelCaps.FixedSizes.Contains(requestedSize) ? requestedSize : "auto";

            return requestedSize;
        }

        private static string? BuildImageDataUrl(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return null;

            var bytes = File.ReadAllBytes(imagePath);
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => "image/png"
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        // ── Pipeline 步骤用公开方法 ──

        public IReadOnlyList<string> GetResponsesApiCandidateUrls(AiConfig config)
            => BuildImageResponsesCandidateUrls(config);

        public async Task<HttpResponseMessage> SendAuthenticatedJsonAsync(
            AiConfig config,
            string url,
            string jsonBody,
            IReadOnlyDictionary<string, string>? additionalHeaders,
            CancellationToken ct)
        {
            return await SendJsonImageRequestAsync(config, url, jsonBody, "Pipeline", ct, "Pipeline", additionalHeaders);
        }

        /// <summary>
        /// 使用 Pipeline 执行完整的图片生成流程：Route → Upload → BuildRequest → Execute → Land。
        /// 此方法目前作为备选入口，与现有 GenerateAndSaveImagesAsync 并行存在。
        /// </summary>
        public async Task<ImagePipeline.ImagePipelineContext> RunPipelineAsync(
            ImagePipeline.ImageTaskRequest taskRequest,
            AiConfig config,
            MediaGenConfig genConfig,
            ImageModelCapabilities? modelCaps,
            CancellationToken ct)
        {
            var ctx = new ImagePipeline.ImagePipelineContext
            {
                Request = taskRequest,
                Config = config,
                GenConfig = genConfig,
                ModelCaps = modelCaps,
            };

            var runner = new ImagePipeline.ImagePipelineRunner()
                .AddStep(new ImagePipeline.Steps.RouteStep())
                .AddStep(new ImagePipeline.Steps.UploadStep(this, _fileIdCache))
                .AddStep(new ImagePipeline.Steps.BuildRequestStep())
                .AddStep(new ImagePipeline.Steps.ExecuteStep(this))
                .AddStep(new ImagePipeline.Steps.LandStep());

            return await runner.RunAsync(ctx, ct);
        }

    }
}
