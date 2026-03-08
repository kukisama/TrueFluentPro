using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    public partial class MediaFileItem : ObservableObject
    {
        [ObservableProperty]
        private string _name = "";

        [ObservableProperty]
        private string _fullPath = "";

        [ObservableProperty]
        private ProcessingDisplayState _processingState;

        [ObservableProperty]
        private string _processingBadgeText = "";

        [ObservableProperty]
        private string _processingDetailText = "";

        public bool HasProcessingBadge => ProcessingState != ProcessingDisplayState.None;
        public bool IsProcessingRunning => ProcessingState == ProcessingDisplayState.Running;
        public bool IsProcessingPending => ProcessingState == ProcessingDisplayState.Pending;
        public bool IsProcessingPartial => ProcessingState == ProcessingDisplayState.Partial;
        public bool IsProcessingCompleted => ProcessingState == ProcessingDisplayState.Completed;
        public bool IsProcessingFailed => ProcessingState == ProcessingDisplayState.Failed;
        public bool IsProcessingRemoved => ProcessingState == ProcessingDisplayState.Removed;

        partial void OnProcessingStateChanged(ProcessingDisplayState value)
        {
            OnPropertyChanged(nameof(HasProcessingBadge));
            OnPropertyChanged(nameof(IsProcessingRunning));
            OnPropertyChanged(nameof(IsProcessingPending));
            OnPropertyChanged(nameof(IsProcessingPartial));
            OnPropertyChanged(nameof(IsProcessingCompleted));
            OnPropertyChanged(nameof(IsProcessingFailed));
            OnPropertyChanged(nameof(IsProcessingRemoved));
        }

        public override string ToString() => Name;
    }
}
