using System.ComponentModel;

namespace TrueFluentPro.Models
{
    public enum BatchTaskStatus
    {
        Pending,
        Running,
        Responding,
        Completed,
        Failed
    }

    public class BatchTaskItem : INotifyPropertyChanged
    {
        private string _fileName = "";
        private string _fullPath = "";
        private BatchTaskStatus _status = BatchTaskStatus.Pending;
        private double _progress;
        private bool _hasAiSubtitle;
        private bool _hasAiSummary;
        private string _statusMessage = "";
        private int _reviewTotal;
        private int _reviewCompleted;
        private int _reviewFailed;
        private int _reviewPending;
        private string _reviewStatusText = "";
        private bool _forceReviewRegeneration;

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value, nameof(FileName));
        }

        public string FullPath
        {
            get => _fullPath;
            set => SetProperty(ref _fullPath, value, nameof(FullPath));
        }

        public BatchTaskStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value, nameof(Status));
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value, nameof(Progress));
        }

        public bool HasAiSubtitle
        {
            get => _hasAiSubtitle;
            set => SetProperty(ref _hasAiSubtitle, value, nameof(HasAiSubtitle));
        }

        public bool HasAiSummary
        {
            get => _hasAiSummary;
            set => SetProperty(ref _hasAiSummary, value, nameof(HasAiSummary));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public int ReviewTotal
        {
            get => _reviewTotal;
            set => SetProperty(ref _reviewTotal, value, nameof(ReviewTotal));
        }

        public int ReviewCompleted
        {
            get => _reviewCompleted;
            set => SetProperty(ref _reviewCompleted, value, nameof(ReviewCompleted));
        }

        public int ReviewFailed
        {
            get => _reviewFailed;
            set => SetProperty(ref _reviewFailed, value, nameof(ReviewFailed));
        }

        public int ReviewPending
        {
            get => _reviewPending;
            set => SetProperty(ref _reviewPending, value, nameof(ReviewPending));
        }

        public string ReviewStatusText
        {
            get => _reviewStatusText;
            set => SetProperty(ref _reviewStatusText, value, nameof(ReviewStatusText));
        }

        public bool ForceReviewRegeneration
        {
            get => _forceReviewRegeneration;
            set => SetProperty(ref _forceReviewRegeneration, value, nameof(ForceReviewRegeneration));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
