using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels.Settings;

public class TransferSectionVM : ViewModelBase
{
    private static readonly JsonSerializerOptions ClipboardJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ISettingsTransferFileService _transferFileService;
    private readonly Func<SettingsTransferPackage> _createBasicExportPackage;
    private readonly Func<AzureSpeechConfig> _createFullExportConfig;
    private readonly Func<SettingsTransferPackage, Task> _importPackageAsync;
    private readonly Func<AzureSpeechConfig, Task> _importFullConfigAsync;
    private readonly Action<string> _reportStatus;
    private bool _isBusy;

    public TransferSectionVM(
        ISettingsTransferFileService transferFileService,
        Func<SettingsTransferPackage> createBasicExportPackage,
        Func<AzureSpeechConfig> createFullExportConfig,
        Func<SettingsTransferPackage, Task> importPackageAsync,
        Func<AzureSpeechConfig, Task> importFullConfigAsync,
        Action<string> reportStatus)
    {
        _transferFileService = transferFileService;
        _createBasicExportPackage = createBasicExportPackage;
        _createFullExportConfig = createFullExportConfig;
        _importPackageAsync = importPackageAsync;
        _importFullConfigAsync = importFullConfigAsync;
        _reportStatus = reportStatus;
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public void ReportStorageProviderUnavailable(bool isImport)
    {
        _reportStatus(isImport ? "导入失败：无法获取文件选择能力" : "导出失败：无法获取文件保存能力");
    }

    public async Task ExportBasicAiConfigAsync(IStorageProvider provider)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var package = _createBasicExportPackage();
            var filePath = await _transferFileService.ExportBasicAiConfigAsync(provider, package);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            _reportStatus($"基本AI配置已导出：{filePath}");
        }
        catch (Exception ex)
        {
            _reportStatus($"导出基本AI配置失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportFullConfigAsync(IStorageProvider provider)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var config = _createFullExportConfig();
            var filePath = await _transferFileService.ExportFullConfigAsync(provider, config);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            _reportStatus($"完整配置已导出：{filePath}");
        }
        catch (Exception ex)
        {
            _reportStatus($"导出完整配置失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportAsync(IStorageProvider provider)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _transferFileService.ImportAsync(provider);
            if (result == null)
            {
                return;
            }

            switch (result.Kind)
            {
                case SettingsTransferImportKind.BasicAiConfig when result.BasicPackage != null:
                    await _importPackageAsync(result.BasicPackage);
                    _reportStatus(BuildBasicImportSuccessMessage(result.DisplayName, result.BasicPackage));
                    break;
                case SettingsTransferImportKind.FullConfig when result.FullConfig != null:
                    await _importFullConfigAsync(result.FullConfig);
                    _reportStatus($"已导入完整配置：{result.DisplayName}。AAD 相关字段与 AAD 认证端点已自动忽略。");
                    break;
                default:
                    throw new InvalidOperationException("无法识别导入文件格式。");
            }
        }
        catch (Exception ex)
        {
            _reportStatus($"导入失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildBasicImportSuccessMessage(string displayName, SettingsTransferPackage package)
    {
        return package.Version switch
        {
            1 => $"已导入 v1 旧版资源级配置：{displayName}。废弃字段已自动忽略，当前支持的终结点与模型引用已生效。",
            2 => $"已导入 v2 资源级配置：{displayName}。当前终结点类型、模型清单与模型引用已生效。",
            _ => $"已导入 v{package.Version} 资源级配置：{displayName}。"
        };
    }

    public void ReportClipboardUnavailable()
    {
        _reportStatus("操作失败：无法访问系统剪贴板");
    }

    public async Task CopyBasicAiConfigToClipboardAsync(IClipboard clipboard)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var package = _createBasicExportPackage();
            var json = JsonSerializer.Serialize(package, ClipboardJsonOptions);
            await clipboard.SetTextAsync(json);
            _reportStatus("基本AI配置已复制到剪贴板");
        }
        catch (Exception ex)
        {
            _reportStatus($"复制到剪贴板失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportFromClipboardAsync(IClipboard clipboard)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var json = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                _reportStatus("剪贴板为空或不包含文本内容。");
                return;
            }

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (TryGetPropertyIgnoreCase(root, "Format", out var formatElement)
                && formatElement.ValueKind == JsonValueKind.String
                && string.Equals(formatElement.GetString(), "TrueFluentPro.ResourceConfig", StringComparison.OrdinalIgnoreCase))
            {
                var package = JsonSerializer.Deserialize<SettingsTransferPackage>(json, ClipboardJsonOptions)
                    ?? throw new InvalidOperationException("基本AI配置内容为空或格式不正确。");

                await _importPackageAsync(package);
                _reportStatus(BuildBasicImportSuccessMessage("剪贴板", package));
                return;
            }

            if (!TryGetPropertyIgnoreCase(root, "Subscriptions", out _)
                && !TryGetPropertyIgnoreCase(root, "AiConfig", out _)
                && !TryGetPropertyIgnoreCase(root, "SourceLanguage", out _)
                && !TryGetPropertyIgnoreCase(root, "TargetLanguage", out _))
            {
                _reportStatus("无法识别剪贴板中的配置格式。请确保剪贴板包含有效的配置 JSON。");
                return;
            }

            var fullConfig = JsonSerializer.Deserialize<AzureSpeechConfig>(json, ClipboardJsonOptions)
                ?? throw new InvalidOperationException("完整配置内容为空或格式不正确。");

            await _importFullConfigAsync(fullConfig);
            _reportStatus("已从剪贴板导入完整配置。AAD 相关字段与 AAD 认证端点已自动忽略。");
        }
        catch (JsonException)
        {
            _reportStatus("剪贴板内容不是有效的 JSON 格式。");
        }
        catch (Exception ex)
        {
            _reportStatus($"从剪贴板导入失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
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
}
