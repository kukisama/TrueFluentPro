using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public class ConfigurationService
    {
        private readonly string _configFilePath;

        public ConfigurationService()
        {
            _configFilePath = PathManager.Instance.ConfigFilePath;
        }

        public async Task<AzureSpeechConfig> LoadConfigAsync()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = await File.ReadAllTextAsync(_configFilePath);
                    var config = JsonSerializer.Deserialize<AzureSpeechConfig>(json);
                    if (config != null)
                    {
                        PathManager.Instance.SetSessionsPath(config.SessionDirectoryOverride);
                        MigrateEndpoints(config);
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载配置失败: {ex.Message}");
            }

            var defaultConfig = new AzureSpeechConfig();
            PathManager.Instance.SetSessionsPath(defaultConfig.SessionDirectoryOverride);
            await SaveConfigAsync(defaultConfig);
            return defaultConfig;
        }

        public async Task SaveConfigAsync(AzureSpeechConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var json = JsonSerializer.Serialize(config, options);
                await File.WriteAllTextAsync(_configFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
                throw;
            }
        }

        public string GetConfigFilePath()
        {
            return _configFilePath;
        }

        /// <summary>
        /// 将旧配置中分散的终结点字段迁移到统一的 Endpoints 注册表。
        /// 仅在 Endpoints 为空且旧字段非空时执行一次。
        /// </summary>
        internal static void MigrateEndpoints(AzureSpeechConfig config)
        {
            if (config.Endpoints.Count > 0)
                return; // 已有终结点，无需迁移

            var endpoints = new List<AiEndpoint>();

            // --- 迁移 AiConfig（文字对话终结点）---
            var ai = config.AiConfig;
            if (ai != null && !string.IsNullOrWhiteSpace(ai.ApiEndpoint))
            {
                var ep = new AiEndpoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "默认文字",
                    IsEnabled = true,
                    ProviderType = ai.ProviderType,
                    BaseUrl = ai.ApiEndpoint,
                    ApiKey = ai.ApiKey,
                    ApiVersion = ai.ApiVersion,
                    AuthMode = ai.AzureAuthMode,
                    AzureTenantId = ai.AzureTenantId,
                    AzureClientId = ai.AzureClientId,
                };

                // 收集所有非空模型名
                var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { ai.ModelName, ai.SummaryModelName, ai.QuickModelName })
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        modelNames.Add(name);
                }

                foreach (var name in modelNames)
                {
                    ep.Models.Add(new AiModelEntry
                    {
                        ModelId = name,
                        DisplayName = name,
                        Capabilities = ModelCapability.Text
                    });
                }

                if (ep.Models.Count > 0)
                {
                    endpoints.Add(ep);

                    // 生成 ModelReference 指向迁移后的终结点+模型
                    var insightModel = !string.IsNullOrWhiteSpace(ai.ModelName) ? ai.ModelName : ep.Models[0].ModelId;
                    ai.InsightModelRef = new ModelReference { EndpointId = ep.Id, ModelId = insightModel };
                    ai.ReviewModelRef = new ModelReference { EndpointId = ep.Id, ModelId = insightModel };

                    var summaryModel = !string.IsNullOrWhiteSpace(ai.SummaryModelName) ? ai.SummaryModelName : insightModel;
                    ai.SummaryModelRef = new ModelReference { EndpointId = ep.Id, ModelId = summaryModel };

                    var quickModel = !string.IsNullOrWhiteSpace(ai.QuickModelName) ? ai.QuickModelName : insightModel;
                    ai.QuickModelRef = new ModelReference { EndpointId = ep.Id, ModelId = quickModel };
                }
            }

            // --- 迁移 MediaGenConfig（图片终结点）---
            var media = config.MediaGenConfig;
            if (!string.IsNullOrWhiteSpace(media.ImageApiEndpoint))
            {
                // 检查是否与已有终结点 URL 相同，可合并
                var existing = endpoints.FirstOrDefault(e =>
                    string.Equals(e.BaseUrl, media.ImageApiEndpoint, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    // 合并：在已有终结点上添加图片模型
                    if (!string.IsNullOrWhiteSpace(media.ImageModel) &&
                        !existing.Models.Any(m => m.ModelId == media.ImageModel))
                    {
                        existing.Models.Add(new AiModelEntry
                        {
                            ModelId = media.ImageModel,
                            DisplayName = media.ImageModel,
                            Capabilities = ModelCapability.Image
                        });
                    }
                    media.ImageModelRef = new ModelReference { EndpointId = existing.Id, ModelId = media.ImageModel };
                }
                else
                {
                    var ep = new AiEndpoint
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "默认图片",
                        IsEnabled = true,
                        ProviderType = media.ImageProviderType,
                        BaseUrl = media.ImageApiEndpoint,
                        ApiKey = media.ImageApiKey,
                        AuthMode = media.ImageAzureAuthMode,
                        AzureTenantId = media.ImageAzureTenantId,
                        AzureClientId = media.ImageAzureClientId,
                    };

                    if (!string.IsNullOrWhiteSpace(media.ImageModel))
                    {
                        ep.Models.Add(new AiModelEntry
                        {
                            ModelId = media.ImageModel,
                            DisplayName = media.ImageModel,
                            Capabilities = ModelCapability.Image
                        });
                    }

                    endpoints.Add(ep);
                    media.ImageModelRef = new ModelReference { EndpointId = ep.Id, ModelId = media.ImageModel };
                }
            }

            // --- 迁移 MediaGenConfig（视频终结点）---
            var videoUrl = media.VideoUseImageEndpoint ? media.ImageApiEndpoint : media.VideoApiEndpoint;
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                var existing = endpoints.FirstOrDefault(e =>
                    string.Equals(e.BaseUrl, videoUrl, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    if (!string.IsNullOrWhiteSpace(media.VideoModel) &&
                        !existing.Models.Any(m => m.ModelId == media.VideoModel))
                    {
                        existing.Models.Add(new AiModelEntry
                        {
                            ModelId = media.VideoModel,
                            DisplayName = media.VideoModel,
                            Capabilities = ModelCapability.Video
                        });
                    }
                    media.VideoModelRef = new ModelReference { EndpointId = existing.Id, ModelId = media.VideoModel };
                }
                else
                {
                    var videoKey = media.VideoUseImageEndpoint ? media.ImageApiKey : media.VideoApiKey;
                    var videoProvider = media.VideoUseImageEndpoint ? media.ImageProviderType : media.VideoProviderType;
                    var videoAuth = media.VideoUseImageEndpoint ? media.ImageAzureAuthMode : media.VideoAzureAuthMode;
                    var videoTenant = media.VideoUseImageEndpoint ? media.ImageAzureTenantId : media.VideoAzureTenantId;
                    var videoClient = media.VideoUseImageEndpoint ? media.ImageAzureClientId : media.VideoAzureClientId;

                    var ep = new AiEndpoint
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "默认视频",
                        IsEnabled = true,
                        ProviderType = videoProvider,
                        BaseUrl = videoUrl,
                        ApiKey = videoKey,
                        AuthMode = videoAuth,
                        AzureTenantId = videoTenant,
                        AzureClientId = videoClient,
                    };

                    if (!string.IsNullOrWhiteSpace(media.VideoModel))
                    {
                        ep.Models.Add(new AiModelEntry
                        {
                            ModelId = media.VideoModel,
                            DisplayName = media.VideoModel,
                            Capabilities = ModelCapability.Video
                        });
                    }

                    endpoints.Add(ep);
                    media.VideoModelRef = new ModelReference { EndpointId = ep.Id, ModelId = media.VideoModel };
                }
            }

            config.Endpoints = endpoints;
        }
    }
}
