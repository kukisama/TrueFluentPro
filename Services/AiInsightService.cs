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

namespace TrueFluentPro.Services
{
    public sealed class AiChatRequestConfig
    {
        public AiProviderType ProviderType { get; init; } = AiProviderType.OpenAiCompatible;
        public string ApiEndpoint { get; init; } = "";
        public string ApiKey { get; init; } = "";
        public string ApiVersion { get; init; } = "2024-02-01";
        public AzureAuthMode AzureAuthMode { get; init; } = AzureAuthMode.ApiKey;
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
                ProviderType = config.ProviderType,
                ApiEndpoint = config.ApiEndpoint,
                ApiKey = config.ApiKey,
                ApiVersion = config.ApiVersion,
                AzureAuthMode = config.AzureAuthMode,
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
    }

    public class AiInsightService
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
            Action<string>? onReasoningChunk = null)
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
                onReasoningChunk);
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
            Action<string>? onReasoningChunk = null)
        {
            using var response = await SendRequestAsync(
                request,
                systemPrompt,
                userContent,
                enableReasoning,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);

                if (enableReasoning
                    && request.ProviderType == AiProviderType.OpenAiCompatible
                    && (int)response.StatusCode is >= 400 and < 500)
                {
                    using var fallbackResponse = await SendRequestAsync(
                        request,
                        systemPrompt,
                        userContent,
                        enableReasoning: false,
                        cancellationToken);

                    if (fallbackResponse.IsSuccessStatusCode)
                    {
                        onOutcome?.Invoke(new AiRequestOutcome
                        {
                            UsedReasoning = false,
                            UsedFallback = true
                        });
                        await StreamResponseAsync(fallbackResponse, onChunk, onReasoningChunk, cancellationToken);
                        return;
                    }

                    var fallbackText = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"Request failed: {(int)fallbackResponse.StatusCode} {fallbackResponse.ReasonPhrase}. {fallbackText}");
                }

                throw new HttpRequestException($"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {errorText}");
            }

            onOutcome?.Invoke(new AiRequestOutcome
            {
                UsedReasoning = enableReasoning
                                && profile == AiChatProfile.Summary
                                && request.SummaryEnableReasoning
                                && request.ProviderType == AiProviderType.OpenAiCompatible,
                UsedFallback = false
            });

            await StreamResponseAsync(response, onChunk, onReasoningChunk, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendRequestAsync(
            AiChatRequestConfig request,
            string systemPrompt,
            string userContent,
            bool enableReasoning,
            CancellationToken cancellationToken)
        {
            var url = BuildUrl(request);
            var body = BuildRequestBody(request, systemPrompt, userContent, enableReasoning);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            if (request.IsAzureEndpoint)
            {
                if (request.AzureAuthMode == AzureAuthMode.AAD && _tokenProvider?.IsLoggedIn == true)
                {
                    var token = await _tokenProvider.GetTokenAsync(cancellationToken);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                else
                {
                    httpRequest.Headers.Add("api-key", request.ApiKey);
                }
            }
            else
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            }

            return await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }

        private static async Task StreamResponseAsync(
            HttpResponseMessage response,
            Action<string> onChunk,
            Action<string>? onReasoningChunk,
            CancellationToken cancellationToken)
        {
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

        private static string BuildUrl(AiChatRequestConfig request)
        {
            var baseUrl = request.ApiEndpoint.TrimEnd('/');

            if (request.IsAzureEndpoint)
            {
                return $"{baseUrl}/openai/deployments/{request.DeploymentName}/chat/completions?api-version={request.ApiVersion}";
            }

            if (baseUrl.EndsWith("/v1"))
                return $"{baseUrl}/chat/completions";
            return $"{baseUrl}/v1/chat/completions";
        }

        private static object BuildRequestBody(
            AiChatRequestConfig request,
            string systemPrompt,
            string userContent,
            bool enableReasoning)
        {
            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            };

            if (request.IsAzureEndpoint)
            {
                return new { messages, stream = true };
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = request.ModelName,
                ["messages"] = messages,
                ["stream"] = true
            };

            if (enableReasoning && request.SummaryEnableReasoning)
            {
                body["reasoning"] = new { effort = "medium" };
            }

            return body;
        }
    }
}
