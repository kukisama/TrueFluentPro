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
    }
}
