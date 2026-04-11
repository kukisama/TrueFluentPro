using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public static class SpeechBatchApiClient
    {
        private static readonly HttpClient HttpClient = new();

        public static async Task<(List<SubtitleCue> Cues, string TranscriptionJson)> BatchTranscribeSpeechToCuesAsync(
            Uri contentUrl,
            string locale,
            AzureSubscription subscription,
            CancellationToken token,
            Action<string> onStatus,
            BatchSubtitleSplitOptions splitOptions,
            Action<string, string>? onBatchLog = null)
        {
            var endpoint = subscription.GetBatchTranscriptionEndpoint();
            Action<HttpRequestMessage> setAuth = req =>
                req.Headers.Add("Ocp-Apim-Subscription-Key", subscription.SubscriptionKey);

            return await BatchTranscribeCoreAsync(contentUrl, locale, endpoint, setAuth, token, onStatus, splitOptions, onBatchLog);
        }

        /// <summary>AAD Bearer token 认证的批量转写入口</summary>
        public static async Task<(List<SubtitleCue> Cues, string TranscriptionJson)> BatchTranscribeSpeechToCuesAsync(
            Uri contentUrl,
            string locale,
            string batchEndpoint,
            Func<CancellationToken, Task<string>> getTokenAsync,
            CancellationToken token,
            Action<string> onStatus,
            BatchSubtitleSplitOptions splitOptions,
            Action<string, string>? onBatchLog = null)
        {
            Action<HttpRequestMessage> setAuth = req =>
            {
                // 每次请求前刷新令牌
                var tokenTask = getTokenAsync(token);
                tokenTask.Wait(token);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenTask.Result);
            };

            return await BatchTranscribeCoreAsync(contentUrl, locale, batchEndpoint, setAuth, token, onStatus, splitOptions, onBatchLog);
        }

        private static async Task<(List<SubtitleCue> Cues, string TranscriptionJson)> BatchTranscribeCoreAsync(
            Uri contentUrl,
            string locale,
            string endpoint,
            Action<HttpRequestMessage> setAuth,
            CancellationToken token,
            Action<string> onStatus,
            BatchSubtitleSplitOptions splitOptions,
            Action<string, string>? onBatchLog)
        {
            var requestBody = new
            {
                displayName = $"Batch-{DateTime.Now:yyyyMMdd_HHmmss}",
                locale = locale,
                contentUrls = new[] { contentUrl.ToString() },
                properties = new
                {
                    diarizationEnabled = true,
                    wordLevelTimestampsEnabled = true,
                    punctuationMode = "DictatedAndAutomatic",
                    profanityFilterMode = "Masked"
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            setAuth(request);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            using var response = await HttpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(token);
                throw new InvalidOperationException($"创建批量转写失败: {response.StatusCode} {detail}");
            }

            var statusUrl = response.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(statusUrl))
            {
                var body = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("self", out var selfElement))
                {
                    statusUrl = selfElement.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(statusUrl))
            {
                throw new InvalidOperationException("未获取到批量转写状态地址");
            }

            onBatchLog?.Invoke("TranscribeSubmit", $"url={statusUrl}");

            string? lastStatusJson = null;
            string? lastPolledStatus = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var statusRequest = new HttpRequestMessage(HttpMethod.Get, statusUrl);
                setAuth(statusRequest);

                using var statusResponse = await HttpClient.SendAsync(statusRequest, token);
                var statusBody = await statusResponse.Content.ReadAsStringAsync(token);
                lastStatusJson = statusBody;

                if (!statusResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"查询批量转写状态失败: {statusResponse.StatusCode} {statusBody}");
                }

                using var statusDoc = JsonDocument.Parse(statusBody);
                var status = statusDoc.RootElement.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : "";

                if (!string.Equals(status, lastPolledStatus, StringComparison.OrdinalIgnoreCase))
                {
                    onBatchLog?.Invoke("TranscribePoll", $"status={status}");
                    lastPolledStatus = status;
                }

                if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    onStatus("批量转写：已完成，整理字幕...");
                    break;
                }

                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    var errorSummary = BuildSpeechBatchFailureSummary(statusDoc) ?? "批量转写失败";
                    var ex = new InvalidOperationException(errorSummary);
                    ex.Data["SpeechBatchError"] = statusBody;
                    throw ex;
                }

                onStatus($"批量转写：{status}...");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }

            var filesUrl = statusUrl.TrimEnd('/') + "/files";
            if (!string.IsNullOrWhiteSpace(lastStatusJson))
            {
                using var statusDoc = JsonDocument.Parse(lastStatusJson);
                if (statusDoc.RootElement.TryGetProperty("links", out var linksElement) &&
                    linksElement.TryGetProperty("files", out var filesElement))
                {
                    filesUrl = filesElement.GetString() ?? filesUrl;
                }
            }

            using var filesRequest = new HttpRequestMessage(HttpMethod.Get, filesUrl);
            setAuth(filesRequest);

            using var filesResponse = await HttpClient.SendAsync(filesRequest, token);
            var filesBody = await filesResponse.Content.ReadAsStringAsync(token);
            if (!filesResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"获取批量转写文件列表失败: {filesResponse.StatusCode} {filesBody}");
            }

            var transcriptionUrl = ExtractTranscriptionContentUrl(filesBody);
            if (string.IsNullOrWhiteSpace(transcriptionUrl))
            {
                throw new InvalidOperationException("未找到批量转写结果文件");
            }

            var transcriptionJson = await HttpClient.GetStringAsync(transcriptionUrl, token);
            var cues = BatchTranscriptionParser.ParseBatchTranscriptionToCues(transcriptionJson, splitOptions);
            return (cues, transcriptionJson);
        }

        public static string? ExtractTranscriptionContentUrl(string filesJson)
        {
            using var doc = JsonDocument.Parse(filesJson);
            if (!doc.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in values.EnumerateArray())
            {
                var kind = item.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : "";
                if (!string.Equals(kind, "Transcription", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("links", out var linksElement) &&
                    linksElement.TryGetProperty("contentUrl", out var contentElement))
                {
                    return contentElement.GetString();
                }
            }

            return null;
        }

        public static string? BuildSpeechBatchFailureSummary(JsonDocument statusDoc)
        {
            if (!statusDoc.RootElement.TryGetProperty("error", out var errorElement))
            {
                return null;
            }

            var code = errorElement.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;
            var message = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            var detailMessages = new List<string>();
            if (errorElement.TryGetProperty("details", out var detailsElement) &&
                detailsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in detailsElement.EnumerateArray())
                {
                    var detailMessage = detail.TryGetProperty("message", out var detailMessageElement)
                        ? detailMessageElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(detailMessage))
                    {
                        detailMessages.Add(detailMessage);
                    }
                }
            }

            var summaryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(code))
            {
                summaryParts.Add(code);
            }
            if (!string.IsNullOrWhiteSpace(message))
            {
                summaryParts.Add(message);
            }
            if (detailMessages.Count > 0)
            {
                summaryParts.Add(string.Join("; ", detailMessages));
            }

            if (summaryParts.Count == 0)
            {
                return null;
            }

            return "批量转写失败: " + string.Join(" | ", summaryParts);
        }
    }
}
