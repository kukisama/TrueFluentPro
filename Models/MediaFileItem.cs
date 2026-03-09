using CommunityToolkit.Mvvm.ComponentModel;
using System;

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

        public string ReviewListDisplayName => TrimAudioPrefix(Name);

        public string ReviewSubtitleListDisplayName => string.IsNullOrWhiteSpace(Name) ? "" : Name;

        public string ReviewListToolTip
        {
            get
            {
                var fileName = string.IsNullOrWhiteSpace(Name) ? "(未命名文件)" : Name;
                var state = string.IsNullOrWhiteSpace(ProcessingBadgeText) ? GetProcessingStateText() : ProcessingBadgeText;
                var detail = string.IsNullOrWhiteSpace(ProcessingDetailText)
                    ? state
                    : ProcessingDetailText;

                return $"文件：{fileName}\n状态：{state}\n进度：{detail}";
            }
        }

        public string ReviewSubtitleToolTip
        {
            get
            {
                var fileName = string.IsNullOrWhiteSpace(Name) ? "(未命名文件)" : Name;
                var fullPath = string.IsNullOrWhiteSpace(FullPath) ? "(路径不可用)" : FullPath;
                return $"字幕：{fileName}\n路径：{fullPath}";
            }
        }

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
            OnPropertyChanged(nameof(ReviewListToolTip));
        }

        partial void OnNameChanged(string value)
        {
            OnPropertyChanged(nameof(ReviewListDisplayName));
            OnPropertyChanged(nameof(ReviewSubtitleListDisplayName));
            OnPropertyChanged(nameof(ReviewListToolTip));
            OnPropertyChanged(nameof(ReviewSubtitleToolTip));
        }

        partial void OnFullPathChanged(string value)
            => OnPropertyChanged(nameof(ReviewSubtitleToolTip));

        partial void OnProcessingBadgeTextChanged(string value)
            => OnPropertyChanged(nameof(ReviewListToolTip));

        partial void OnProcessingDetailTextChanged(string value)
            => OnPropertyChanged(nameof(ReviewListToolTip));

        private static string TrimAudioPrefix(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "";
            }

            if (!fileName.StartsWith("Audio", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            var trimmed = fileName.Substring(5).TrimStart('_', '-', ' ', '.');
            return string.IsNullOrWhiteSpace(trimmed) ? fileName : trimmed;
        }

        private string GetProcessingStateText() => ProcessingState switch
        {
            ProcessingDisplayState.Pending => "未处理",
            ProcessingDisplayState.Running => "处理中",
            ProcessingDisplayState.Partial => "部分完成",
            ProcessingDisplayState.Completed => "已完成",
            ProcessingDisplayState.Failed => "失败",
            ProcessingDisplayState.Removed => "已删除",
            _ => "未处理"
        };

        public override string ToString() => Name;
    }
}
