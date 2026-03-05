using System.Windows.Input;

namespace TrueFluentPro.ViewModels
{
    public partial class MainWindowViewModel
    {
        public PlaybackViewModel Playback { get; }
        public AiInsightViewModel AiInsight { get; }
        public ICommand ToggleTranslationCommand { get; }
        public ICommand StartTranslationCommand { get; }
        public ICommand StopTranslationCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand ShowConfigCommand { get; }
        public ICommand OpenHistoryFolderCommand { get; }
        public ICommand ShowFloatingSubtitlesCommand { get; }
        public ICommand ShowFloatingInsightCommand { get; }
        public ICommand ToggleEditorTypeCommand { get; }
        public ICommand OpenAzureSpeechPortalCommand { get; }
        public ICommand Open21vAzureSpeechPortalCommand { get; }
        public ICommand OpenStoragePortalCommand { get; }
        public ICommand Open21vStoragePortalCommand { get; }
        public ICommand OpenFoundryPortalCommand { get; }
        public ICommand OpenProjectGitHubCommand { get; }
        public ICommand ShowAboutCommand { get; }
        public ICommand ShowHelpCommand { get; }
        public string AppVersion { get; }
        public ICommand ShowMediaStudioCommand { get; }
        public ICommand CheckForUpdateCommand { get; }
        public ICommand DownloadAndApplyUpdateCommand { get; }
    }
}
