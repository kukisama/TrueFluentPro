using System.Windows.Input;

namespace TrueFluentPro.ViewModels
{
    public partial class MainWindowViewModel
    {
        public AiInsightViewModel AiInsight { get; }
        public ICommand ToggleTranslationCommand { get; }
        public ICommand RefreshAudioDevicesCommand { get; }
        public ICommand RefreshAudioLibraryCommand { get; }
        public ICommand PlayAudioCommand { get; }
        public ICommand PauseAudioCommand { get; }
        public ICommand StopAudioCommand { get; }
        public ICommand StartTranslationCommand { get; }
        public ICommand StopTranslationCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand ShowConfigCommand { get; }
        public ICommand OpenHistoryFolderCommand { get; }
        public ICommand ShowFloatingSubtitlesCommand { get; }
        public ICommand ToggleEditorTypeCommand { get; }
        public ICommand OpenAzureSpeechPortalCommand { get; }
        public ICommand OpenFoundryPortalCommand { get; }
        public ICommand OpenProjectGitHubCommand { get; }
        public ICommand ShowAboutCommand { get; }
        public ICommand ShowHelpCommand { get; }
        public string AppVersion { get; }
        public ICommand GenerateReviewSummaryCommand { get; }
        public ICommand GenerateAllReviewSheetsCommand { get; }
        public ICommand ReviewMarkdownLinkCommand { get; }
        public ICommand LoadBatchTasksCommand { get; }
        public ICommand ClearBatchTasksCommand { get; }
        public ICommand StartBatchCommand { get; }
        public ICommand StopBatchCommand { get; }
        public ICommand RefreshBatchQueueCommand { get; }
        public ICommand CancelBatchQueueItemCommand { get; }
        public ICommand EnqueueSubtitleReviewCommand { get; }
        public ICommand GenerateSpeechSubtitleCommand { get; } = null!;
        public ICommand CancelSpeechSubtitleCommand { get; } = null!;
        public ICommand GenerateBatchSpeechSubtitleCommand { get; } = null!;
        public ICommand ShowMediaStudioCommand { get; }
    }
}
