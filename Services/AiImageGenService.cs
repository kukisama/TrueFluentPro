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
    }

    /// <summary>
    /// 图片生成服务（OpenAI Compatible Images API）
    /// </summary>
    public class AiImageGenService : AiMediaServiceBase
    {
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

        private sealed class ImageAttemptResult
        {
            public required HttpResponseMessage Response { get; init; }
            public required string Url { get; init; }
            public string? ErrorText { get; init; }
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
                // ── 有参考图：优先尝试 /images/edits + multipart/form-data（Azure OpenAI 官方方式） ──
                var authModeText = DescribeMediaAuthStrategy(config);
                var imageNames = validReferenceImages
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToList();
                var editAttempt = await SendImageEditRequestAsync(config, prompt, genConfig, validReferenceImages, authModeText, imageNames, ct);
                response = editAttempt.Response;

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
                // ── 无参考图：使用 /images/generations 终结点 + JSON ──
                var bodyObj = new Dictionary<string, object>
                {
                    ["prompt"] = prompt,
                    ["model"] = genConfig.ImageModel,
                    ["n"] = genConfig.ImageCount,
                    ["size"] = genConfig.ImageSize,
                    ["quality"] = genConfig.ImageQuality,
                    ["output_format"] = genConfig.ImageFormat
                };
                var authModeText = DescribeMediaAuthStrategy(config);
                var generateAttempt = await SendImageGenerateRequestAsync(config, bodyObj, authModeText, ct);
                response = generateAttempt.Response;

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

                onProgress?.Invoke(100);
                return new ImageGenerationResult
                {
                    Images = results,
                    GenerateSeconds = generateSeconds,
                    DownloadSeconds = downloadSeconds
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
            var bodyObj = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["model"] = genConfig.ImageModel,
                ["n"] = genConfig.ImageCount,
                ["size"] = genConfig.ImageSize,
                ["quality"] = genConfig.ImageQuality,
                ["output_format"] = genConfig.ImageFormat
            };

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

            foreach (var url in candidateUrls)
            {
                var response = await SendJsonImageRequestAsync(config, url, jsonBody, authModeText, ct, "ImageGenerate");

                if (response.IsSuccessStatusCode)
                {
                    return new ImageAttemptResult { Response = response, Url = url };
                }

                var errorText = await response.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, response, errorText))
                {
                    response.Dispose();

                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                    response = await SendJsonImageRequestAsync(config, queryUrl, jsonBody, authModeText, ct, "ImageGenerate-ApimQueryRetry");

                    if (response.IsSuccessStatusCode)
                    {
                        return new ImageAttemptResult { Response = response, Url = queryUrl };
                    }

                    errorText = await response.Content.ReadAsStringAsync(ct);
                    lastAttempt = new ImageAttemptResult { Response = response, Url = queryUrl, ErrorText = errorText };

                    if (ShouldTryNextImageCandidate(response))
                    {
                        continue;
                    }

                    return lastAttempt;
                }

                lastAttempt = new ImageAttemptResult { Response = response, Url = url, ErrorText = errorText };

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
            CancellationToken ct)
        {
            ImageAttemptResult? lastAttempt = null;

            foreach (var url in BuildImageEditCandidateUrls(config))
            {
                var response = await SendEditImageRequestAsync(
                    config,
                    url,
                    prompt,
                    genConfig,
                    validReferenceImages,
                    authModeText,
                    imageNames,
                    ct,
                    "ImageEdit");

                if (response.IsSuccessStatusCode)
                {
                    return new ImageAttemptResult { Response = response, Url = url };
                }

                var errorText = await response.Content.ReadAsStringAsync(ct);

                if (IsMissingApimSubscriptionKeyResponse(config, response, errorText))
                {
                    response.Dispose();

                    var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, config.ApiKey);
                    response = await SendEditImageRequestAsync(
                        config,
                        queryUrl,
                        prompt,
                        genConfig,
                        validReferenceImages,
                        authModeText,
                        imageNames,
                        ct,
                        "ImageEdit-ApimQueryRetry");

                    if (response.IsSuccessStatusCode)
                    {
                        return new ImageAttemptResult { Response = response, Url = queryUrl };
                    }

                    errorText = await response.Content.ReadAsStringAsync(ct);
                    lastAttempt = new ImageAttemptResult { Response = response, Url = queryUrl, ErrorText = errorText };

                    if (ShouldTryNextImageCandidate(response))
                    {
                        continue;
                    }

                    return lastAttempt;
                }

                lastAttempt = new ImageAttemptResult { Response = response, Url = url, ErrorText = errorText };

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
            string logPrefix)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            await SetAuthHeadersAsync(request, config, ct);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.ExpectContinue = false;

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
            string logPrefix)
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

    /// <summary>
    /// 图片生成+保存结果（含文件路径和耗时）
    /// </summary>
    public class ImageSaveResult
    {
        public List<string> FilePaths { get; set; } = new();
        public double GenerateSeconds { get; set; }
        public double DownloadSeconds { get; set; }
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

                await File.WriteAllBytesAsync(filePath, data, ct);
                filePaths.Add(filePath);
                seq++;
            }

            return new ImageSaveResult
            {
                FilePaths = filePaths,
                GenerateSeconds = genResult.GenerateSeconds,
                DownloadSeconds = genResult.DownloadSeconds
            };
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

    }
}
