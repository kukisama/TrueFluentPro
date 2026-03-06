using System;
using System.Collections.Generic;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 设置页资源配置导入/导出包（仅包含可迁移、可复用的资源项）。
    /// 不包含本机个性化细项，也不包含 AAD 登录相关字段与 AAD 认证端点。
    /// </summary>
    public class SettingsTransferPackage
    {
        public string Format { get; set; } = "TrueFluentPro.ResourceConfig";
        public int Version { get; set; } = 1;
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
        public string Description { get; set; } = "仅包含 Azure Speech、存储、API Key 型 AI 终结点与模型选择；不包含 AAD 登录字段、AAD 认证端点和细微个性化配置。";
        public TransferSpeechConfig Speech { get; set; } = new();
        public TransferStorageConfig Storage { get; set; } = new();
        public List<TransferAiEndpoint> Endpoints { get; set; } = new();
        public TransferModelSelections ModelSelections { get; set; } = new();
    }

    public class TransferSpeechConfig
    {
        public List<TransferSpeechSubscription> Subscriptions { get; set; } = new();
        public int ActiveSubscriptionIndex { get; set; }
    }

    public class TransferSpeechSubscription
    {
        public string Name { get; set; } = "";
        public string SubscriptionKey { get; set; } = "";
        public string ServiceRegion { get; set; } = "southeastasia";
        public string Endpoint { get; set; } = "";
    }

    public class TransferStorageConfig
    {
        public string BatchStorageConnectionString { get; set; } = "";
        public string BatchAudioContainerName { get; set; } = AzureSpeechConfig.DefaultBatchAudioContainerName;
        public string BatchResultContainerName { get; set; } = AzureSpeechConfig.DefaultBatchResultContainerName;
    }

    public class TransferAiEndpoint
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ApiVersion { get; set; } = "";
        public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ApiKey;
        public List<TransferAiModel> Models { get; set; } = new();
    }

    public class TransferAiModel
    {
        public string ModelId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DeploymentName { get; set; } = "";
        public string GroupName { get; set; } = "";
        public ModelCapability Capabilities { get; set; } = ModelCapability.Text;
    }

    public class TransferModelSelections
    {
        public ModelReference? InsightModelRef { get; set; }
        public ModelReference? SummaryModelRef { get; set; }
        public ModelReference? QuickModelRef { get; set; }
        public ModelReference? ReviewModelRef { get; set; }
        public ModelReference? ImageModelRef { get; set; }
        public ModelReference? VideoModelRef { get; set; }
    }
}
