using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

public interface ISettingsTransferFileService
{
    Task<string?> ExportBasicAiConfigAsync(IStorageProvider provider, SettingsTransferPackage package, CancellationToken cancellationToken = default);
    Task<string?> ExportFullConfigAsync(IStorageProvider provider, AzureSpeechConfig config, CancellationToken cancellationToken = default);
    Task<SettingsTransferImportResult?> ImportAsync(IStorageProvider provider, CancellationToken cancellationToken = default);
}

public enum SettingsTransferImportKind
{
    BasicAiConfig,
    FullConfig
}

public sealed record SettingsTransferImportResult(
    SettingsTransferImportKind Kind,
    string DisplayName,
    SettingsTransferPackage? BasicPackage = null,
    AzureSpeechConfig? FullConfig = null);
