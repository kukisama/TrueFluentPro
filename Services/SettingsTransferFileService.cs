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

    public async Task<string?> ExportAsync(IStorageProvider provider, SettingsTransferPackage package, CancellationToken cancellationToken = default)
    {
        var targetFile = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出资源配置",
            SuggestedFileName = $"truefluentpro-resource-config-{DateTime.Now:yyyyMMdd-HHmmss}",
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
        await JsonSerializer.SerializeAsync(stream, package, TransferJsonOptions, cancellationToken);

        return targetFile.TryGetLocalPath() ?? targetFile.Name ?? "已选文件";
    }

    public async Task<SettingsTransferImportResult?> ImportAsync(IStorageProvider provider, CancellationToken cancellationToken = default)
    {
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入资源配置",
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
        SettingsTransferPackage? package;
        var localPath = selectedFile.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            var json = await File.ReadAllTextAsync(localPath, cancellationToken);
            package = JsonSerializer.Deserialize<SettingsTransferPackage>(json, TransferJsonOptions);
        }
        else
        {
            await using var stream = await selectedFile.OpenReadAsync();
            package = await JsonSerializer.DeserializeAsync<SettingsTransferPackage>(stream, TransferJsonOptions, cancellationToken);
        }

        if (package == null)
        {
            throw new InvalidOperationException("文件内容为空或格式不正确。");
        }

        var displayName = selectedFile.TryGetLocalPath() ?? selectedFile.Name ?? "所选文件";
        return new SettingsTransferImportResult(package, displayName);
    }
}
