using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
        private static readonly SemaphoreSlim SaveGate = new(1, 1);
        // 按对象身份跟踪，不能按值匹配：导入/新建对象的首次保存仍是完整替换。
        private static readonly ConditionalWeakTable<AzureSpeechConfig, VoiceImeLanguageBaseline> VoiceImeBaselines = new();
        private sealed record VoiceImeLanguageBaseline(
            string FilePath, string? RecognitionLanguage, bool EnableTranslation, string? TargetLanguage);

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
                defaultConfig.EnsureSpeechSubscriptionsMigratedToEndpoints();
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
                    config.EnsureSpeechSubscriptionsMigratedToEndpoints();
                    config.EnsureEndpointVendorCredentialsMigrated();
                    PathManager.Instance.SetSessionsPath(config.SessionDirectoryOverride);
                    RememberVoiceImeLanguages(config, config.VoiceIme);
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
                    backupConfig.EnsureSpeechSubscriptionsMigratedToEndpoints();
                    backupConfig.EnsureEndpointVendorCredentialsMigrated();
                    System.Diagnostics.Debug.WriteLine($"主配置加载失败，已回退到备份配置: {_backupConfigFilePath}");
                    PathManager.Instance.SetSessionsPath(backupConfig.SessionDirectoryOverride);
                    LastLoadReport = new ConfigurationLoadReport
                    {
                        UsedFallbackConfig = true,
                        WarningMessage = BuildLoadFailureWarning(timestampBackupPath, _backupConfigFilePath),
                        InvalidConfigBackupPath = timestampBackupPath,
                        RecoverySourcePath = _backupConfigFilePath
                    };
                    RememberVoiceImeLanguages(backupConfig, backupConfig.VoiceIme);
                    return backupConfig;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载备份配置失败: {ex.Message}");
            }

            var fallbackDefaultConfig = new AzureSpeechConfig();
            fallbackDefaultConfig.EnsureSpeechResourcesBackfilledFromLegacy();
            fallbackDefaultConfig.EnsureSpeechSubscriptionsMigratedToEndpoints();
            PathManager.Instance.SetSessionsPath(fallbackDefaultConfig.SessionDirectoryOverride);
            LastLoadReport = new ConfigurationLoadReport
            {
                UsedFallbackConfig = true,
                WarningMessage = BuildLoadFailureWarning(timestampBackupPath, null),
                InvalidConfigBackupPath = timestampBackupPath
            };
            RememberVoiceImeLanguages(fallbackDefaultConfig, fallbackDefaultConfig.VoiceIme);
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
            await SaveGate.WaitAsync();
            try
            {
                config.EnsureSpeechResourcesBackfilledFromLegacy();
                config.EnsureSpeechSubscriptionsMigratedToEndpoints();
                config.EnsureEndpointVendorCredentialsMigrated();
                Directory.CreateDirectory(Path.GetDirectoryName(_configFilePath)!);

                var tempFilePath = _configFilePath + ".tmp";
                var options = CreateSerializerOptions(writeIndented: true);
                var voiceIme = config.VoiceIme;
                var snapshot = JsonSerializer.SerializeToNode(config, options)!;
                var savedLanguages = snapshot[nameof(AzureSpeechConfig.VoiceIme)]?.Deserialize<VoiceImeConfig>(options);
                var originalLanguages = savedLanguages == null ? null : CaptureVoiceImeLanguages(savedLanguages);
                if (savedLanguages != null &&
                    VoiceImeBaselines.TryGetValue(config, out var baseline) && baseline.FilePath == _configFilePath)
                {
                    var latest = await TryLoadVoiceImeLanguagesAsync();
                    if (latest != null)
                    {
                        if (savedLanguages.RecognitionLanguage == baseline.RecognitionLanguage)
                            savedLanguages.RecognitionLanguage = latest.RecognitionLanguage;
                        if (savedLanguages.EnableTranslation == baseline.EnableTranslation)
                            savedLanguages.EnableTranslation = latest.EnableTranslation;
                        if (savedLanguages.TargetLanguage == baseline.TargetLanguage)
                            savedLanguages.TargetLanguage = latest.TargetLanguage;

                        var output = snapshot[nameof(AzureSpeechConfig.VoiceIme)]!;
                        output[nameof(VoiceImeConfig.RecognitionLanguage)] = savedLanguages.RecognitionLanguage;
                        output[nameof(VoiceImeConfig.EnableTranslation)] = savedLanguages.EnableTranslation;
                        output[nameof(VoiceImeConfig.TargetLanguage)] = savedLanguages.TargetLanguage;
                    }
                }

                var json = snapshot.ToJsonString(options);
                await File.WriteAllTextAsync(tempFilePath, json);

                if (File.Exists(_configFilePath))
                {
                    File.Copy(_configFilePath, _backupConfigFilePath, overwrite: true);
                }

                File.Move(tempFilePath, _configFilePath, overwrite: true);
                // 主窗/齿轮反复保存同一对象。成功后同步合并值，但不覆盖等待 I/O 期间的新编辑。
                if (voiceIme != null && savedLanguages != null && originalLanguages != null && ReferenceEquals(config.VoiceIme, voiceIme))
                {
                    if (voiceIme.RecognitionLanguage == originalLanguages.RecognitionLanguage)
                        voiceIme.RecognitionLanguage = savedLanguages.RecognitionLanguage;
                    if (voiceIme.EnableTranslation == originalLanguages.EnableTranslation)
                        voiceIme.EnableTranslation = savedLanguages.EnableTranslation;
                    if (voiceIme.TargetLanguage == originalLanguages.TargetLanguage)
                        voiceIme.TargetLanguage = savedLanguages.TargetLanguage;
                }
                RememberVoiceImeLanguages(config, savedLanguages);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
                throw;
            }
            finally
            {
                SaveGate.Release();
            }
        }

        private VoiceImeLanguageBaseline CaptureVoiceImeLanguages(VoiceImeConfig? settings)
        {
            settings ??= new VoiceImeConfig();
            return new(_configFilePath, settings.RecognitionLanguage, settings.EnableTranslation, settings.TargetLanguage);
        }

        private void RememberVoiceImeLanguages(AzureSpeechConfig config, VoiceImeConfig? settings)
        {
            VoiceImeBaselines.Remove(config);
            VoiceImeBaselines.Add(config, CaptureVoiceImeLanguages(settings));
        }

        private async Task<VoiceImeConfig?> TryLoadVoiceImeLanguagesAsync()
        {
            try
            {
                return (await TryLoadConfigAsync(_configFilePath))?.VoiceIme;
            }
            catch (JsonException ex)
            {
                // 保留现有损坏配置的恢复保存语义；I/O 错误仍向上传递，不能悄悄覆盖未读到的值。
                System.Diagnostics.Debug.WriteLine($"无法合并输入法语言，沿用恢复配置: {ex.Message}");
                return null;
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
