using System.ComponentModel;
using System.Threading;

namespace TrueFluentPro.Models
{
    public enum BatchQueueItemType
    {
        ReviewSheet,
        SpeechSubtitle
    }

    public class BatchQueueItem : INotifyPropertyChanged
    {
        private string _fileName = "";
        private string _fullPath = "";
        private string _sheetName = "";
        private string _sheetTag = "";
        private string _prompt = "";
        private BatchQueueItemType _queueType = BatchQueueItemType.ReviewSheet;
        private BatchTaskStatus _status = BatchTaskStatus.Pending;
        private double _progress;
        private string _statusMessage = "";

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

        public string SheetName
        {
            get => _sheetName;
            set => SetProperty(ref _sheetName, value, nameof(SheetName));
        }

        public string SheetTag
        {
            get => _sheetTag;
            set => SetProperty(ref _sheetTag, value, nameof(SheetTag));
        }

        public string Prompt
        {
            get => _prompt;
            set => SetProperty(ref _prompt, value, nameof(Prompt));
        }

        public BatchQueueItemType QueueType
        {
            get => _queueType;
            set => SetProperty(ref _queueType, value, nameof(QueueType));
        }

        public BatchTaskStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value, nameof(Status)))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value, nameof(Progress));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public CancellationTokenSource? Cts { get; set; }

        public bool PauseRequested { get; set; }

        public bool CanCancel => Status is BatchTaskStatus.Pending or BatchTaskStatus.Running;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
