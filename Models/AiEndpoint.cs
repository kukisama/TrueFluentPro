using System;
using System.Collections.Generic;

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
    public class AiEndpoint
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public bool IsEnabled { get; set; } = true;

        // --- 连接信息 ---
        public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ApiVersion { get; set; } = "";

        // --- 认证 ---
        public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ApiKey;
        public string AzureTenantId { get; set; } = "";
        public string AzureClientId { get; set; } = "";

        // --- 模型列表 ---
        public List<AiModelEntry> Models { get; set; } = new();
    }

    /// <summary>终结点下的一个模型定义</summary>
    public class AiModelEntry
    {
        public string ModelId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DeploymentName { get; set; } = "";
        public string GroupName { get; set; } = "";
        public ModelCapability Capabilities { get; set; }
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
