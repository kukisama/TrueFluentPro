using System;
using System.Collections.Generic;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 设置页资源配置导入/导出包（仅包含可迁移、可复用的资源项）。
    /// 包含所有终结点类型（含 AAD 和 Speech），但 AAD 登录凭据已清除，仅保留空壳结构。
    /// </summary>
    public class SettingsTransferPackage
    {
        public string Format { get; set; } = "TrueFluentPro.ResourceConfig";
        public int Version { get; set; } = 2;
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
        public string Description { get; set; } = "包含所有 AI 终结点（含 AAD 空壳与 Speech）、模型清单与模型引用、Azure Speech 资源与存储配置；AAD 登录凭据已自动清除。";
        public TransferSpeechConfig Speech { get; set; } = new();
        public TransferStorageConfig Storage { get; set; } = new();
        public List<TransferAiEndpoint> Endpoints { get; set; } = new();
        public TransferModelSelections ModelSelections { get; set; } = new();
    }

    public class TransferSpeechConfig
    {
        public List<TransferSpeechSubscription> Subscriptions { get; set; } = new();
        public int ActiveSubscriptionIndex { get; set; }
        public List<TransferSpeechResource> Resources { get; set; } = new();
        public string ActiveSpeechResourceId { get; set; } = "";
    }

    public class TransferSpeechSubscription
    {
        public string Name { get; set; } = "";
        public string SubscriptionKey { get; set; } = "";
        public string ServiceRegion { get; set; } = "southeastasia";
        public string Endpoint { get; set; } = "";
    }

    public class TransferSpeechResource
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public SpeechVendorType Vendor { get; set; } = SpeechVendorType.Microsoft;
        public SpeechConnectorType ConnectorType { get; set; } = SpeechConnectorType.MicrosoftSpeech;
        public bool IsEnabled { get; set; } = true;
        public SpeechCapability Capabilities { get; set; } = SpeechCapability.None;
        public string SubscriptionName { get; set; } = "";
        public string SubscriptionKey { get; set; } = "";
        public string ServiceRegion { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public ModelReference? RealtimeSpeechToTextModelRef { get; set; }
        public ModelReference? BatchSpeechToTextModelRef { get; set; }
        public ModelReference? TextToSpeechModelRef { get; set; }
    }

    public class TransferStorageConfig
    {
        public string BatchStorageConnectionString { get; set; } = "";
        public string BatchAudioContainerName { get; set; } = AzureSpeechConfig.DefaultBatchAudioContainerName;
        public string BatchResultContainerName { get; set; } = AzureSpeechConfig.DefaultBatchResultContainerName;
        public ReviewSubtitleSourceMode ReviewSubtitleSourceMode { get; set; } = ReviewSubtitleSourceMode.DefaultSubtitle;
    }

    public class TransferAiEndpoint
    {
        public string Id { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public EndpointApiType EndpointType { get; set; } = EndpointApiType.OpenAiCompatible;
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ApiVersion { get; set; } = "";
        public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ApiKey;
        public ApiKeyHeaderMode ApiKeyHeaderMode { get; set; } = ApiKeyHeaderMode.Auto;
        public TextApiProtocolMode TextApiProtocolMode { get; set; } = TextApiProtocolMode.Auto;
        public ImageApiRouteMode ImageApiRouteMode { get; set; } = ImageApiRouteMode.Auto;
        public string AzureTenantId { get; set; } = "";
        public string AzureClientId { get; set; } = "";
        public List<TransferAiModel> Models { get; set; } = new();

        // --- Azure Speech 专属字段 ---
        public string SpeechSubscriptionKey { get; set; } = "";
        public string SpeechRegion { get; set; } = "";
        public string SpeechEndpoint { get; set; } = "";
        public SpeechCapability SpeechCapabilities { get; set; } = SpeechCapability.None;
    }

    public class TransferAiModel
    {
        public string ModelId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DeploymentName { get; set; } = "";
        public string GroupName { get; set; } = "";
        public ModelCapability Capabilities { get; set; } = ModelCapability.None;
    }

    public class TransferModelSelections
    {
        public ModelReference? InsightModelRef { get; set; }
        public ModelReference? SummaryModelRef { get; set; }
        public ModelReference? QuickModelRef { get; set; }
        public ModelReference? ReviewModelRef { get; set; }
        public ModelReference? ConversationModelRef { get; set; }
        public ModelReference? IntentModelRef { get; set; }
        public ModelReference? ImageModelRef { get; set; }
        public ModelReference? VideoModelRef { get; set; }
        public ModelReference? RealtimeTranscriptionModelRef { get; set; }
        public ModelReference? BatchTranscriptionModelRef { get; set; }
        public ModelReference? TextToSpeechModelRef { get; set; }
    }
}
