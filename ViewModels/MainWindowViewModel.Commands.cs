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
        public ICommand ShowFloatingMicSubtitleCommand { get; }
        public ICommand ShowFloatingLoopbackSubtitleCommand { get; }
        public ICommand ShowFloatingInsightCommand { get; }
        public ICommand ToggleEditorTypeCommand { get; }
        public ICommand ShowMediaStudioCommand { get; }
    }
}
