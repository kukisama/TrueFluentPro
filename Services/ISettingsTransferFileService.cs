using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

public interface ISettingsTransferFileService
{
    Task<string?> ExportAsync(IStorageProvider provider, SettingsTransferPackage package, CancellationToken cancellationToken = default);
    Task<SettingsTransferImportResult?> ImportAsync(IStorageProvider provider, CancellationToken cancellationToken = default);
}

public sealed record SettingsTransferImportResult(SettingsTransferPackage Package, string DisplayName);
