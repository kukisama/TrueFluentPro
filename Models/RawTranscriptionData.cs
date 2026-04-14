using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 转录原始数据包装 — lifecycle DB 中 Transcribed 阶段的新存储格式。
    /// 保存 API 原始响应 JSON 作为数据源，UI 段落按需从 raw + 拆分配置实时计算。
    /// </summary>
    public sealed class RawTranscriptionData
    {
        /// <summary>格式版本，当前为 2（v1 是旧的 TranscriptSegmentDto[] 数组格式）。</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; } = 2;

        /// <summary>API 类型：fast / batch。用于选择解析器。</summary>
        [JsonPropertyName("apiType")]
        public string ApiType { get; set; } = "fast";

        /// <summary>原始 API 响应 JSON 字符串。</summary>
        [JsonPropertyName("rawResponse")]
        public string RawResponse { get; set; } = "";

        /// <summary>转录时使用的 locale（用于后续重新解析时参考）。</summary>
        [JsonPropertyName("locale")]
        public string? Locale { get; set; }
    }
}
