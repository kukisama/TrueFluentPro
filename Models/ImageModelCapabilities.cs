using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    /// <summary>分辨率模式</summary>
    public enum ResolutionMode
    {
        /// <summary>固定几档尺寸（如 gpt-image-1.5）</summary>
        Fixed,
        /// <summary>自由分辨率，遵循约束（如 gpt-image-2）</summary>
        FreeForm
    }

    /// <summary>Token 计算方式</summary>
    public enum TokenCalculationMode
    {
        /// <summary>固定阶梯：quality+size→固定 token 数（gpt-image-1.5 及更早）</summary>
        FixedLadder,
        /// <summary>动态计算：quality+size→动态 token 数（gpt-image-2）</summary>
        DynamicCalculator
    }

    /// <summary>
    /// 图片模型的能力声明——纯数据，不含逻辑。
    /// Pipeline 各 Step 根据 Capabilities 动态决策。
    /// </summary>
    public sealed class ImageModelCapabilities
    {
        // ── 身份 ──
        [JsonPropertyName("modelId")]
        public required string ModelId { get; init; }

        [JsonPropertyName("vendor")]
        public string Vendor { get; init; } = "openai";

        [JsonPropertyName("snapshotVersion")]
        public string? SnapshotVersion { get; init; }

        // ── 模态 ──
        [JsonPropertyName("supportsTextOutput")]
        public bool SupportsTextOutput { get; init; }

        // ── 生成能力 ──
        [JsonPropertyName("supportsGeneration")]
        public bool SupportsGeneration { get; init; } = true;

        [JsonPropertyName("supportsEditing")]
        public bool SupportsEditing { get; init; } = true;

        [JsonPropertyName("supportsMask")]
        public bool SupportsMask { get; init; } = true;

        [JsonPropertyName("supportsMultiReference")]
        public bool SupportsMultiReference { get; init; } = true;

        // ── 分辨率 ──
        [JsonPropertyName("resolutionMode")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ResolutionMode ResolutionMode { get; init; }

        [JsonPropertyName("fixedSizes")]
        public List<string> FixedSizes { get; init; } = new();

        [JsonPropertyName("freeFormConstraints")]
        public ResolutionConstraints? FreeFormConstraints { get; init; }

        // ── 质量 ──
        [JsonPropertyName("qualityOptions")]
        public List<string> QualityOptions { get; init; } = new() { "low", "medium", "high" };

        // ── 格式 ──
        [JsonPropertyName("outputFormats")]
        public List<string> OutputFormats { get; init; } = new() { "png", "jpeg", "webp" };

        [JsonPropertyName("supportsTransparentBackground")]
        public bool SupportsTransparentBackground { get; init; }

        // ── 高级 ──
        [JsonPropertyName("supportsInputFidelity")]
        public bool SupportsInputFidelity { get; init; }

        [JsonPropertyName("defaultInputFidelity")]
        public string DefaultInputFidelity { get; init; } = "high";

        [JsonPropertyName("supportsStreaming")]
        public bool SupportsStreaming { get; init; } = true;

        [JsonPropertyName("maxPartialImages")]
        public int MaxPartialImages { get; init; } = 3;

        // ── API 端点 ──
        [JsonPropertyName("supportsImageApi")]
        public bool SupportsImageApi { get; init; } = true;

        [JsonPropertyName("supportsResponsesApi")]
        public bool SupportsResponsesApi { get; init; } = true;

        [JsonPropertyName("requiresDeploymentHeader")]
        public bool RequiresDeploymentHeader { get; init; }

        // ── 计费 ──
        [JsonPropertyName("billing")]
        public ImageBillingModel Billing { get; init; } = new();
    }

    /// <summary>
    /// 计费模型描述。
    /// </summary>
    public sealed class ImageBillingModel
    {
        [JsonPropertyName("supportsTokenBilling")]
        public bool SupportsTokenBilling { get; init; } = true;

        [JsonPropertyName("supportsPerImageBilling")]
        public bool SupportsPerImageBilling { get; init; }

        [JsonPropertyName("tokenCalcMode")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TokenCalculationMode TokenCalcMode { get; init; }

        [JsonPropertyName("imageOutputTokenPrice")]
        public double ImageOutputTokenPrice { get; init; }

        [JsonPropertyName("textInputTokenPrice")]
        public double TextInputTokenPrice { get; init; }

        [JsonPropertyName("imageInputTokenPrice")]
        public double ImageInputTokenPrice { get; init; }
    }

    /// <summary>
    /// FreeForm 分辨率约束（如 gpt-image-2）。
    /// </summary>
    public sealed class ResolutionConstraints
    {
        [JsonPropertyName("maxEdge")]
        public int MaxEdge { get; init; } = 3840;

        [JsonPropertyName("edgeMultiple")]
        public int EdgeMultiple { get; init; } = 16;

        [JsonPropertyName("maxAspectRatio")]
        public double MaxAspectRatio { get; init; } = 3.0;

        [JsonPropertyName("minPixels")]
        public int MinPixels { get; init; } = 655_360;

        [JsonPropertyName("maxPixels")]
        public int MaxPixels { get; init; } = 8_294_400;
    }

    /// <summary>
    /// image-models.json 根结构。
    /// </summary>
    public sealed class ImageModelsConfig
    {
        [JsonPropertyName("models")]
        public List<ImageModelCapabilities> Models { get; set; } = new();
    }
}
