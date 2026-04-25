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
    /// 支持 defaults + 模型覆盖模式：共性属性写在 defaults，特定模型只写差异项，
    /// 模型级显式值优先，缺失项从 defaults 继承。
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
                    var raw = JsonSerializer.Deserialize<ImageModelsConfig>(stream);
                    if (raw is { Models.Count: > 0 })
                    {
                        if (raw.Defaults.HasValue && raw.Defaults.Value.ValueKind == JsonValueKind.Object)
                        {
                            raw.Models = MergeDefaultsIntoModels(raw.Defaults.Value, raw.Models);
                        }
                        return raw;
                    }
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

        /// <summary>
        /// 将 defaults 与每个模型的显式定义合并：模型显式值优先，缺失项从 defaults 继承。
        /// 使用 JSON 层面的属性合并，避免 C# 属性默认值干扰。
        /// </summary>
        private static List<ImageModelCapabilities> MergeDefaultsIntoModels(
            JsonElement defaults, List<ImageModelCapabilities> rawModels)
        {
            var result = new List<ImageModelCapabilities>(rawModels.Count);

            // 将 defaults 序列化为 Dictionary 以做属性级合并
            var defaultProps = new Dictionary<string, JsonElement>();
            foreach (var prop in defaults.EnumerateObject())
                defaultProps[prop.Name] = prop.Value.Clone();

            foreach (var model in rawModels)
            {
                // 将模型重新序列化为 JSON 获取显式声明的属性
                var modelJson = JsonSerializer.SerializeToUtf8Bytes(model);
                using var modelDoc = JsonDocument.Parse(modelJson);
                var modelProps = new Dictionary<string, JsonElement>();
                foreach (var prop in modelDoc.RootElement.EnumerateObject())
                    modelProps[prop.Name] = prop.Value.Clone();

                // 合并：defaults 为底，模型覆盖；嵌套对象（如 billing）做深度合并
                var merged = new Dictionary<string, JsonElement>();

                foreach (var kvp in defaultProps)
                    merged[kvp.Key] = kvp.Value;

                foreach (var kvp in modelProps)
                {
                    if (kvp.Value.ValueKind == JsonValueKind.Object
                        && merged.TryGetValue(kvp.Key, out var existing)
                        && existing.ValueKind == JsonValueKind.Object)
                    {
                        // 深度合并嵌套对象
                        merged[kvp.Key] = MergeJsonObjects(existing, kvp.Value);
                    }
                    else
                    {
                        merged[kvp.Key] = kvp.Value;
                    }
                }

                var mergedJson = JsonSerializer.SerializeToUtf8Bytes(merged);
                var caps = JsonSerializer.Deserialize<ImageModelCapabilities>(mergedJson);
                if (caps != null)
                    result.Add(caps);
            }

            return result;
        }

        /// <summary>深度合并两个 JSON 对象：base 为底，overlay 覆盖。</summary>
        private static JsonElement MergeJsonObjects(JsonElement baseObj, JsonElement overlay)
        {
            var merged = new Dictionary<string, JsonElement>();
            foreach (var prop in baseObj.EnumerateObject())
                merged[prop.Name] = prop.Value.Clone();
            foreach (var prop in overlay.EnumerateObject())
                merged[prop.Name] = prop.Value.Clone();

            var bytes = JsonSerializer.SerializeToUtf8Bytes(merged);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
    }
}
