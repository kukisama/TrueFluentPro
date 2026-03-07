using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public sealed class ModelRuntimeResolution
    {
        public AiEndpoint Endpoint { get; init; } = null!;
        public AiModelEntry Model { get; init; } = null!;
        public ModelCapability Capability { get; init; }

        public string EndpointId => Endpoint.Id;
        public string ProfileId => Endpoint.ProfileId ?? "";
        public string EndpointName => Endpoint.Name;
        public string ModelId => Model.ModelId;
        public string DisplayName => string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.ModelId : Model.DisplayName;
        public string ApiEndpoint => Endpoint.BaseUrl?.Trim() ?? "";
        public string ApiKey => Endpoint.ApiKey?.Trim() ?? "";
        public string ApiVersion => Endpoint.ApiVersion?.Trim() ?? "";
        public EndpointApiType EndpointType => Endpoint.EndpointType;
        public AiProviderType ProviderType => Endpoint.ProviderType;
        public AzureAuthMode AzureAuthMode => Endpoint.AuthMode;
        public ApiKeyHeaderMode ApiKeyHeaderMode => Endpoint.ApiKeyHeaderMode;
        public TextApiProtocolMode TextApiProtocolMode => Endpoint.TextApiProtocolMode;
        public ImageApiRouteMode ImageApiRouteMode => Endpoint.ImageApiRouteMode;
        public string AzureTenantId => Endpoint.AzureTenantId ?? "";
        public string AzureClientId => Endpoint.AzureClientId ?? "";
        public bool IsAzureEndpoint => Endpoint.IsAzureEndpoint;
        public string EffectiveDeploymentName => string.IsNullOrWhiteSpace(Model.DeploymentName) ? Model.ModelId : Model.DeploymentName;
        public string ProfileKey => $"endpoint_{Endpoint.Id}";

        public AiChatRequestConfig CreateChatRequest(bool summaryEnableReasoning = false)
        {
            return new AiChatRequestConfig
            {
                ProfileId = ProfileId,
                EndpointType = EndpointType,
                ProviderType = ProviderType,
                ApiEndpoint = ApiEndpoint,
                ApiKey = ApiKey,
                ApiVersion = ApiVersion,
                AzureAuthMode = AzureAuthMode,
                ApiKeyHeaderMode = ApiKeyHeaderMode,
                TextApiProtocolMode = TextApiProtocolMode,
                ImageApiRouteMode = ImageApiRouteMode,
                AzureTenantId = AzureTenantId,
                AzureClientId = AzureClientId,
                IsAzureEndpoint = IsAzureEndpoint,
                ModelName = ModelId,
                DeploymentName = EffectiveDeploymentName,
                SummaryEnableReasoning = summaryEnableReasoning
            };
        }

        public AiConfig CreateRequestConfig(bool summaryEnableReasoning = false)
        {
            var config = new AiConfig
            {
                ProfileId = ProfileId,
                EndpointType = EndpointType,
                ProviderType = ProviderType,
                ApiEndpoint = ApiEndpoint,
                ApiKey = ApiKey,
                ApiVersion = ApiVersion,
                AzureAuthMode = AzureAuthMode,
                ApiKeyHeaderMode = ApiKeyHeaderMode,
                TextApiProtocolMode = TextApiProtocolMode,
                ImageApiRouteMode = ImageApiRouteMode,
                AzureTenantId = AzureTenantId,
                AzureClientId = AzureClientId,
                DeploymentName = EffectiveDeploymentName,
                SummaryEnableReasoning = summaryEnableReasoning
            };

            if (IsAzureEndpoint)
            {
                config.DeploymentName = EffectiveDeploymentName;
            }
            else
            {
                config.ModelName = ModelId;
            }

            return config;
        }
    }

    public interface IModelRuntimeResolver
    {
        bool TryResolve(
            AzureSpeechConfig config,
            ModelReference? reference,
            ModelCapability capability,
            out ModelRuntimeResolution? runtime,
            out string errorMessage);
    }
}