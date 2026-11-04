
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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

        /// <summary>浮窗专用：null=旧服务未提供，空串=仅原文更新，非空=同句的稳定标识。</summary>
        [JsonIgnore]
        public string? SubtitleSegmentKey { get; set; }

        /// <summary>浮窗专用的实际音频路由，不改变历史记录的来源显示。</summary>
        [JsonIgnore]
        public VadGateController.ActiveSource? SubtitleSource { get; set; }

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
                    OnPropertyChanged(nameof(SourceDisplayColor));
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

        /// <summary>来源颜色（与时间轴一致）：Mic=蓝，Loopback=红。</summary>
        public string SourceDisplayColor => _source switch
        {
            VadGateController.ActiveSource.Mic => "#3B82F6",
            VadGateController.ActiveSource.Loopback => "#EF4444",
            _ => "Transparent"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}


