using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface ISettingsImportExportService
    {
        AzureSpeechConfig CreateFullExportConfig(AzureSpeechConfig config);
        AzureSpeechConfig NormalizeImportedFullConfig(AzureSpeechConfig config);
        SettingsTransferPackage CreateExportPackage(AzureSpeechConfig config);
        AzureSpeechConfig ApplyImportPackage(AzureSpeechConfig currentConfig, SettingsTransferPackage package);
    }
}
