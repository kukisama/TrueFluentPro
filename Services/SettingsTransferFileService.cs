using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

public sealed class SettingsTransferFileService : ISettingsTransferFileService
{
    private static readonly JsonSerializerOptions TransferJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public Task<string?> ExportBasicAiConfigAsync(IStorageProvider provider, SettingsTransferPackage package, CancellationToken cancellationToken = default)
        => ExportAsync(
            provider,
            package,
            title: "导出基本AI配置",
            suggestedFileName: $"truefluentpro-basic-ai-config-{DateTime.Now:yyyyMMdd-HHmmss}",
            cancellationToken);

    public Task<string?> ExportFullConfigAsync(IStorageProvider provider, AzureSpeechConfig config, CancellationToken cancellationToken = default)
        => ExportAsync(
            provider,
            config,
            title: "导出完整配置",
            suggestedFileName: $"truefluentpro-full-config-{DateTime.Now:yyyyMMdd-HHmmss}",
            cancellationToken);

    private static async Task<string?> ExportAsync<T>(
        IStorageProvider provider,
        T data,
        string title,
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var targetFile = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON 文件")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        if (targetFile == null)
        {
            return null;
        }

        await using var stream = await targetFile.OpenWriteAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await JsonSerializer.SerializeAsync(stream, data, TransferJsonOptions, cancellationToken);

        return targetFile.TryGetLocalPath() ?? targetFile.Name ?? "已选文件";
    }

    public async Task<SettingsTransferImportResult?> ImportAsync(IStorageProvider provider, CancellationToken cancellationToken = default)
    {
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 文件")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        if (files == null || files.Count == 0)
        {
            return null;
        }

        var selectedFile = files[0];
        var localPath = selectedFile.TryGetLocalPath();
        string json;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            json = await File.ReadAllTextAsync(localPath, cancellationToken);
        }
        else
        {
            await using var stream = await selectedFile.OpenReadAsync();
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("文件内容为空或格式不正确。");
        }

        var displayName = selectedFile.TryGetLocalPath() ?? selectedFile.Name ?? "所选文件";
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
            var package = JsonSerializer.Deserialize<SettingsTransferPackage>(json, TransferJsonOptions)
                ?? throw new InvalidOperationException("基本AI配置文件内容为空或格式不正确。");

            return new SettingsTransferImportResult(
                SettingsTransferImportKind.BasicAiConfig,
                displayName,
                BasicPackage: package);
        }

        if (!TryGetPropertyIgnoreCase(root, "Subscriptions", out _)
            && !TryGetPropertyIgnoreCase(root, "AiConfig", out _)
            && !TryGetPropertyIgnoreCase(root, "SourceLanguage", out _)
            && !TryGetPropertyIgnoreCase(root, "TargetLanguage", out _))
        {
            throw new InvalidOperationException("无法识别导入文件格式。请导入“基本AI配置”或“完整配置”导出的 JSON 文件。");
        }

        var fullConfig = JsonSerializer.Deserialize<AzureSpeechConfig>(json, TransferJsonOptions)
            ?? throw new InvalidOperationException("完整配置文件内容为空或格式不正确。");

        return new SettingsTransferImportResult(
            SettingsTransferImportKind.FullConfig,
            displayName,
            FullConfig: fullConfig);
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
