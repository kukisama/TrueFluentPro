using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services
{
    public class SettingsImportExportService : ISettingsImportExportService
    {
        public AzureSpeechConfig CreateFullExportConfig(AzureSpeechConfig config)
            => SanitizeFullConfig(config);

        public AzureSpeechConfig NormalizeImportedFullConfig(AzureSpeechConfig config)
            => SanitizeFullConfig(config);

        private static AzureSpeechConfig SanitizeFullConfig(AzureSpeechConfig config)
        {
            var result = CloneConfig(config);
            result.Endpoints = (result.Endpoints ?? new List<AiEndpoint>())
                .Where(IsImportableEndpoint)
                .Select(endpoint =>
                {
                    endpoint.AzureTenantId = "";
                    endpoint.AzureClientId = "";
                    return endpoint;
                })
                .ToList();

            var ai = result.AiConfig ??= new AiConfig();
            ai.AzureTenantId = "";
            ai.AzureClientId = "";
            if (ai.AzureAuthMode == AzureAuthMode.AAD)
            {
                ai.AzureAuthMode = AzureAuthMode.ApiKey;
            }

            var media = result.MediaGenConfig ??= new MediaGenConfig();

            ai.InsightModelRef = NormalizeReference(result, ai.InsightModelRef, ModelCapability.Text);
            ai.SummaryModelRef = NormalizeReference(result, ai.SummaryModelRef, ModelCapability.Text);
            ai.QuickModelRef = NormalizeReference(result, ai.QuickModelRef, ModelCapability.Text);
            ai.ReviewModelRef = NormalizeReference(result, ai.ReviewModelRef, ModelCapability.Text);
            media.ImageModelRef = NormalizeReference(result, media.ImageModelRef, ModelCapability.Image);
            media.VideoModelRef = NormalizeReference(result, media.VideoModelRef, ModelCapability.Video);
            result.RealtimeTranscriptionModelRef = NormalizeReference(result, result.RealtimeTranscriptionModelRef, ModelCapability.SpeechToText);
            result.BatchTranscriptionModelRef = NormalizeReference(result, result.BatchTranscriptionModelRef, ModelCapability.SpeechToText);
            result.TextToSpeechModelRef = NormalizeReference(result, result.TextToSpeechModelRef, ModelCapability.TextToSpeech);
            NormalizeSpeechResources(result);

            return result;
        }

        public SettingsTransferPackage CreateExportPackage(AzureSpeechConfig config)
        {
            var ai = config.AiConfig ?? new AiConfig();
            var media = config.MediaGenConfig ?? new MediaGenConfig();
            var exportableEndpoints = (config.Endpoints ?? new List<AiEndpoint>())
                .Where(IsImportableEndpoint)
                .ToList();

            return new SettingsTransferPackage
            {
                Speech = new TransferSpeechConfig
                {
                    ActiveSubscriptionIndex = NormalizeActiveSubscriptionIndex(config.ActiveSubscriptionIndex, config.Subscriptions.Count),
                    ActiveSpeechResourceId = config.GetEffectiveActiveSpeechResourceId(),
                    Subscriptions = config.Subscriptions.Select(subscription => new TransferSpeechSubscription
                    {
                        Name = subscription.Name,
                        SubscriptionKey = subscription.SubscriptionKey,
                        ServiceRegion = subscription.ServiceRegion,
                        Endpoint = subscription.Endpoint
                    }).ToList(),
                    Resources = config.GetEffectiveSpeechResources().Select(resource => new TransferSpeechResource
                    {
                        Id = resource.Id,
                        Name = resource.Name,
                        Vendor = resource.Vendor,
                        ConnectorType = resource.ConnectorType,
                        IsEnabled = resource.IsEnabled,
                        Capabilities = resource.Capabilities,
                        SubscriptionName = resource.SubscriptionName,
                        SubscriptionKey = resource.SubscriptionKey,
                        ServiceRegion = resource.ServiceRegion,
                        Endpoint = resource.Endpoint,
                        RealtimeSpeechToTextModelRef = CloneReference(resource.RealtimeSpeechToTextModelRef),
                        BatchSpeechToTextModelRef = CloneReference(resource.BatchSpeechToTextModelRef),
                        TextToSpeechModelRef = CloneReference(resource.TextToSpeechModelRef)
                    }).ToList()
                },
                Storage = new TransferStorageConfig
                {
                    BatchStorageConnectionString = config.BatchStorageConnectionString,
                    BatchAudioContainerName = config.BatchAudioContainerName,
                    BatchResultContainerName = config.BatchResultContainerName,
                    ReviewSubtitleSourceMode = config.GetEffectiveReviewSubtitleSourceMode()
                },
                Endpoints = exportableEndpoints.Select(endpoint => new TransferAiEndpoint
                {
                    Id = endpoint.Id,
                    ProfileId = endpoint.ProfileId,
                    Name = endpoint.Name,
                    IsEnabled = endpoint.IsEnabled,
                    EndpointType = endpoint.EndpointType,
                    BaseUrl = endpoint.BaseUrl,
                    ApiKey = endpoint.ApiKey,
                    ApiVersion = endpoint.ApiVersion,
                    AuthMode = endpoint.AuthMode,
                    ApiKeyHeaderMode = endpoint.ApiKeyHeaderMode,
                    TextApiProtocolMode = endpoint.TextApiProtocolMode,
                    ImageApiRouteMode = endpoint.ImageApiRouteMode,
                    Models = endpoint.Models.Select(model => new TransferAiModel
                    {
                        ModelId = model.ModelId,
                        DisplayName = model.DisplayName,
                        DeploymentName = model.DeploymentName,
                        GroupName = model.GroupName,
                        Capabilities = model.Capabilities
                    }).ToList()
                }).ToList(),
                ModelSelections = new TransferModelSelections
                {
                    InsightModelRef = CloneReference(ai.InsightModelRef),
                    SummaryModelRef = CloneReference(ai.SummaryModelRef),
                    QuickModelRef = CloneReference(ai.QuickModelRef),
                    ReviewModelRef = CloneReference(ai.ReviewModelRef),
                    ImageModelRef = CloneReference(media.ImageModelRef),
                    VideoModelRef = CloneReference(media.VideoModelRef),
                    RealtimeTranscriptionModelRef = CloneReference(config.RealtimeTranscriptionModelRef),
                    BatchTranscriptionModelRef = CloneReference(config.BatchTranscriptionModelRef),
                    TextToSpeechModelRef = CloneReference(config.TextToSpeechModelRef)
                }
            };
        }

        public AzureSpeechConfig ApplyImportPackage(AzureSpeechConfig currentConfig, SettingsTransferPackage package)
        {
            ValidatePackage(package);

            var result = CloneConfig(currentConfig);
            result.Subscriptions = (package.Speech?.Subscriptions ?? new List<TransferSpeechSubscription>())
                .Select(subscription => new AzureSubscription
                {
                    Name = subscription.Name?.Trim() ?? "",
                    SubscriptionKey = subscription.SubscriptionKey?.Trim() ?? "",
                    ServiceRegion = string.IsNullOrWhiteSpace(subscription.ServiceRegion) ? "southeastasia" : subscription.ServiceRegion.Trim(),
                    Endpoint = subscription.Endpoint?.Trim() ?? ""
                })
                .ToList();
            result.ActiveSubscriptionIndex = NormalizeActiveSubscriptionIndex(package.Speech?.ActiveSubscriptionIndex ?? 0, result.Subscriptions.Count);
            result.SpeechResources = (package.Speech?.Resources ?? new List<TransferSpeechResource>())
                .Select(resource => new SpeechResource
                {
                    Id = string.IsNullOrWhiteSpace(resource.Id) ? Guid.NewGuid().ToString() : resource.Id.Trim(),
                    Name = resource.Name?.Trim() ?? "",
                    Vendor = resource.Vendor,
                    ConnectorType = resource.ConnectorType,
                    IsEnabled = resource.IsEnabled,
                    Capabilities = resource.Capabilities,
                    SubscriptionName = resource.SubscriptionName?.Trim() ?? "",
                    SubscriptionKey = resource.SubscriptionKey?.Trim() ?? "",
                    ServiceRegion = resource.ServiceRegion?.Trim() ?? "",
                    Endpoint = resource.Endpoint?.Trim() ?? "",
                    RealtimeSpeechToTextModelRef = CloneReference(resource.RealtimeSpeechToTextModelRef),
                    BatchSpeechToTextModelRef = CloneReference(resource.BatchSpeechToTextModelRef),
                    TextToSpeechModelRef = CloneReference(resource.TextToSpeechModelRef)
                })
                .ToList();
            result.ActiveSpeechResourceId = package.Speech?.ActiveSpeechResourceId?.Trim() ?? "";

            if (package.Storage != null)
            {
                result.BatchStorageConnectionString = package.Storage.BatchStorageConnectionString?.Trim() ?? "";
                result.BatchAudioContainerName = string.IsNullOrWhiteSpace(package.Storage.BatchAudioContainerName)
                    ? AzureSpeechConfig.DefaultBatchAudioContainerName
                    : package.Storage.BatchAudioContainerName.Trim();
                result.BatchResultContainerName = string.IsNullOrWhiteSpace(package.Storage.BatchResultContainerName)
                    ? AzureSpeechConfig.DefaultBatchResultContainerName
                    : package.Storage.BatchResultContainerName.Trim();
                result.ReviewSubtitleSourceMode = package.Storage.ReviewSubtitleSourceMode;
                result.UseSpeechSubtitleForReview = result.ReviewSubtitleSourceMode == ReviewSubtitleSourceMode.SpeechSubtitle;
                var hasImportedStorage = !string.IsNullOrWhiteSpace(result.BatchStorageConnectionString);
                result.BatchStorageIsValid = hasImportedStorage;
            }
            else
            {
                result.BatchStorageConnectionString = "";
                result.BatchStorageIsValid = false;
                result.ReviewSubtitleSourceMode = ReviewSubtitleSourceMode.DefaultSubtitle;
                result.UseSpeechSubtitleForReview = false;
            }

            result.Endpoints = MapEndpoints(package.Endpoints);

            var ai = result.AiConfig ??= new AiConfig();
            var media = result.MediaGenConfig ??= new MediaGenConfig();
            var selections = package.ModelSelections ?? new TransferModelSelections();

            ai.InsightModelRef = NormalizeReference(result, selections.InsightModelRef, ModelCapability.Text);
            ai.SummaryModelRef = NormalizeReference(result, selections.SummaryModelRef, ModelCapability.Text);
            ai.QuickModelRef = NormalizeReference(result, selections.QuickModelRef, ModelCapability.Text);
            ai.ReviewModelRef = NormalizeReference(result, selections.ReviewModelRef, ModelCapability.Text);
            media.ImageModelRef = NormalizeReference(result, selections.ImageModelRef, ModelCapability.Image);
            media.VideoModelRef = NormalizeReference(result, selections.VideoModelRef, ModelCapability.Video);
            result.RealtimeTranscriptionModelRef = NormalizeReference(result, selections.RealtimeTranscriptionModelRef, ModelCapability.SpeechToText);
            result.BatchTranscriptionModelRef = NormalizeReference(result, selections.BatchTranscriptionModelRef, ModelCapability.SpeechToText);
            result.TextToSpeechModelRef = NormalizeReference(result, selections.TextToSpeechModelRef, ModelCapability.TextToSpeech);
            NormalizeSpeechResources(result);

            return result;
        }

        private static void ValidatePackage(SettingsTransferPackage package)
        {
            if (package == null)
            {
                throw new InvalidOperationException("导入文件为空或无法解析。");
            }

            if (!string.Equals(package.Format, "TrueFluentPro.ResourceConfig", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("这不是受支持的资源配置文件，请选择通过本程序导出的 JSON 文件。");
            }

            if (package.Version is not 1 and not 2)
            {
                throw new InvalidOperationException($"暂不支持的配置文件版本：v{package.Version}");
            }
        }

        private static AzureSpeechConfig CloneConfig(AzureSpeechConfig config)
        {
            var json = JsonSerializer.Serialize(config);
            return JsonSerializer.Deserialize<AzureSpeechConfig>(json) ?? new AzureSpeechConfig();
        }

        private static int NormalizeActiveSubscriptionIndex(int index, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (index < 0)
            {
                return 0;
            }

            if (index >= count)
            {
                return count - 1;
            }

            return index;
        }

        private static List<AiEndpoint> MapEndpoints(IEnumerable<TransferAiEndpoint>? endpoints)
        {
            var result = new List<AiEndpoint>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var endpoint in endpoints ?? Enumerable.Empty<TransferAiEndpoint>())
            {
                if (!IsImportableEndpoint(endpoint))
                {
                    continue;
                }

                var endpointId = string.IsNullOrWhiteSpace(endpoint.Id) ? Guid.NewGuid().ToString() : endpoint.Id.Trim();
                if (!usedIds.Add(endpointId))
                {
                    endpointId = Guid.NewGuid().ToString();
                    usedIds.Add(endpointId);
                }

                result.Add(new AiEndpoint
                {
                    Id = endpointId,
                    ProfileId = string.IsNullOrWhiteSpace(endpoint.ProfileId)
                        ? MapProfileId(endpoint.EndpointType)
                        : endpoint.ProfileId.Trim(),
                    Name = endpoint.Name?.Trim() ?? "",
                    IsEnabled = endpoint.IsEnabled,
                    EndpointType = endpoint.EndpointType,
                    ProviderType = MapProviderType(endpoint.EndpointType),
                    BaseUrl = endpoint.BaseUrl?.Trim() ?? "",
                    ApiKey = endpoint.ApiKey?.Trim() ?? "",
                    ApiVersion = endpoint.ApiVersion?.Trim() ?? "",
                    AuthMode = endpoint.AuthMode,
                    ApiKeyHeaderMode = endpoint.ApiKeyHeaderMode,
                    TextApiProtocolMode = endpoint.TextApiProtocolMode,
                    ImageApiRouteMode = endpoint.ImageApiRouteMode,
                    AzureTenantId = "",
                    AzureClientId = "",
                    Models = (endpoint.Models ?? new List<TransferAiModel>()).Select(model => new AiModelEntry
                    {
                        ModelId = model.ModelId?.Trim() ?? "",
                        DisplayName = model.DisplayName?.Trim() ?? "",
                        DeploymentName = model.DeploymentName?.Trim() ?? "",
                        GroupName = model.GroupName?.Trim() ?? "",
                        Capabilities = model.Capabilities
                    }).ToList()
                });
            }

            return result;
        }

        private static ModelReference? CloneReference(ModelReference? reference)
        {
            if (reference == null)
            {
                return null;
            }

            return new ModelReference
            {
                EndpointId = reference.EndpointId,
                ModelId = reference.ModelId
            };
        }

        private static ModelReference? NormalizeReference(AzureSpeechConfig config, ModelReference? reference, ModelCapability capability)
        {
            if (IsReferenceValid(config, reference, capability))
            {
                return CloneReference(reference);
            }

            return null;
        }

        private static void NormalizeSpeechResources(AzureSpeechConfig config)
        {
            if (config.SpeechResources.Count == 0)
            {
                config.EnsureSpeechResourcesBackfilledFromLegacy();
                return;
            }

            foreach (var resource in config.SpeechResources)
            {
                resource.RealtimeSpeechToTextModelRef = NormalizeReference(config, resource.RealtimeSpeechToTextModelRef, ModelCapability.SpeechToText);
                resource.BatchSpeechToTextModelRef = NormalizeReference(config, resource.BatchSpeechToTextModelRef, ModelCapability.SpeechToText);
                resource.TextToSpeechModelRef = NormalizeReference(config, resource.TextToSpeechModelRef, ModelCapability.TextToSpeech);
            }

            config.ActiveSpeechResourceId = config.GetEffectiveActiveSpeechResourceId();
        }

        private static bool IsReferenceValid(AzureSpeechConfig config, ModelReference? reference, ModelCapability capability)
        {
            if (reference == null)
            {
                return false;
            }

            var resolved = config.ResolveModel(reference);
            return resolved.Endpoint != null
                   && resolved.Model != null
                     && resolved.Model.Capabilities.HasFlag(capability)
                     && EndpointCapabilityPolicyResolver.IsCapabilityAllowed(resolved.Endpoint.ProfileId, resolved.Endpoint.EndpointType, capability);
        }

        private static bool IsImportableEndpoint(AiEndpoint endpoint)
            => endpoint.AuthMode != AzureAuthMode.AAD;

        private static bool IsImportableEndpoint(TransferAiEndpoint endpoint)
            => endpoint.AuthMode != AzureAuthMode.AAD;

        private static AiProviderType MapProviderType(EndpointApiType endpointType)
            => endpointType == EndpointApiType.AzureOpenAi
                ? AiProviderType.AzureOpenAi
                : AiProviderType.OpenAiCompatible;

        private static string MapProfileId(EndpointApiType endpointType)
            => endpointType switch
            {
                EndpointApiType.AzureOpenAi => "builtin.microsoft.azure-openai",
                EndpointApiType.ApiManagementGateway => "builtin.microsoft.apim-gateway",
                _ => "builtin.openai.compatible"
            };

    }
}
