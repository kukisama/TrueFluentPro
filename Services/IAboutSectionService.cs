using System.ComponentModel;
using System.Threading.Tasks;

namespace TrueFluentPro.Services;

public interface IAboutSectionService : INotifyPropertyChanged
{
    string AppVersion { get; }
    bool IsUpdateAvailable { get; }
    string UpdateVersionText { get; }
    bool IsDownloading { get; }
    double DownloadProgress { get; }

    Task ShowAboutAsync(System.Action<string>? reportStatus = null);
    Task ShowHelpAsync(System.Action<string>? reportStatus = null);
    void OpenAzureSpeechPortal(System.Action<string>? reportStatus = null);
    void Open21vAzureSpeechPortal(System.Action<string>? reportStatus = null);
    void OpenStoragePortal(System.Action<string>? reportStatus = null);
    void Open21vStoragePortal(System.Action<string>? reportStatus = null);
    void OpenFoundryPortal(System.Action<string>? reportStatus = null);
    void OpenProjectGitHub(System.Action<string>? reportStatus = null);
    Task CheckForUpdateAsync(bool silent, bool isAutoUpdateEnabled, System.Action<string>? reportStatus = null);
    Task DownloadAndApplyUpdateAsync(System.Action<string>? reportStatus = null);
}
