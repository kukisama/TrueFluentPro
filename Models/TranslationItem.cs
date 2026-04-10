
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Models
{
    public class TranslationItem : INotifyPropertyChanged
    {
        private DateTime _timestamp;
        private string _originalText = "";
        private string _translatedText = "";
        private bool _hasBeenWrittenToFile = false;
        private VadGateController.ActiveSource _source = VadGateController.ActiveSource.None;

        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                if (_timestamp != value)
                {
                    _timestamp = value;
                    OnPropertyChanged();
                }
            }
        }

        public string OriginalText
        {
            get => _originalText;
            set
            {
                if (_originalText != value)
                {
                    _originalText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string TranslatedText
        {
            get => _translatedText;
            set
            {
                if (_translatedText != value)
                {
                    _translatedText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasBeenWrittenToFile
        {
            get => _hasBeenWrittenToFile;
            set
            {
                if (_hasBeenWrittenToFile != value)
                {
                    _hasBeenWrittenToFile = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>音频来源：None=未标记/单路, Mic=麦克风(我), Loopback=环回(对方)。</summary>
        public VadGateController.ActiveSource Source
        {
            get => _source;
            set
            {
                if (_source != value)
                {
                    _source = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SourceDisplayLabel));
                }
            }
        }

        /// <summary>来源显示文本，用于 UI 绑定。</summary>
        public string SourceDisplayLabel => _source switch
        {
            VadGateController.ActiveSource.Mic => "\U0001f3a4",
            VadGateController.ActiveSource.Loopback => "\U0001f50a",
            _ => ""
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}


