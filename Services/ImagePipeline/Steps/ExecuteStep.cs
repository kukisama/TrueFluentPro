using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.ImagePipeline.Steps
{
    /// <summary>
    /// 发送 HTTP 请求并解析响应：提取 base64 图片、response_id、token 用量。
    /// 遍历 Responses API 候选 URL，容错降级。
    /// </summary>
    public sealed class ExecuteStep : IImagePipelineStep
    {
        private readonly IAiImageGenService _imageService;

        public ExecuteStep(IAiImageGenService imageService)
        {
            _imageService = imageService;
        }

        public string Name => "Execute";

        public async Task<bool> ExecuteAsync(ImagePipelineContext ctx, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ctx.RequestJsonBody))
            {
                ctx.ErrorCode = ImageErrorCodes.ApiError;
                ctx.ErrorMessage = "RequestJsonBody 为空，BuildRequestStep 未执行或失败";
                return false;
            }

            var candidateUrls = _imageService.GetResponsesApiCandidateUrls(ctx.Config);
            if (candidateUrls.Count == 0)
            {
                ctx.ErrorCode = ImageErrorCodes.ApiError;
                ctx.ErrorMessage = "无可用的 Responses API URL";
                return false;
            }

            var sw = Stopwatch.StartNew();
            string? lastErrorText = null;

            foreach (var url in candidateUrls)
            {
                ct.ThrowIfCancellationRequested();
                ctx.AttemptedUrls.Add(url);

                using var response = await _imageService.SendAuthenticatedJsonAsync(
                    ctx.Config, url, ctx.RequestJsonBody, ctx.RequestHeaders, ct);

                if (response.IsSuccessStatusCode)
                {
                    ctx.GenerateSeconds = sw.Elapsed.TotalSeconds;
                    var body = await response.Content.ReadAsStringAsync(ct);
                    ctx.DownloadSeconds = sw.Elapsed.TotalSeconds - ctx.GenerateSeconds;
                    ParseResponseBody(ctx, body);
                    return true;
                }

                lastErrorText = await response.Content.ReadAsStringAsync(ct);
                ctx.HttpStatus = (int)response.StatusCode;

                // 404/502/503 可重试下一个候选
                var code = (int)response.StatusCode;
                if (code is 404 or 502 or 503)
                    continue;

                // 其他错误不重试
                break;
            }

            ctx.ErrorCode = ImageErrorCodes.ApiError;
            ctx.ErrorMessage = lastErrorText ?? "所有候选 URL 均失败";
            return false;
        }

        private static void ParseResponseBody(ImagePipelineContext ctx, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 提取图片（Responses API 格式：output[] → image_generation_call → result）
            if (root.TryGetProperty("output", out var outputArray))
            {
                foreach (var item in outputArray.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeElem)
                        && typeElem.GetString() == "image_generation_call"
                        && item.TryGetProperty("result", out var resultElem))
                    {
                        var b64 = resultElem.GetString();
                        if (!string.IsNullOrEmpty(b64))
                            ctx.DecodedImages.Add(Convert.FromBase64String(b64));
                    }
                }
            }
            // 兼容 data[] 格式（/images/generations 返回格式）
            else if (root.TryGetProperty("data", out var dataArray))
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    if (item.TryGetProperty("b64_json", out var b64Elem))
                    {
                        var b64 = b64Elem.GetString();
                        if (!string.IsNullOrEmpty(b64))
                            ctx.DecodedImages.Add(Convert.FromBase64String(b64));
                    }
                }
            }

            // response id（多轮改图用）
            if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String)
                ctx.ResponseId = idElem.GetString();

            // token 用量
            if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
            {
                if (usageElem.TryGetProperty("input_tokens", out var it)) ctx.ActualInputTokens = it.GetInt32();
                if (usageElem.TryGetProperty("output_tokens", out var ot)) ctx.ActualOutputTokens = ot.GetInt32();
                if (usageElem.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object)
                {
                    if (itd.TryGetProperty("cached_tokens", out var ct2)) ctx.ActualCachedTokens = ct2.GetInt32();
                }
            }
        }
    }
}
