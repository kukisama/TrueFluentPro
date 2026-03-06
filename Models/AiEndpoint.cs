using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    /// <summary>模型能力标签（可组合）</summary>
    [Flags]
    public enum ModelCapability
    {
        Text  = 1,   // 文字对话：洞察、复盘、快问
        Image = 2,   // 图片生成
        Video = 4    // 视频生成
    }

    /// <summary>一个 AI 终结点（Provider 实例）</summary>
    public class AiEndpoint : ObservableObject
    {
        private static bool LooksLikeAzureOpenAiEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)
                || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host;
            return host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
        }

        private string _id = "";
        private string _name = "";
        private bool _isEnabled = true;
        private AiProviderType _providerType = AiProviderType.OpenAiCompatible;
        private string _baseUrl = "";
        private string _apiKey = "";
        private string _apiVersion = "";
        private AzureAuthMode _authMode = AzureAuthMode.ApiKey;
        private string _azureTenantId = "";
        private string _azureClientId = "";
        private List<AiModelEntry> _models = new();

        public string Id { get => _id; set => SetProperty(ref _id, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

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

        public string AzureTenantId { get => _azureTenantId; set => SetProperty(ref _azureTenantId, value); }
        public string AzureClientId { get => _azureClientId; set => SetProperty(ref _azureClientId, value); }

        /// <summary>
        /// 是否为 Azure OpenAI 终结点。
        /// ProviderType、AAD 认证，或官方 Azure OpenAI 域名（*.openai.azure.com / *.services.ai.azure.com）
        /// 任一命中都视为 Azure，避免 API Key 模式下仅因 ProviderType 未正确设置而误走 OpenAI Compatible 认证。
        /// </summary>
        public bool IsAzureEndpoint => ProviderType == AiProviderType.AzureOpenAi
                           || AuthMode == AzureAuthMode.AAD
                           || LooksLikeAzureOpenAiEndpoint(BaseUrl);

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
        private ModelCapability _capabilities;

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
