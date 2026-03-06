using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface ISettingsImportExportService
    {
        SettingsTransferPackage CreateExportPackage(AzureSpeechConfig config);
        AzureSpeechConfig ApplyImportPackage(AzureSpeechConfig currentConfig, SettingsTransferPackage package);
    }
}
