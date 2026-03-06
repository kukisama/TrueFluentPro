using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public class SettingsImportExportService : ISettingsImportExportService
    {
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
                    Subscriptions = config.Subscriptions.Select(subscription => new TransferSpeechSubscription
                    {
                        Name = subscription.Name,
                        SubscriptionKey = subscription.SubscriptionKey,
                        ServiceRegion = subscription.ServiceRegion,
                        Endpoint = subscription.Endpoint
                    }).ToList()
                },
                Storage = new TransferStorageConfig
                {
                    BatchStorageConnectionString = config.BatchStorageConnectionString,
                    BatchAudioContainerName = config.BatchAudioContainerName,
                    BatchResultContainerName = config.BatchResultContainerName
                },
                Endpoints = exportableEndpoints.Select(endpoint => new TransferAiEndpoint
                {
                    Id = endpoint.Id,
                    Name = endpoint.Name,
                    IsEnabled = endpoint.IsEnabled,
                    ProviderType = endpoint.ProviderType,
                    BaseUrl = endpoint.BaseUrl,
                    ApiKey = endpoint.ApiKey,
                    ApiVersion = endpoint.ApiVersion,
                    AuthMode = endpoint.AuthMode,
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
                    VideoModelRef = CloneReference(media.VideoModelRef)
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

            if (package.Storage != null)
            {
                result.BatchStorageConnectionString = package.Storage.BatchStorageConnectionString?.Trim() ?? "";
                result.BatchAudioContainerName = string.IsNullOrWhiteSpace(package.Storage.BatchAudioContainerName)
                    ? AzureSpeechConfig.DefaultBatchAudioContainerName
                    : package.Storage.BatchAudioContainerName.Trim();
                result.BatchResultContainerName = string.IsNullOrWhiteSpace(package.Storage.BatchResultContainerName)
                    ? AzureSpeechConfig.DefaultBatchResultContainerName
                    : package.Storage.BatchResultContainerName.Trim();
                var hasImportedStorage = !string.IsNullOrWhiteSpace(result.BatchStorageConnectionString);
                result.BatchStorageIsValid = hasImportedStorage;
                result.UseSpeechSubtitleForReview = hasImportedStorage;
            }
            else
            {
                result.BatchStorageConnectionString = "";
                result.BatchStorageIsValid = false;
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

            if (package.Version != 1)
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
                    Name = endpoint.Name?.Trim() ?? "",
                    IsEnabled = endpoint.IsEnabled,
                    ProviderType = endpoint.ProviderType,
                    BaseUrl = endpoint.BaseUrl?.Trim() ?? "",
                    ApiKey = endpoint.ApiKey?.Trim() ?? "",
                    ApiVersion = endpoint.ApiVersion?.Trim() ?? "",
                    AuthMode = endpoint.AuthMode,
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

        private static bool IsReferenceValid(AzureSpeechConfig config, ModelReference? reference, ModelCapability capability)
        {
            if (reference == null)
            {
                return false;
            }

            var resolved = config.ResolveModel(reference);
            return resolved.Endpoint != null
                   && resolved.Model != null
                   && resolved.Model.Capabilities.HasFlag(capability);
        }

        private static bool IsImportableEndpoint(AiEndpoint endpoint)
            => endpoint.AuthMode != AzureAuthMode.AAD;

        private static bool IsImportableEndpoint(TransferAiEndpoint endpoint)
            => endpoint.AuthMode != AzureAuthMode.AAD;

    }
}
