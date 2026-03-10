using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    /// <summary>模型能力标签（可组合）</summary>
    [Flags]
    public enum ModelCapability
    {
        None = 0,
        Text = 1,              // 文字对话：洞察、复盘、快问
        Image = 2,             // 图片生成
        Video = 4,             // 视频生成
        SpeechToText = 8,      // 语音转文字 / 音频转写
        TextToSpeech = 16      // 文字转语音 / 语音合成
    }

    /// <summary>一个 AI 终结点（Provider 实例）</summary>
    public class AiEndpoint : ObservableObject
    {
        private string _id = "";
        private string _profileId = "";
        private string _name = "";
        private bool _isEnabled = true;
        private EndpointApiType _endpointType = EndpointApiType.OpenAiCompatible;
        private AiProviderType _providerType = AiProviderType.OpenAiCompatible;
        private string _baseUrl = "";
        private string _apiKey = "";
        private string _apiVersion = "";
        private AzureAuthMode _authMode = AzureAuthMode.ApiKey;
        private ApiKeyHeaderMode _apiKeyHeaderMode = ApiKeyHeaderMode.Auto;
        private TextApiProtocolMode _textApiProtocolMode = TextApiProtocolMode.Auto;
        private ImageApiRouteMode _imageApiRouteMode = ImageApiRouteMode.Auto;
        private string _azureTenantId = "";
        private string _azureClientId = "";
        private List<AiModelEntry> _models = new();

        public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string ProfileId { get => _profileId; set => SetProperty(ref _profileId, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

        public EndpointApiType EndpointType
        {
            get => _endpointType;
            set
            {
                if (SetProperty(ref _endpointType, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                    OnPropertyChanged(nameof(EndpointTypeDisplayName));
                    OnPropertyChanged(nameof(EndpointTypeGlyph));
                    OnPropertyChanged(nameof(EndpointTypeMonogram));
                    OnPropertyChanged(nameof(EndpointTypeBadgeBackground));
                    OnPropertyChanged(nameof(EndpointTypeSubtitle));
                    OnPropertyChanged(nameof(EndpointTypeIconAssetPath));
                }
            }
        }

        // --- 连接信息 ---
        public AiProviderType ProviderType
        {
            get => _providerType;
            set
            {
                if (SetProperty(ref _providerType, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                }
            }
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                if (SetProperty(ref _baseUrl, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                }
            }
        }

        public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
        public string ApiVersion { get => _apiVersion; set => SetProperty(ref _apiVersion, value); }

        // --- 认证 ---
        public AzureAuthMode AuthMode
        {
            get => _authMode;
            set
            {
                if (SetProperty(ref _authMode, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                }
            }
        }

        public ApiKeyHeaderMode ApiKeyHeaderMode
        {
            get => _apiKeyHeaderMode;
            set => SetProperty(ref _apiKeyHeaderMode, value);
        }

        public TextApiProtocolMode TextApiProtocolMode
        {
            get => _textApiProtocolMode;
            set => SetProperty(ref _textApiProtocolMode, value);
        }

        public ImageApiRouteMode ImageApiRouteMode
        {
            get => _imageApiRouteMode;
            set => SetProperty(ref _imageApiRouteMode, value);
        }

        public string AzureTenantId { get => _azureTenantId; set => SetProperty(ref _azureTenantId, value); }
        public string AzureClientId { get => _azureClientId; set => SetProperty(ref _azureClientId, value); }

        /// <summary>
        /// 是否为 Azure OpenAI 终结点。
        /// 仅由终结点类型决定，不再基于域名或 ProviderType 猜测。
        /// </summary>
        public bool IsAzureEndpoint => EndpointType == EndpointApiType.AzureOpenAi;

        public string EndpointTypeDisplayName => EndpointType switch
        {
            EndpointApiType.AzureOpenAi => "Azure OpenAI",
            EndpointApiType.ApiManagementGateway => "APIM 网关",
            _ => "OpenAI Compatible"
        };

        public string EndpointTypeGlyph => EndpointType switch
        {
            EndpointApiType.AzureOpenAi => "☰",
            EndpointApiType.ApiManagementGateway => "⇆",
            _ => "✦"
        };

        public string EndpointTypeMonogram => EndpointType switch
        {
            EndpointApiType.AzureOpenAi => "AZ",
            EndpointApiType.ApiManagementGateway => "AP",
            _ => "OA"
        };

        public string EndpointTypeBadgeBackground => EndpointType switch
        {
            EndpointApiType.AzureOpenAi => "#0078D4",
            EndpointApiType.ApiManagementGateway => "#6D28D9",
            _ => "#10A37F"
        };

        public string EndpointTypeSubtitle => EndpointType switch
        {
            EndpointApiType.AzureOpenAi => "官方 Azure OpenAI / Foundry",
            EndpointApiType.ApiManagementGateway => "Azure API Management 网关",
            _ => "标准 OpenAI / 兼容服务"
        };

        public string EndpointTypeIconAssetPath => EndpointType switch
        {
            EndpointApiType.AzureOpenAi => "/Assets/EndpointProfiles/Icons/azure-openai.svg",
            EndpointApiType.ApiManagementGateway => "/Assets/EndpointProfiles/Icons/apim-gateway.svg",
            _ => "/Assets/EndpointProfiles/Icons/openai-compatible.svg"
        };

        // --- 模型列表 ---
        public List<AiModelEntry> Models { get => _models; set => SetProperty(ref _models, value); }
    }

    /// <summary>终结点下的一个模型定义</summary>
    public class AiModelEntry : ObservableObject
    {
        private string _modelId = "";
        private string _displayName = "";
        private string _deploymentName = "";
        private string _groupName = "";
        private ModelCapability _capabilities = ModelCapability.None;

        public string ModelId
        {
            get => _modelId;
            set
            {
                if (SetProperty(ref _modelId, value))
                {
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
        public string DeploymentName { get => _deploymentName; set => SetProperty(ref _deploymentName, value); }
        public string GroupName { get => _groupName; set => SetProperty(ref _groupName, value); }
        public ModelCapability Capabilities { get => _capabilities; set => SetProperty(ref _capabilities, value); }

        public string DisplayTitle => string.IsNullOrWhiteSpace(ModelId) ? "未命名模型" : ModelId;
    }

    /// <summary>功能分区对某个模型的引用（终结点ID + 模型ID）</summary>
    public class ModelReference
    {
        public string EndpointId { get; set; } = "";
        public string ModelId { get; set; } = "";
    }

    /// <summary>下拉框中的模型选项，显示 "终结点名 / 模型名"</summary>
    public class ModelOption
    {
        public ModelReference Reference { get; init; } = new();
        public string EndpointName { get; init; } = "";
        public string ModelDisplayName { get; init; } = "";
        public override string ToString() => $"{EndpointName} / {ModelDisplayName}";
    }
}
