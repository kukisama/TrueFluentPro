using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 图片模型能力目录：加载 image-models.json，按 modelId 查询能力声明。
    /// </summary>
    public sealed class ImageModelCatalogService
    {
        private Dictionary<string, ImageModelCapabilities> _models = new(StringComparer.OrdinalIgnoreCase);

        public void Load()
        {
            var config = LoadConfig();
            _models = config.Models.ToDictionary(m => m.ModelId, m => m, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 按 modelId 精确或前缀匹配查询能力声明。
        /// 返回 null 表示未知模型（使用默认行为）。
        /// </summary>
        public ImageModelCapabilities? GetCapabilities(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            // 精确匹配
            if (_models.TryGetValue(modelId, out var caps))
                return caps;

            // 前缀匹配（如 gpt-image-2-2026-04-21 → gpt-image-2）
            foreach (var kvp in _models)
            {
                if (modelId.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// 获取所有已注册模型 ID。
        /// </summary>
        public IReadOnlyCollection<string> GetRegisteredModelIds() => _models.Keys;

        private static ImageModelsConfig LoadConfig()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("image-models.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    var config = JsonSerializer.Deserialize<ImageModelsConfig>(stream);
                    if (config is { Models.Count: > 0 }) return config;
                }
            }

            // 最小化 fallback
            return new ImageModelsConfig
            {
                Models = new()
                {
                    new ImageModelCapabilities
                    {
                        ModelId = "gpt-image-2",
                        Vendor = "openai",
                        ResolutionMode = ResolutionMode.FreeForm,
                        SupportsTransparentBackground = false,
                        SupportsInputFidelity = false,
                        RequiresDeploymentHeader = true,
                    },
                    new ImageModelCapabilities
                    {
                        ModelId = "gpt-image-1.5",
                        Vendor = "openai",
                        ResolutionMode = ResolutionMode.Fixed,
                        FixedSizes = { "1024x1024", "1536x1024", "1024x1536" },
                        SupportsTransparentBackground = true,
                        SupportsInputFidelity = true,
                        RequiresDeploymentHeader = true,
                    }
                }
            };
        }
    }
}
