using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services
{
    public class AiEndpointModelDiscoveryService : IAiEndpointModelDiscoveryService
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly IEndpointProfileCatalogService _profileCatalogService;
        private readonly IEndpointPlatformDefaultPolicyService _platformDefaultPolicyService;

        public AiEndpointModelDiscoveryService(
            IEndpointProfileCatalogService profileCatalogService,
            IEndpointPlatformDefaultPolicyService platformDefaultPolicyService)
        {
            _profileCatalogService = profileCatalogService;
            _platformDefaultPolicyService = platformDefaultPolicyService;
        }

        public async Task<AiEndpointModelDiscoveryResult> DiscoverModelsAsync(AiEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            {
                return Fail("请先填写 API 地址。");
            }

            if (endpoint.IsAzureEndpoint)
            {
                return Fail("Azure 终结点通常无法通过统一的 /models 接口枚举部署，请手动填写部署名称。");
            }

            if (endpoint.AuthMode == AzureAuthMode.AAD)
            {
                return Fail("OpenAI Compatible 模型枚举暂不支持 AAD，请改用 API Key 后重试。");
            }

            if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                return Fail("请先填写 API 密钥。");
            }

            string? lastError = null;
            foreach (var url in BuildCandidateUrls(endpoint))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyApiKeyHeader(request, endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                try
                {
                    using var response = await HttpClient.SendAsync(request, cancellationToken);
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                        continue;
                    }

                    var modelIds = ParseModelIds(content);
                    if (modelIds.Count == 0)
                    {
                        lastError = "接口返回成功，但没有解析到可添加的模型。";
                        continue;
                    }

                    return new AiEndpointModelDiscoveryResult
                    {
                        Success = true,
                        Message = $"已发现 {modelIds.Count} 个模型，可直接点“添加”。",
                        ModelIds = modelIds
                    };
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = "请求超时，请检查终结点地址与网络连接。";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            return Fail(string.IsNullOrWhiteSpace(lastError)
                ? "未能从该终结点读取模型列表，请确认它支持 OpenAI Compatible 的 models 接口。"
                : $"拉取失败：{lastError}");
        }

        private static void ApplyApiKeyHeader(HttpRequestMessage request, AiEndpoint endpoint)
        {
            var mode = EndpointProfileUrlBuilder.GetEffectiveApiKeyHeaderMode(
                endpoint.ProfileId,
                endpoint.EndpointType,
                endpoint.ApiKeyHeaderMode,
                endpoint.IsAzureEndpoint || endpoint.EndpointType == EndpointApiType.ApiManagementGateway);

            if (mode == ApiKeyHeaderMode.ApiKeyHeader)
            {
                request.Headers.TryAddWithoutValidation("api-key", endpoint.ApiKey.Trim());
                return;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey.Trim());
        }

        private IReadOnlyList<string> BuildCandidateUrls(AiEndpoint endpoint)
        {
            var profile = ResolveProfile(endpoint);
            var urls = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddUrl(string? url)
            {
                if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
                {
                    urls.Add(url);
                }
            }

            void AddRange(IEnumerable<string> values)
            {
                foreach (var value in values)
                {
                    AddUrl(value);
                }
            }

            var overridePrimaryUrls = EndpointProfileRuntimeResolver.BuildUrls(
                endpoint.BaseUrl,
                string.IsNullOrWhiteSpace(profile?.Overrides.Routes.ModelDiscovery.PrimaryUrl)
                    ? Array.Empty<string>()
                    : new[] { profile!.Overrides.Routes.ModelDiscovery.PrimaryUrl });

            var legacyProfileUrls = EndpointProfileRuntimeResolver.BuildUrls(
                endpoint.BaseUrl,
                profile?.ModelDiscovery.UrlCandidates);

            var explicitFallbackUrls = EndpointProfileRuntimeResolver.BuildUrls(
                endpoint.BaseUrl,
                profile?.Fallbacks.ModelDiscovery);

            var hasVendorDefinedDiscovery = overridePrimaryUrls.Count > 0
                                            || legacyProfileUrls.Count > 0
                                            || explicitFallbackUrls.Count > 0;

            if (hasVendorDefinedDiscovery)
            {
                AddRange(overridePrimaryUrls);

                if (overridePrimaryUrls.Count == 0 && legacyProfileUrls.Count > 0)
                {
                    AddUrl(legacyProfileUrls[0]);
                }

                AddRange(explicitFallbackUrls);

                if (legacyProfileUrls.Count > 1)
                {
                    foreach (var url in legacyProfileUrls.Skip(1))
                    {
                        AddUrl(url);
                    }
                }

                return urls;
            }

            try
            {
                var platformPolicy = _platformDefaultPolicyService.GetPolicy(endpoint.EndpointType);
                var platformUrls = EndpointProfileRuntimeResolver.BuildUrls(
                    endpoint.BaseUrl,
                    string.IsNullOrWhiteSpace(platformPolicy.ModelDiscovery.PrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { platformPolicy.ModelDiscovery.PrimaryUrl });
                AddRange(platformUrls);
            }
            catch
            {
                // 如果平台默认值尚未定义当前终结点类型，则维持空列表，交由上层提示。
            }

            return urls;
        }

        private EndpointProfileDefinition? ResolveProfile(AiEndpoint endpoint)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.ProfileId))
            {
                var byId = _profileCatalogService.FindProfile(endpoint.ProfileId);
                if (byId != null)
                    return byId;
            }

            return _profileCatalogService.GetProfile(endpoint.EndpointType);
        }

        private static IReadOnlyList<string> ParseModelIds(string content)
        {
            using var doc = JsonDocument.Parse(content);
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ReadArray(JsonElement array)
            {
                if (array.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var item in array.EnumerateArray())
                {
                    switch (item.ValueKind)
                    {
                        case JsonValueKind.String:
                            Add(item.GetString());
                            break;
                        case JsonValueKind.Object:
                            if (item.TryGetProperty("id", out var id)) Add(id.GetString());
                            else if (item.TryGetProperty("model", out var model)) Add(model.GetString());
                            else if (item.TryGetProperty("name", out var name)) Add(name.GetString());
                            break;
                    }
                }
            }

            void Add(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value.Trim());
                }
            }

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadArray(root);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data)) ReadArray(data);
                if (root.TryGetProperty("models", out var models)) ReadArray(models);
                if (root.TryGetProperty("value", out var value)) ReadArray(value);
            }

            return results.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static AiEndpointModelDiscoveryResult Fail(string message) => new()
        {
            Success = false,
            Message = message,
            ModelIds = Array.Empty<string>()
        };
    }
}
