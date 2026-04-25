using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// billing-tiers.json 加载与 Snap-Up 计费查表服务。
    /// 加载优先级：本地缓存 → 内嵌资源。
    /// </summary>
    public sealed class BillingTiersService
    {
        private BillingTiersConfig? _config;
        private static readonly string LocalCachePath = Path.Combine(
            PathManager.Instance.AppDataPath, "billing-tiers.json");

        public BillingTiersConfig Config => _config ?? throw new InvalidOperationException("BillingTiersService 未初始化");

        public void Load()
        {
            // 1. 尝试本地缓存
            if (File.Exists(LocalCachePath))
            {
                try
                {
                    var json = File.ReadAllText(LocalCachePath);
                    var cached = JsonSerializer.Deserialize<BillingTiersConfig>(json);
                    if (cached is { Models.Count: > 0 })
                    {
                        _config = cached;
                        return;
                    }
                }
                catch { /* 缓存损坏，fallback 到内嵌 */ }
            }

            // 2. 内嵌资源
            _config = LoadEmbedded();
        }

        private static BillingTiersConfig LoadEmbedded()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("billing-tiers.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
                return CreateFallbackConfig();

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                return CreateFallbackConfig();

            return JsonSerializer.Deserialize<BillingTiersConfig>(stream) ?? CreateFallbackConfig();
        }

        /// <summary>
        /// Snap-Up: 给定实际尺寸和质量，找到计费用的标准档位。
        /// 在同质量的 tiers 中，找 pixelArea >= actualArea 的最小者。
        /// 超出所有档位，取最大档（保守）。
        /// </summary>
        public BillingTier? SnapUp(string modelId, int actualWidth, int actualHeight, string quality)
        {
            if (_config == null) return null;

            // 模糊匹配模型 key（gpt-image-2-xxx → gpt-image-2）
            var modelKey = FindModelKey(modelId);
            if (modelKey == null || !_config.Models.TryGetValue(modelKey, out var model))
                return null;

            var q = NormalizeQuality(quality);
            var candidates = model.Tiers
                .Where(t => string.Equals(t.Quality, q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.PixelArea)
                .ToList();

            if (candidates.Count == 0) return null;

            long actualArea = (long)actualWidth * actualHeight;
            return candidates.FirstOrDefault(t => t.PixelArea >= actualArea) ?? candidates[^1];
        }

        /// <summary>
        /// 获取模型的输出 token 单价（$/M）。
        /// </summary>
        public double GetOutputTokenPrice(string modelId)
        {
            if (_config == null) return 30.0;
            var modelKey = FindModelKey(modelId);
            if (modelKey != null && _config.Models.TryGetValue(modelKey, out var m))
                return m.PricePerMillionOutputTokens;
            return 30.0;
        }

        /// <summary>
        /// 获取模型的计费单位类型。
        /// </summary>
        public string GetBillingUnit(string modelId)
        {
            if (_config == null) return "token";
            var modelKey = FindModelKey(modelId);
            if (modelKey != null && _config.Models.TryGetValue(modelKey, out var m))
                return m.BillingUnit;
            return "token";
        }

        private string? FindModelKey(string modelId)
        {
            if (_config == null) return null;
            var lower = modelId.ToLowerInvariant();

            // 精确匹配
            if (_config.Models.ContainsKey(lower)) return lower;

            // 前缀匹配（gpt-image-2-2026-04-21 → gpt-image-2）
            foreach (var key in _config.Models.Keys)
            {
                if (lower.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    return key;
            }

            return null;
        }

        private static string NormalizeQuality(string quality)
        {
            var q = quality.ToLowerInvariant();
            return q is "low" or "medium" or "high" ? q : "medium";
        }

        private static BillingTiersConfig CreateFallbackConfig()
        {
            // 最小化硬编码 fallback，确保离线也能运行
            return new BillingTiersConfig
            {
                SchemaVersion = 1,
                Models =
                {
                    ["gpt-image-2"] = new BillingTierModel
                    {
                        PricePerMillionOutputTokens = 30.0,
                        Tiers =
                        {
                            new BillingTier { Width = 1024, Height = 1024, Quality = "low", Tokens = 200 },
                            new BillingTier { Width = 1024, Height = 1024, Quality = "medium", Tokens = 1767 },
                            new BillingTier { Width = 1024, Height = 1024, Quality = "high", Tokens = 7033 },
                        }
                    },
                    ["gpt-image-1.5"] = new BillingTierModel
                    {
                        PricePerMillionOutputTokens = 32.0,
                        Tiers =
                        {
                            new BillingTier { Width = 1024, Height = 1024, Quality = "low", Tokens = 272 },
                            new BillingTier { Width = 1024, Height = 1024, Quality = "medium", Tokens = 1056 },
                            new BillingTier { Width = 1024, Height = 1024, Quality = "high", Tokens = 4160 },
                        }
                    }
                }
            };
        }
    }
}
