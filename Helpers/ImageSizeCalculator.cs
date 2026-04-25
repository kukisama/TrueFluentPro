using System;
using System.Collections.Generic;

namespace TrueFluentPro.Helpers
{
    /// <summary>
    /// gpt-image-2 尺寸约束工具。纯静态方法，零依赖。
    /// 
    /// 官方约束（OpenAI 2026-04 文档）:
    ///   - 两边都必须是 16px 的倍数
    ///   - 单边最大 3840px
    ///   - 长边 / 短边 ≤ 3:1
    ///   - 总像素 ≥ 655,360 且 ≤ 8,294,400
    ///   - 总像素 > 3,686,400 被标记为实验性（2K+）
    /// </summary>
    public static class ImageSizeCalculator
    {
        public const int GridUnit = 16;
        public const int MaxEdge = 3840;
        public const int MinPixels = 655_360;
        public const int MaxPixels = 8_294_400;
        public const int ExperimentalPixelThreshold = 3_686_400;
        public const double MaxAspectRatio = 3.0;

        /// <summary>计算结果</summary>
        public record struct SizeResult(int Width, int Height)
        {
            public int TotalPixels => Width * Height;
            public bool IsExperimental => TotalPixels > ExperimentalPixelThreshold;
            public string ToSizeString() => $"{Width}x{Height}";
        }

        /// <summary>
        /// 将任意宽高对齐到最近的 16px 倍数，并约束在 gpt-image-2 合法范围内。
        /// 保持原始宽高比。
        /// </summary>
        public static SizeResult AlignToGrid(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return new SizeResult(1024, 1024);

            var w = SnapTo16(width);
            var h = SnapTo16(height);

            // 约束单边上限
            if (w > MaxEdge || h > MaxEdge)
            {
                var scale = Math.Min((double)MaxEdge / w, (double)MaxEdge / h);
                w = SnapTo16((int)(w * scale));
                h = SnapTo16((int)(h * scale));
            }

            // 约束宽高比 ≤ 3:1
            var longEdge = Math.Max(w, h);
            var shortEdge = Math.Min(w, h);
            if (shortEdge > 0 && (double)longEdge / shortEdge > MaxAspectRatio)
            {
                if (w > h)
                    h = SnapTo16((int)(w / MaxAspectRatio));
                else
                    w = SnapTo16((int)(h / MaxAspectRatio));
            }

            // 约束总像素上限
            var totalPx = (long)w * h;
            if (totalPx > MaxPixels)
            {
                var scale = Math.Sqrt((double)MaxPixels / totalPx);
                w = SnapTo16((int)(w * scale));
                h = SnapTo16((int)(h * scale));
            }

            // 约束总像素下限（等比放大）
            totalPx = (long)w * h;
            if (totalPx < MinPixels)
            {
                var scale = Math.Sqrt((double)MinPixels / totalPx);
                w = SnapTo16((int)Math.Ceiling(w * scale));
                h = SnapTo16((int)Math.Ceiling(h * scale));
            }

            // 最终安全保底
            w = Clamp16(w);
            h = Clamp16(h);

            return new SizeResult(w, h);
        }

        /// <summary>
        /// 校验给定尺寸是否满足 gpt-image-2 约束。返回 null 表示合法，否则返回原因。
        /// </summary>
        public static string? Validate(int width, int height)
        {
            if (width % GridUnit != 0 || height % GridUnit != 0)
                return $"宽高必须是 {GridUnit}px 的倍数";
            if (width > MaxEdge || height > MaxEdge)
                return $"单边最大 {MaxEdge}px";
            if (width <= 0 || height <= 0)
                return "宽高必须大于 0";

            var longEdge = Math.Max(width, height);
            var shortEdge = Math.Min(width, height);
            if (shortEdge > 0 && (double)longEdge / shortEdge > MaxAspectRatio)
                return $"宽高比不能超过 {MaxAspectRatio}:1";

            var totalPx = (long)width * height;
            if (totalPx < MinPixels)
                return $"总像素至少 {MinPixels:N0}（当前 {totalPx:N0}）";
            if (totalPx > MaxPixels)
                return $"总像素最多 {MaxPixels:N0}（当前 {totalPx:N0}）";

            return null;
        }

        /// <summary>
        /// 解析 "WxH" 格式字符串。失败返回 false。
        /// </summary>
        public static bool TryParse(string sizeString, out int width, out int height)
        {
            width = height = 0;
            if (string.IsNullOrWhiteSpace(sizeString)) return false;

            var parts = sizeString.Split('x', 'X', '×');
            return parts.Length == 2
                && int.TryParse(parts[0].Trim(), out width)
                && int.TryParse(parts[1].Trim(), out height)
                && width > 0 && height > 0;
        }

        /// <summary>就近对齐到 16 的倍数</summary>
        public static int SnapTo16(int value)
        {
            if (value <= 0) return GridUnit;
            return ((value + GridUnit / 2) / GridUnit) * GridUnit;
        }

        /// <summary>对齐并限制在合法单边范围内</summary>
        private static int Clamp16(int value)
        {
            var snapped = SnapTo16(value);
            return Math.Clamp(snapped, GridUnit, MaxEdge);
        }

        /// <summary>
        /// 常见预设尺寸列表（生成模式可选）
        /// </summary>
        /// <summary>核心预设（下拉框默认项，不会被清理）</summary>
        public static IReadOnlyList<string> Presets { get; } = new[]
        {
            "auto",
            "1024x1024",   // 1:1  —  1 MP
            "1536x1024",   // 3:2  横
            "1024x1536",   // 2:3  竖
            "1920x1080",   // 16:9 1080p
            "2048x1152",   // 16:9 2K 横
            "1152x2048",   // 9:16 2K 竖
            "3840x2160",   // 16:9 4K 横
            "2160x3840",   // 9:16 4K 竖
        };

        /// <summary>核心预设集合（O(1) 查找）</summary>
        public static readonly HashSet<string> PresetSet = new(Presets);

        /// <summary>
        /// 估算输出 token 数（用于费用预览）。
        /// 按模型分流：gpt-image-2 和 gpt-image-1.5 使用各自的官方数据点查表，
        /// 自定义尺寸按最近标准档位的像素比例估算。
        /// </summary>
        public static int EstimateOutputTokens(int width, int height, string quality, string? modelId = null)
        {
            var q = NormalizeQuality(quality);
            var sizeKey = $"{width}x{height}";

            if (modelId != null && modelId.StartsWith("gpt-image-1", StringComparison.OrdinalIgnoreCase))
            {
                // gpt-image-1.5 / gpt-image-1: 固定阶梯表（官方精确值）
                if (GptImage15FixedTokens.TryGetValue((q, sizeKey), out var exact15))
                    return exact15;
                // 1.5 只支持固定尺寸，理论上不走这里
                return q switch { "low" => 272, "high" => 4160, _ => 1056 };
            }

            // gpt-image-2 或未知模型: 使用 gpt-image-2 数据点
            if (GptImage2KnownTokens.TryGetValue((q, sizeKey), out var exact2))
                return exact2;

            // 自定义尺寸 fallback: 基于 1024×1024 数据点的像素比例估算
            var totalPx = (long)width * height;
            const double basePx = 1_048_576.0;
            int baseTokens = q switch { "low" => 200, "high" => 7_033, _ => 1_767 };
            return Math.Max(1, (int)(baseTokens * totalPx / basePx));
        }

        /// <summary>
        /// 向后兼容：不传 modelId 时默认按 gpt-image-2 估算。
        /// </summary>
        public static int EstimateOutputTokens(int width, int height, string quality)
            => EstimateOutputTokens(width, height, quality, null);

        private static string NormalizeQuality(string quality)
        {
            var q = quality.ToLowerInvariant();
            return q is "low" or "medium" or "high" ? q : "medium";
        }

        /// <summary>
        /// gpt-image-2 已知数据点（从 OpenAI 定价表 @ $30/M output tokens 反推）。
        /// </summary>
        private static readonly Dictionary<(string quality, string size), int> GptImage2KnownTokens = new()
        {
            [("low",    "1024x1024")] = 200,
            [("low",    "1536x1024")] = 167,
            [("low",    "1024x1536")] = 167,
            [("medium", "1024x1024")] = 1_767,
            [("medium", "1536x1024")] = 1_367,
            [("medium", "1024x1536")] = 1_367,
            [("high",   "1024x1024")] = 7_033,
            [("high",   "1536x1024")] = 5_500,
            [("high",   "1024x1536")] = 5_500,
        };

        /// <summary>
        /// gpt-image-1.5 / gpt-image-1 固定 token 阶梯（OpenAI 官方精确值）。
        /// </summary>
        private static readonly Dictionary<(string quality, string size), int> GptImage15FixedTokens = new()
        {
            [("low",    "1024x1024")] = 272,
            [("low",    "1536x1024")] = 408,
            [("low",    "1024x1536")] = 400,
            [("medium", "1024x1024")] = 1_056,
            [("medium", "1536x1024")] = 1_584,
            [("medium", "1024x1536")] = 1_568,
            [("high",   "1024x1024")] = 4_160,
            [("high",   "1536x1024")] = 6_240,
            [("high",   "1024x1536")] = 6_208,
        };
    }
}
