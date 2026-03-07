using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels.Settings;

public class TransferSectionVM : ViewModelBase
{
    private readonly ISettingsTransferFileService _transferFileService;
    private readonly Func<SettingsTransferPackage> _createExportPackage;
    private readonly Func<SettingsTransferPackage, Task> _importPackageAsync;
    private readonly Action<string> _reportStatus;
    private bool _isBusy;

    public TransferSectionVM(
        ISettingsTransferFileService transferFileService,
        Func<SettingsTransferPackage> createExportPackage,
        Func<SettingsTransferPackage, Task> importPackageAsync,
        Action<string> reportStatus)
    {
        _transferFileService = transferFileService;
        _createExportPackage = createExportPackage;
        _importPackageAsync = importPackageAsync;
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

    public async Task ExportAsync(IStorageProvider provider)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var package = _createExportPackage();
            var filePath = await _transferFileService.ExportAsync(provider, package);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            _reportStatus($"资源配置已导出：{filePath}");
        }
        catch (Exception ex)
        {
            _reportStatus($"导出失败: {ex.Message}");
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

            await _importPackageAsync(result.Package);
            _reportStatus(BuildImportSuccessMessage(result.DisplayName, result.Package));
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

    private static string BuildImportSuccessMessage(string displayName, SettingsTransferPackage package)
    {
        return package.Version switch
        {
            1 => $"已导入 v1 旧版资源级配置：{displayName}。废弃字段已自动忽略，当前支持的终结点与模型引用已生效。",
            2 => $"已导入 v2 资源级配置：{displayName}。当前终结点类型、模型清单与模型引用已生效。",
            _ => $"已导入 v{package.Version} 资源级配置：{displayName}。"
        };
    }
}
