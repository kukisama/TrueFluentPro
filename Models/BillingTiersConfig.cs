using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// billing-tiers.json 根配置。
    /// </summary>
    public sealed class BillingTiersConfig
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("models")]
        public Dictionary<string, BillingTierModel> Models { get; set; } = new();
    }

    /// <summary>
    /// 单个模型的计费配置。
    /// </summary>
    public sealed class BillingTierModel
    {
        [JsonPropertyName("billingUnit")]
        public string BillingUnit { get; set; } = "token";

        [JsonPropertyName("pricePerMillionOutputTokens")]
        public double PricePerMillionOutputTokens { get; set; }

        [JsonPropertyName("pricePerMillionInputTokens")]
        public double PricePerMillionInputTokens { get; set; }

        [JsonPropertyName("pricePerMillionImageInputTokens")]
        public double PricePerMillionImageInputTokens { get; set; }

        [JsonPropertyName("tiers")]
        public List<BillingTier> Tiers { get; set; } = new();
    }

    /// <summary>
    /// 单个计费档位。
    /// </summary>
    public sealed class BillingTier
    {
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("quality")]
        public string Quality { get; set; } = "medium";

        [JsonPropertyName("tokens")]
        public int Tokens { get; set; }

        /// <summary>按张固定价（仅 gpt-image-1.5 等双轨模型有值）</summary>
        [JsonPropertyName("priceUsd")]
        public double? PriceUsd { get; set; }

        /// <summary>像素面积（运行时填充）</summary>
        [JsonIgnore]
        public long PixelArea => (long)Width * Height;
    }
}
