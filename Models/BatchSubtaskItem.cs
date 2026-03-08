using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    public sealed partial class BatchSubtaskItem : ObservableObject
    {
        [ObservableProperty]
        private string _title = "";

        [ObservableProperty]
        private string _tag = "";

        [ObservableProperty]
        private string _statusText = "";

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private ProcessingDisplayState _state;

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private string _audioPath = "";

        [ObservableProperty]
        private bool _isSpeechSubtask;

        [ObservableProperty]
        private bool _canRegenerate = true;

        [ObservableProperty]
        private string _iconValue = "fa-solid fa-file-lines";

        [ObservableProperty]
        private Thickness _indentMargin = new(0, 0, 0, 0);
    }
}
