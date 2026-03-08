using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    public sealed partial class BatchPackageItem : ObservableObject
    {
        [ObservableProperty]
        private string _displayName = "";

        [ObservableProperty]
        private string _fullPath = "";

        [ObservableProperty]
        private ProcessingDisplayState _state;

        [ObservableProperty]
        private string _stateText = "";

        [ObservableProperty]
        private string _summaryText = "";

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private bool _isRemoved;

        [ObservableProperty]
        private bool _isPaused;

        [ObservableProperty]
        private bool _isExpanded;

        [ObservableProperty]
        private int _completedCount;

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private int _failedCount;

        public ObservableCollection<BatchSubtaskItem> Subtasks { get; } = new();

        public bool IsCompleted => State == ProcessingDisplayState.Completed;
        public bool IsRemovedBucket => State == ProcessingDisplayState.Removed;
        public bool IsInFlight => State is ProcessingDisplayState.Pending or ProcessingDisplayState.Running or ProcessingDisplayState.Partial;
        public string ExpandGlyph => IsExpanded ? "▾" : "▸";
        public bool CanPause => !IsRemoved && !IsPaused && State == ProcessingDisplayState.Running;
        public bool CanResume => !IsRemoved && IsPaused;
        public bool CanDelete => !IsRemoved && State is ProcessingDisplayState.Running or ProcessingDisplayState.Partial or ProcessingDisplayState.Failed;
        public bool CanEnqueue => !IsRemoved && State is ProcessingDisplayState.Pending or ProcessingDisplayState.Partial or ProcessingDisplayState.Failed;

        partial void OnStateChanged(ProcessingDisplayState value)
        {
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsRemovedBucket));
            OnPropertyChanged(nameof(IsInFlight));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanEnqueue));
        }

        partial void OnIsRemovedChanged(bool value)
        {
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanEnqueue));
        }

        partial void OnIsPausedChanged(bool value)
        {
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
        }

        partial void OnIsExpandedChanged(bool value)
        {
            OnPropertyChanged(nameof(ExpandGlyph));
        }
    }
}
