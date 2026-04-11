using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    public partial class TranscriptSegment : ObservableObject
    {
        [ObservableProperty]
        private string _speaker = "";

        [ObservableProperty]
        private int _speakerIndex;

        [ObservableProperty]
        private TimeSpan _startTime;

        [ObservableProperty]
        private string _text = "";

        [ObservableProperty]
        private bool _isCurrent;

        public List<SubtitleCue> SourceCues { get; set; } = new();

        /// <summary>时间戳显示文本（mm:ss 格式）</summary>
        public string TimeText => StartTime.TotalHours >= 1
            ? StartTime.ToString(@"h\:mm\:ss")
            : StartTime.ToString(@"m\:ss");
    }

    public class MindMapNode
    {
        public string Title { get; set; } = "";
        public List<MindMapNode> Children { get; set; } = new();
    }

    public class ResearchTopic : ObservableObject
    {
        private string _title = "";
        private bool _isSelected = true;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
