namespace TrueFluentPro.Models
{
    /// <summary>
    /// 计费审计流水记录（WORM: Write Once Read Many）。
    /// 每次 API 调用完成后写入一条，仅 INSERT，不 UPDATE/DELETE。
    /// </summary>
    public sealed class BillingLedgerEntry
    {
        // ─── 主键 ───
        public required string LedgerId { get; init; }

        // ─── 关联 ───
        public required string SessionId { get; init; }
        public string? TaskId { get; init; }
        public string? MessageId { get; init; }

        // ─── 时间 ───
        public required string EventTime { get; init; }
        public string? ResponseTime { get; init; }
        public required string RecordedAt { get; init; }

        // ─── 操作上下文 ───
        public required string EventType { get; init; }
        public required string ModelId { get; init; }
        public string? ModelSnapshot { get; init; }
        public required string ApiEndpoint { get; init; }
        public string? EndpointProfile { get; init; }

        // ─── 请求参数 ───
        public string? RequestQuality { get; init; }
        public string? RequestSize { get; init; }
        public int RequestN { get; init; } = 1;
        public string? RequestFormat { get; init; }
        public bool HasReferenceInput { get; init; }
        public bool HasMask { get; init; }
        public int PartialImages { get; init; }
        public string? PromptFingerprint { get; init; }

        // ─── 视频专用 ───
        public double? VideoDurationSeconds { get; init; }
        public string? VideoResolution { get; init; }

        // ─── 预估 token ───
        public int? EstimatedOutputTokens { get; init; }

        // ─── 实际 token（API response.usage）───
        public int? ActualInputTokens { get; init; }
        public int? ActualOutputTokens { get; init; }
        public int? ActualImageInputTokens { get; init; }
        public int? ActualImageOutputTokens { get; init; }
        public int? ActualCachedTokens { get; init; }

        // ─── 真实尺寸 ───
        public int? ActualWidth { get; init; }
        public int? ActualHeight { get; init; }
        public int? ActualPixelArea { get; init; }

        // ─── 计费归档（Snap-Up）───
        public int? BillingTierWidth { get; init; }
        public int? BillingTierHeight { get; init; }
        public int? BillingTierTokens { get; init; }
        public double? BillingUnitCostUsd { get; init; }

        // ─── 成本计算 ───
        public double? UnitPriceInputPerM { get; init; }
        public double? UnitPriceOutputPerM { get; init; }
        public double? CalculatedCostUsd { get; init; }

        // ─── 倍率计费（gpt-image-1.5 按张模式）───
        public double? Multiplier { get; init; }
        public double? BaseUnitPrice { get; init; }
        public double? MultiplierCost { get; init; }

        // ─── 结果 ───
        public int ResultCount { get; init; }
        public long? ResultTotalBytes { get; init; }
        public int? HttpStatus { get; init; }
        public string? ErrorCode { get; init; }

        // ─── 配置版本 ───
        public string? BillingConfigVersion { get; init; }

        // ─── 防篡改 ───
        public required string RecordChecksum { get; init; }
    }
}
