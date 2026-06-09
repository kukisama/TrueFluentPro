using System;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 从 AAD 登录的 Foundry / Azure OpenAI 终结点推导同资源的 Azure Speech custom domain。
    /// 规则：保留资源子域名，将 .openai.azure.com / .services.ai.azure.com
    /// 替换为 .cognitiveservices.azure.com（中国区对应 azure.cn）。
    /// </summary>
    public sealed record FoundrySpeechEndpointResolution(
        string Subdomain,
        string Region,
        string ResourceEndpoint,
        string FastTranscriptionEndpoint,
        string BatchTranscriptionEndpoint,
        string VoiceListEndpoint,
        string TextToSpeechEndpoint,
        bool IsChinaCloud);

    public static class FoundrySpeechEndpointResolver
    {
        public const string SpeechResourceIdPrefix = "foundry-speech-";

        public static string BuildSpeechResourceId(string endpointId)
            => SpeechResourceIdPrefix + endpointId;

        public static string? TryExtractEndpointIdFromSpeechResourceId(string? speechResourceId)
        {
            if (string.IsNullOrWhiteSpace(speechResourceId)
                || !speechResourceId.StartsWith(SpeechResourceIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return speechResourceId[SpeechResourceIdPrefix.Length..];
        }

        public static string BuildEndpointProfileKey(string endpointId)
            => $"endpoint_{endpointId}";

        public static bool TryResolve(AiEndpoint? endpoint, out FoundrySpeechEndpointResolution? resolution, out string errorMessage)
        {
            resolution = null;
            errorMessage = string.Empty;

            if (endpoint == null)
            {
                errorMessage = "Foundry 终结点为空。";
                return false;
            }

            if (!endpoint.IsEnabled)
            {
                errorMessage = $"Foundry 终结点“{endpoint.Name}”已禁用。";
                return false;
            }

            if (endpoint.EndpointType != EndpointApiType.AzureOpenAi || endpoint.AuthMode != AzureAuthMode.AAD)
            {
                errorMessage = $"终结点“{endpoint.Name}”不是 AAD 模式的 Azure OpenAI / Foundry 终结点。";
                return false;
            }

            if (!TryParseBaseUrl(endpoint.BaseUrl, out var subdomain, out var isChinaCloud, out errorMessage))
            {
                errorMessage = $"无法从终结点“{endpoint.Name}”推导 Speech 资源：{errorMessage}";
                return false;
            }

            var region = ParseRegionFromSubdomain(subdomain);
            if (string.IsNullOrWhiteSpace(region))
            {
                errorMessage = $"无法从资源子域名“{subdomain}”解析区域。";
                return false;
            }

            var suffix = isChinaCloud ? "cognitiveservices.azure.cn" : "cognitiveservices.azure.com";
            var resourceEndpoint = $"https://{subdomain}.{suffix}";
            resolution = new FoundrySpeechEndpointResolution(
                subdomain,
                region,
                resourceEndpoint,
                $"{resourceEndpoint}/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
                $"{resourceEndpoint}/speechtotext/v3.1/transcriptions",
                $"{resourceEndpoint}/tts/cognitiveservices/voices/list",
                $"{resourceEndpoint}/tts/cognitiveservices/v1",
                isChinaCloud);
            return true;
        }

        public static bool TryParseBaseUrl(string? baseUrl, out string subdomain, out bool isChinaCloud, out string errorMessage)
        {
            subdomain = string.Empty;
            isChinaCloud = false;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                errorMessage = "API 地址为空。";
                return false;
            }

            try
            {
                var url = baseUrl.Trim();
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                isChinaCloud = host.EndsWith(".azure.cn", StringComparison.OrdinalIgnoreCase);

                subdomain = TrimSuffix(host, ".openai.azure.com")
                         ?? TrimSuffix(host, ".services.ai.azure.com")
                         ?? TrimSuffix(host, ".cognitiveservices.azure.com")
                         ?? TrimSuffix(host, ".openai.azure.cn")
                         ?? TrimSuffix(host, ".services.ai.azure.cn")
                         ?? TrimSuffix(host, ".cognitiveservices.azure.cn")
                         ?? string.Empty;

                if (string.IsNullOrWhiteSpace(subdomain))
                {
                    errorMessage = "仅支持 *.openai.azure.com、*.services.ai.azure.com 或 *.cognitiveservices.azure.com 形态。";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static string? ParseSubdomain(string? baseUrl)
            => TryParseBaseUrl(baseUrl, out var subdomain, out _, out _) ? subdomain : null;

        public static string? ParseRegion(string? baseUrl)
        {
            var subdomain = ParseSubdomain(baseUrl);
            return string.IsNullOrWhiteSpace(subdomain)
                ? null
                : ParseRegionFromSubdomain(subdomain);
        }

        public static bool IsAzureChinaUrl(string? baseUrl)
            => !string.IsNullOrWhiteSpace(baseUrl)
               && baseUrl.Contains(".azure.cn", StringComparison.OrdinalIgnoreCase);

        private static string ParseRegionFromSubdomain(string subdomain)
        {
            var lastDash = subdomain.LastIndexOf('-');
            return lastDash >= 0 && lastDash < subdomain.Length - 1
                ? subdomain[(lastDash + 1)..]
                : subdomain;
        }

        private static string? TrimSuffix(string host, string suffix)
            => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? host[..^suffix.Length]
                : null;
    }
}