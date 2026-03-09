using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public sealed class ConfigurationLoadReport
    {
        public bool UsedFallbackConfig { get; init; }
        public bool CreatedDefaultConfig { get; init; }
        public string? WarningMessage { get; init; }
        public string? InvalidConfigBackupPath { get; init; }
        public string? RecoverySourcePath { get; init; }
    }

    public sealed class ShellStartupPreferences
    {
        public ThemeModePreference ThemeMode { get; init; } = ThemeModePreference.System;
        public bool IsMainNavPaneOpen { get; init; }
    }

    public class ConfigurationService
    {
        private readonly string _configFilePath;
        private readonly string _backupConfigFilePath;
        public ConfigurationLoadReport? LastLoadReport { get; private set; }

        public ConfigurationService()
        {
            _configFilePath = PathManager.Instance.ConfigFilePath;
            _backupConfigFilePath = _configFilePath + ".bak";
        }

        public async Task<AzureSpeechConfig> LoadConfigAsync()
        {
            LastLoadReport = null;

            if (!File.Exists(_configFilePath))
            {
                var defaultConfig = new AzureSpeechConfig();
                defaultConfig.EnsureSpeechResourcesBackfilledFromLegacy();
                PathManager.Instance.SetSessionsPath(defaultConfig.SessionDirectoryOverride);
                await SaveConfigAsync(defaultConfig);
                LastLoadReport = new ConfigurationLoadReport
                {
                    CreatedDefaultConfig = true,
                    WarningMessage = "首次启动，已创建新的默认配置文件。"
                };
                return defaultConfig;
            }

            string? timestampBackupPath = null;
            try
            {
                var config = await TryLoadConfigAsync(_configFilePath);
                if (config != null)
                {
                    config.EnsureSpeechResourcesBackfilledFromLegacy();
                    PathManager.Instance.SetSessionsPath(config.SessionDirectoryOverride);
                    return config;
                }

                System.Diagnostics.Debug.WriteLine($"主配置文件解析结果为空，将尝试备份配置: {_configFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载主配置失败: {ex.Message}");
                timestampBackupPath = TryCreateTimestampedBackup(_configFilePath);
            }

            try
            {
                var backupConfig = await TryLoadConfigAsync(_backupConfigFilePath);
                if (backupConfig != null)
                {
                    backupConfig.EnsureSpeechResourcesBackfilledFromLegacy();
                    System.Diagnostics.Debug.WriteLine($"主配置加载失败，已回退到备份配置: {_backupConfigFilePath}");
                    PathManager.Instance.SetSessionsPath(backupConfig.SessionDirectoryOverride);
                    LastLoadReport = new ConfigurationLoadReport
                    {
                        UsedFallbackConfig = true,
                        WarningMessage = BuildLoadFailureWarning(timestampBackupPath, _backupConfigFilePath),
                        InvalidConfigBackupPath = timestampBackupPath,
                        RecoverySourcePath = _backupConfigFilePath
                    };
                    return backupConfig;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载备份配置失败: {ex.Message}");
            }

            var fallbackDefaultConfig = new AzureSpeechConfig();
            fallbackDefaultConfig.EnsureSpeechResourcesBackfilledFromLegacy();
            PathManager.Instance.SetSessionsPath(fallbackDefaultConfig.SessionDirectoryOverride);
            LastLoadReport = new ConfigurationLoadReport
            {
                UsedFallbackConfig = true,
                WarningMessage = BuildLoadFailureWarning(timestampBackupPath, null),
                InvalidConfigBackupPath = timestampBackupPath
            };
            return fallbackDefaultConfig;
        }

        public ShellStartupPreferences LoadShellStartupPreferences()
        {
            var defaultPreferences = new ShellStartupPreferences();

            return TryLoadShellStartupPreferences(_configFilePath)
                ?? TryLoadShellStartupPreferences(_backupConfigFilePath)
                ?? defaultPreferences;
        }

        public async Task SaveConfigAsync(AzureSpeechConfig config)
        {
            try
            {
                config.EnsureSpeechResourcesBackfilledFromLegacy();
                Directory.CreateDirectory(Path.GetDirectoryName(_configFilePath)!);

                var tempFilePath = _configFilePath + ".tmp";
                var json = JsonSerializer.Serialize(config, CreateSerializerOptions(writeIndented: true));
                await File.WriteAllTextAsync(tempFilePath, json);

                if (File.Exists(_configFilePath))
                {
                    File.Copy(_configFilePath, _backupConfigFilePath, overwrite: true);
                }

                File.Move(tempFilePath, _configFilePath, overwrite: true);
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

        private static async Task<AzureSpeechConfig?> TryLoadConfigAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AzureSpeechConfig>(json, CreateSerializerOptions(writeIndented: false));
        }

        private static ShellStartupPreferences? TryLoadShellStartupPreferences(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                var root = document.RootElement;
                var themeMode = ThemeModePreference.System;
                var isMainNavPaneOpen = false;

                if (TryGetPropertyIgnoreCase(root, "ThemeMode", out var themeElement) && themeElement.ValueKind == JsonValueKind.String)
                {
                    var themeRaw = themeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(themeRaw) && Enum.TryParse<ThemeModePreference>(themeRaw, ignoreCase: true, out var parsedTheme))
                    {
                        themeMode = parsedTheme;
                    }
                }

                if (TryGetPropertyIgnoreCase(root, "IsMainNavPaneOpen", out var navElement) &&
                    (navElement.ValueKind == JsonValueKind.True || navElement.ValueKind == JsonValueKind.False))
                {
                    isMainNavPaneOpen = navElement.GetBoolean();
                }

                return new ShellStartupPreferences
                {
                    ThemeMode = themeMode,
                    IsMainNavPaneOpen = isMainNavPaneOpen
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取启动壳层偏好失败: {ex.Message}");
                return null;
            }
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static JsonSerializerOptions CreateSerializerOptions(bool writeIndented)
        {
            return new JsonSerializerOptions
            {
                WriteIndented = writeIndented,
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        private static string? TryCreateTimestampedBackup(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                var directory = Path.GetDirectoryName(filePath);
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                var extension = Path.GetExtension(filePath);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupPath = Path.Combine(directory!, $"{fileNameWithoutExtension}.load-failed.{timestamp}{extension}");
                File.Copy(filePath, backupPath, overwrite: false);
                return backupPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建时间戳备份失败: {ex.Message}");
                return null;
            }
        }

        private static string BuildLoadFailureWarning(string? timestampBackupPath, string? recoverySourcePath)
        {
            var backupNote = string.IsNullOrWhiteSpace(timestampBackupPath)
                ? ""
                : $" 已按时间戳备份原配置：{timestampBackupPath}";

            var recoveryNote = string.IsNullOrWhiteSpace(recoverySourcePath)
                ? " 当前已回退到默认配置，请检查配置文件内容，必要时更新到新版本后重试。"
                : $" 当前已从备份配置恢复：{recoverySourcePath}。请检查主配置文件内容，必要时更新到新版本后重试。";

            return "警告：检测到配置文件可能损坏或版本不兼容，加载主配置失败。" + backupNote + recoveryNote;
        }
    }
}
