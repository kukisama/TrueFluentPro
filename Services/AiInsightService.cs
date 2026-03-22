using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services
{
    public sealed class AiChatRequestConfig
    {
        public string ProfileId { get; init; } = "";
        public EndpointApiType EndpointType { get; init; } = EndpointApiType.OpenAiCompatible;
        public AiProviderType ProviderType { get; init; } = AiProviderType.OpenAiCompatible;
        public string ApiEndpoint { get; init; } = "";
        public string ApiKey { get; init; } = "";
        public string ApiVersion { get; init; } = "2024-02-01";
        public AzureAuthMode AzureAuthMode { get; init; } = AzureAuthMode.ApiKey;
        public ApiKeyHeaderMode ApiKeyHeaderMode { get; init; } = ApiKeyHeaderMode.Auto;
        public TextApiProtocolMode TextApiProtocolMode { get; init; } = TextApiProtocolMode.Auto;
        public ImageApiRouteMode ImageApiRouteMode { get; init; } = ImageApiRouteMode.Auto;
        public string AzureTenantId { get; init; } = "";
        public string AzureClientId { get; init; } = "";
        public bool IsAzureEndpoint { get; init; }
        public string ModelName { get; init; } = "";
        public string DeploymentName { get; init; } = "";
        public bool SummaryEnableReasoning { get; init; }

        public bool IsValid => !string.IsNullOrWhiteSpace(ApiEndpoint)
                            && (AzureAuthMode == AzureAuthMode.AAD || !string.IsNullOrWhiteSpace(ApiKey))
                            && (IsAzureEndpoint
                                ? !string.IsNullOrWhiteSpace(DeploymentName)
                                : !string.IsNullOrWhiteSpace(ModelName));

        public static AiChatRequestConfig FromLegacyConfig(AiConfig config, AiChatProfile profile)
        {
            return new AiChatRequestConfig
            {
                ProfileId = config.ProfileId,
                EndpointType = config.EndpointType,
                ProviderType = config.ProviderType,
                ApiEndpoint = config.ApiEndpoint,
                ApiKey = config.ApiKey,
                ApiVersion = config.ApiVersion,
                AzureAuthMode = config.AzureAuthMode,
                ApiKeyHeaderMode = config.ApiKeyHeaderMode,
                TextApiProtocolMode = config.TextApiProtocolMode,
                ImageApiRouteMode = config.ImageApiRouteMode,
                AzureTenantId = config.AzureTenantId,
                AzureClientId = config.AzureClientId,
                IsAzureEndpoint = config.IsAzureEndpoint,
                ModelName = config.ModelName,
                DeploymentName = config.DeploymentName,
                SummaryEnableReasoning = config.SummaryEnableReasoning
            };
        }
    }

    public class AiRequestOutcome
    {
        public bool UsedReasoning { get; set; }
        public bool UsedFallback { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
    }

    public sealed class AiRequestTrace
    {
        public IReadOnlyList<string> AttemptedUrls { get; init; } = Array.Empty<string>();
        public string FinalUrl { get; init; } = "";
    }

    public class AiInsightService : IAiInsightService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        private readonly AzureTokenProvider? _tokenProvider;

        public AiInsightService(AzureTokenProvider? tokenProvider = null)
        {
            _tokenProvider = tokenProvider;
        }

        public Task StreamChatAsync(
            AiConfig config,
            string systemPrompt,
            string userContent,
            Action<string> onChunk,
            CancellationToken cancellationToken,
            AiChatProfile profile = AiChatProfile.Quick,
            bool enableReasoning = false,
            Action<AiRequestOutcome>? onOutcome = null,
            Action<string>? onReasoningChunk = null,
            Action<AiRequestTrace>? onTrace = null,
            IReadOnlyList<string>? urlCandidatesOverride = null,
            bool allowNextUrlRetry = true,
            bool allowApimSubscriptionKeyQueryRetry = true)
        {
            var request = AiChatRequestConfig.FromLegacyConfig(config, profile);
            return StreamChatAsync(
                request,
                systemPrompt,
                userContent,
                onChunk,
                cancellationToken,
                profile,
                enableReasoning,
                onOutcome,
                onReasoningChunk,
                onTrace,
                urlCandidatesOverride,
                allowNextUrlRetry,
                allowApimSubscriptionKeyQueryRetry);
        }

        public async Task StreamChatAsync(
            AiChatRequestConfig request,
            string systemPrompt,
            string userContent,
            Action<string> onChunk,
            CancellationToken cancellationToken,
            AiChatProfile profile = AiChatProfile.Quick,
            bool enableReasoning = false,
            Action<AiRequestOutcome>? onOutcome = null,
            Action<string>? onReasoningChunk = null,
            Action<AiRequestTrace>? onTrace = null,
            IReadOnlyList<string>? urlCandidatesOverride = null,
            bool allowNextUrlRetry = true,
            bool allowApimSubscriptionKeyQueryRetry = true)
        {
            var (response, trace) = await SendRequestAsync(
                request,
                systemPrompt,
                userContent,
                enableReasoning,
                cancellationToken,
                urlCandidatesOverride,
                allowNextUrlRetry,
                allowApimSubscriptionKeyQueryRetry);
            using (response)
            {
                onTrace?.Invoke(trace);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);

                    throw new HttpRequestException($"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {errorText}");
                }

                onOutcome?.Invoke(new AiRequestOutcome
                {
                    UsedReasoning = enableReasoning
                                    && profile == AiChatProfile.Summary,
                    UsedFallback = false
                });

                var usage = await ConsumeResponseAsync(request, response, onChunk, onReasoningChunk, cancellationToken);

                // 回填 Token 用量到 outcome
                if (usage.HasValue)
                {
                    onOutcome?.Invoke(new AiRequestOutcome
                    {
                        UsedReasoning = enableReasoning && profile == AiChatProfile.Summary,
                        UsedFallback = false,
                        PromptTokens = usage.Value.PromptTokens,
                        CompletionTokens = usage.Value.CompletionTokens
                    });
                }
            }
        }

        private async Task<(HttpResponseMessage Response, AiRequestTrace Trace)> SendRequestAsync(
            AiChatRequestConfig request,
            string systemPrompt,
            string userContent,
            bool enableReasoning,
            CancellationToken cancellationToken,
            IReadOnlyList<string>? urlCandidatesOverride,
            bool allowNextUrlRetry,
            bool allowApimSubscriptionKeyQueryRetry)
        {
            var body = BuildRequestBody(request, systemPrompt, userContent, enableReasoning);
            var payloadJson = JsonSerializer.Serialize(body);
            HttpResponseMessage? lastResponse = null;
            var attemptedUrls = new List<string>();
            string finalUrl = string.Empty;

            var urlCandidates = urlCandidatesOverride is { Count: > 0 }
                ? urlCandidatesOverride
                : BuildUrlCandidates(request);

            foreach (var url in urlCandidates)
            {
                lastResponse?.Dispose();
                var attempt = await SendSingleRequestAsync(request, url, payloadJson, cancellationToken, allowApimSubscriptionKeyQueryRetry);
                lastResponse = attempt.Response;
                attemptedUrls.AddRange(attempt.AttemptedUrls);
                finalUrl = attempt.FinalUrl;

                if (lastResponse.IsSuccessStatusCode)
                    return (lastResponse, new AiRequestTrace { AttemptedUrls = attemptedUrls, FinalUrl = finalUrl });

                if (!allowNextUrlRetry || !ShouldTryNextUrl(request, lastResponse))
                    return (lastResponse, new AiRequestTrace { AttemptedUrls = attemptedUrls, FinalUrl = finalUrl });
            }

            return lastResponse is not null
                ? (lastResponse, new AiRequestTrace { AttemptedUrls = attemptedUrls, FinalUrl = finalUrl })
                : throw new HttpRequestException("未能构造任何可用的文本请求 URL。");
        }

        private async Task<(HttpResponseMessage Response, IReadOnlyList<string> AttemptedUrls, string FinalUrl)> SendSingleRequestAsync(
            AiChatRequestConfig request,
            string url,
            string payloadJson,
            CancellationToken cancellationToken,
            bool allowApimSubscriptionKeyQueryRetry)
        {
            var attemptedUrls = new List<string> { url };
            var response = await SendRequestCoreAsync(request, url, payloadJson, cancellationToken);

            if (!allowApimSubscriptionKeyQueryRetry || !IsMissingApimSubscriptionKeyResponse(request, response))
                return (response, attemptedUrls, url);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!IsMissingApimSubscriptionKeyResponse(request, response, body))
                return (response, attemptedUrls, url);

            response.Dispose();
            var queryUrl = BuildApimSubscriptionKeyQueryUrl(url, request.ApiKey);
            attemptedUrls.Add(queryUrl);
            var retriedResponse = await SendRequestCoreAsync(request, queryUrl, payloadJson, cancellationToken);
            return (retriedResponse, attemptedUrls, queryUrl);
        }

        private async Task<HttpResponseMessage> SendRequestCoreAsync(
            AiChatRequestConfig request,
            string url,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            if (request.AzureAuthMode == AzureAuthMode.AAD)
            {
                if (_tokenProvider?.IsLoggedIn != true)
                {
                    throw new InvalidOperationException(
                        "Azure AAD 认证未登录或静默登录已失效。请先在设置中重新登录当前终结点后再重试。");
                }

                var token = await _tokenProvider.GetTokenAsync(cancellationToken);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else
            {
                ApplyApiKeyAuthHeader(httpRequest, request);
            }

            return await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }

        private static void ApplyApiKeyAuthHeader(HttpRequestMessage request, AiChatRequestConfig config)
        {
            var mode = EndpointProfileUrlBuilder.GetEffectiveApiKeyHeaderMode(
                config.ProfileId,
                config.EndpointType,
                config.ApiKeyHeaderMode,
                config.IsAzureEndpoint);

            if (mode == ApiKeyHeaderMode.ApiKeyHeader)
            {
                request.Headers.Add("api-key", config.ApiKey);
                return;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        private static async Task<(int PromptTokens, int CompletionTokens)?> ConsumeResponseAsync(
            AiChatRequestConfig request,
            HttpResponseMessage response,
            Action<string> onChunk,
            Action<string>? onReasoningChunk,
            CancellationToken cancellationToken)
        {
            var protocol = GetEffectiveTextApiProtocol(request);
            if (protocol == TextApiProtocolMode.Responses)
            {
                return await StreamResponsesApiAsync(response, onChunk, onReasoningChunk, cancellationToken);
            }

            return await StreamResponseAsync(response, onChunk, onReasoningChunk, cancellationToken);
        }

        /// <summary>
        /// Responses API 流式 SSE 解析：逐行读取 data: 事件，提取 output_text.delta 和 reasoning_summary_text.delta。
        /// </summary>
        private static async Task<(int PromptTokens, int CompletionTokens)?> StreamResponsesApiAsync(
            HttpResponseMessage response,
            Action<string> onChunk,
            Action<string>? onReasoningChunk,
            CancellationToken cancellationToken)
        {
            int? promptTokens = null;
            int? completionTokens = null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;

                var data = line.Substring(6);
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    var eventType = root.TryGetProperty("type", out var typeElem)
                        ? typeElem.GetString() ?? ""
                        : "";

                    // 正文文本增量
                    if (eventType == "response.output_text.delta")
                    {
                        if (root.TryGetProperty("delta", out var deltaElem)
                            && deltaElem.ValueKind == JsonValueKind.String)
                        {
                            var text = deltaElem.GetString();
                            if (!string.IsNullOrEmpty(text))
                                onChunk(text);
                        }
                    }
                    // 推理摘要增量
                    else if (eventType == "response.reasoning_summary_text.delta" && onReasoningChunk != null)
                    {
                        if (root.TryGetProperty("delta", out var deltaElem)
                            && deltaElem.ValueKind == JsonValueKind.String)
                        {
                            var text = deltaElem.GetString();
                            if (!string.IsNullOrEmpty(text))
                                onReasoningChunk(text);
                        }
                    }
                    // response.completed 事件包含完整 usage
                    else if (eventType == "response.completed")
                    {
                        if (root.TryGetProperty("response", out var respElem)
                            && respElem.TryGetProperty("usage", out var usageElem)
                            && usageElem.ValueKind == JsonValueKind.Object)
                        {
                            if (usageElem.TryGetProperty("input_tokens", out var it))
                                promptTokens = it.GetInt32();
                            if (usageElem.TryGetProperty("output_tokens", out var ot))
                                completionTokens = ot.GetInt32();
                        }
                    }
                }
                catch (JsonException)
                {
                    // skip unparseable lines
                }
            }

            return promptTokens.HasValue || completionTokens.HasValue
                ? (promptTokens ?? 0, completionTokens ?? 0)
                : null;
        }

        private static async Task ReadResponsesApiResponseAsync(
            HttpResponseMessage response,
            Action<string> onChunk,
            Action<string>? onReasoningChunk,
            CancellationToken cancellationToken)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? fallbackOutputText = null;

            if (root.TryGetProperty("output_text", out var outputTextElem)
                && outputTextElem.ValueKind == JsonValueKind.String)
            {
                fallbackOutputText = outputTextElem.GetString();
            }

            var outputBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();

            if (root.TryGetProperty("output", out var outputElem)
                && outputElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var outputItem in outputElem.EnumerateArray())
                {
                    var outputType = outputItem.TryGetProperty("type", out var outputTypeElem)
                        ? outputTypeElem.GetString() ?? string.Empty
                        : string.Empty;

                    if (outputType.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendResponsesReasoningSummary(outputItem, reasoningBuilder);
                    }

                    if (!outputItem.TryGetProperty("content", out var contentElem)
                        || contentElem.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var contentItem in contentElem.EnumerateArray())
                    {
                        var type = contentItem.TryGetProperty("type", out var typeElem)
                            ? typeElem.GetString() ?? string.Empty
                            : string.Empty;

                        if ((type == "output_text" || type == "text")
                            && contentItem.TryGetProperty("text", out var textElem)
                            && textElem.ValueKind == JsonValueKind.String)
                        {
                            outputBuilder.Append(textElem.GetString());
                        }

                        if (type.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                            && contentItem.TryGetProperty("text", out var reasoningTextElem)
                            && reasoningTextElem.ValueKind == JsonValueKind.String)
                        {
                            reasoningBuilder.Append(reasoningTextElem.GetString());
                        }
                    }
                }
            }

            if (reasoningBuilder.Length > 0 && onReasoningChunk != null)
            {
                onReasoningChunk(reasoningBuilder.ToString());
            }

            if (outputBuilder.Length == 0 && !string.IsNullOrWhiteSpace(fallbackOutputText))
            {
                outputBuilder.Append(fallbackOutputText);
            }

            if (outputBuilder.Length > 0)
            {
                onChunk(outputBuilder.ToString());
                return;
            }

            throw new InvalidOperationException($"Responses API 返回成功，但未解析到输出文本。原始响应: {json}");
        }

        private static void AppendResponsesReasoningSummary(JsonElement outputItem, StringBuilder reasoningBuilder)
        {
            if (!outputItem.TryGetProperty("summary", out var summaryElem)
                || summaryElem.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var summaryItem in summaryElem.EnumerateArray())
            {
                var type = summaryItem.TryGetProperty("type", out var typeElem)
                    ? typeElem.GetString() ?? string.Empty
                    : string.Empty;

                if (!type.Contains("summary", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!summaryItem.TryGetProperty("text", out var textElem)
                    || textElem.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = textElem.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (reasoningBuilder.Length > 0)
                {
                    reasoningBuilder.AppendLine();
                    reasoningBuilder.AppendLine();
                }

                reasoningBuilder.Append(text);
            }
        }

        private static async Task<(int PromptTokens, int CompletionTokens)?> StreamResponseAsync(
            HttpResponseMessage response,
            Action<string> onChunk,
            Action<string>? onReasoningChunk,
            CancellationToken cancellationToken)
        {
            int? promptTokens = null;
            int? completionTokens = null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;

                var data = line.Substring(6);
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    // 提取 usage（通常在最后一个 chunk 中）
                    if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                    {
                        if (usageElem.TryGetProperty("prompt_tokens", out var pt))
                            promptTokens = pt.GetInt32();
                        if (usageElem.TryGetProperty("completion_tokens", out var ct2))
                            completionTokens = ct2.GetInt32();
                    }

                    if (!root.TryGetProperty("choices", out var choices)
                        || choices.GetArrayLength() == 0)
                        continue;

                    var firstChoice = choices[0];
                    if (!firstChoice.TryGetProperty("delta", out var delta))
                        continue;

                    if (TryReadReasoning(delta, out var reasoningText) && onReasoningChunk != null)
                    {
                        onReasoningChunk(reasoningText);
                    }

                    if (delta.TryGetProperty("content", out var contentElem))
                    {
                        var text = contentElem.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            onChunk(text);
                        }
                    }
                }
                catch (JsonException)
                {
                    // skip unparseable lines
                }
            }

            return promptTokens.HasValue || completionTokens.HasValue
                ? (promptTokens ?? 0, completionTokens ?? 0)
                : null;
        }

        private static bool TryReadReasoning(JsonElement delta, out string text)
        {
            text = "";

            if (delta.TryGetProperty("reasoning", out var reasoningElem))
            {
                return TryExtractReasoningText(reasoningElem, out text);
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoningContentElem))
            {
                return TryExtractReasoningText(reasoningContentElem, out text);
            }

            if (delta.TryGetProperty("thinking", out var thinkingElem))
            {
                return TryExtractReasoningText(thinkingElem, out text);
            }

            return false;
        }

        private static bool TryExtractReasoningText(JsonElement element, out string text)
        {
            text = "";

            if (element.ValueKind == JsonValueKind.String)
            {
                text = element.GetString() ?? "";
                return !string.IsNullOrEmpty(text);
            }

            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("text", out var textElem)
                && textElem.ValueKind == JsonValueKind.String)
            {
                text = textElem.GetString() ?? "";
                return !string.IsNullOrEmpty(text);
            }

            return false;
        }

        private static IReadOnlyList<string> BuildUrlCandidates(AiChatRequestConfig request)
            => EndpointProfileUrlBuilder.BuildTextUrlCandidates(
                request.ApiEndpoint,
                request.ProfileId,
                request.EndpointType,
                request.TextApiProtocolMode,
                request.IsAzureEndpoint,
                request.DeploymentName,
                request.ApiVersion);

        private static object BuildRequestBody(
            AiChatRequestConfig request,
            string systemPrompt,
            string userContent,
            bool enableReasoning)
        {
            var protocol = GetEffectiveTextApiProtocol(request);
            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            };

            if (protocol == TextApiProtocolMode.Responses)
            {
                var input = new List<object>
                {
                    new
                    {
                        role = "system",
                        content = new[] { new { type = "input_text", text = systemPrompt } }
                    },
                    new
                    {
                        role = "user",
                        content = new[] { new { type = "input_text", text = userContent } }
                    }
                };

                var responsesBody = new Dictionary<string, object>
                {
                    ["model"] = request.ModelName,
                    ["input"] = input,
                    ["stream"] = true
                };

                if (enableReasoning)
                {
                    responsesBody["reasoning"] = new { effort = "medium", summary = "auto" };
                }

                return responsesBody;
            }

            if (request.IsAzureEndpoint)
            {
                var azureBody = new Dictionary<string, object>
                {
                    ["model"] = request.ModelName,
                    ["messages"] = messages,
                    ["stream"] = true,
                    ["stream_options"] = new { include_usage = true }
                };
                if (enableReasoning)
                {
                    azureBody["reasoning_effort"] = "medium";
                }
                return azureBody;
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = request.ModelName,
                ["messages"] = messages,
                ["stream"] = true,
                ["stream_options"] = new { include_usage = true }
            };

            if (enableReasoning)
            {
                body["reasoning_effort"] = "medium";
            }

            return body;
        }

        private static TextApiProtocolMode GetEffectiveTextApiProtocol(AiChatRequestConfig request)
            => EndpointProfileUrlBuilder.GetEffectiveTextProtocol(
                request.ProfileId,
                request.EndpointType,
                request.TextApiProtocolMode,
                request.IsAzureEndpoint);

        private static string GetEffectiveApiVersion(AiChatRequestConfig request, TextApiProtocolMode protocol)
            => EndpointProfileUrlBuilder.GetEffectiveTextApiVersion(
                request.ProfileId,
                request.EndpointType,
                request.ApiVersion,
                request.IsAzureEndpoint);

        private static bool ShouldTryNextUrl(AiChatRequestConfig request, HttpResponseMessage response)
            => (IsApimGateway(request) || request.IsAzureEndpoint) && (int)response.StatusCode is 404 or 405;

        // --- 临时禁用 APIM subscription-key query 回退 ---
        private static bool IsMissingApimSubscriptionKeyResponse(AiChatRequestConfig request, HttpResponseMessage response)
            => false; // IsApimGateway(request) && (int)response.StatusCode == 401;

        private static bool IsMissingApimSubscriptionKeyResponse(AiChatRequestConfig request, HttpResponseMessage response, string? body)
            => false; // 原逻辑依赖上面的方法，一并禁用

        private static string BuildApimSubscriptionKeyQueryUrl(string url, string apiKey)
        {
            var separator = url.Contains('?') ? '&' : '?';
            return $"{url}{separator}subscription-key={Uri.EscapeDataString(apiKey)}";
        }

        private static bool IsApimGateway(AiChatRequestConfig request)
            => request.EndpointType == EndpointApiType.ApiManagementGateway;
    }
}
