using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels
{    public class FloatingSubtitleViewModel : INotifyPropertyChanged
    {
        private string _subtitleText = "等待字幕内容...";
        private string _formattedSubtitleText = "等待字幕内容...";
        private int _backgroundMode = 0;
        private double _fontScaleBias = 1.0;
        private readonly SubtitleSyncService _syncService;

        /// <summary>该浮动窗口关注的音频来源（None 表示不区分来源，显示所有）。</summary>
        public VadGateController.ActiveSource SourceFilter { get; }

        /// <summary>标题标签</summary>
        public string SourceLabel => SourceFilter switch
        {
            VadGateController.ActiveSource.Mic => "\U0001f3a4 我的语音",
            VadGateController.ActiveSource.Loopback => "\U0001f50a 对方语音",
            _ => "\U0001f50a 浮动字幕"
        };

        /// <summary>窗口边框颜色</summary>
        public IBrush SourceBorderBrush => SourceFilter switch
        {
            VadGateController.ActiveSource.Mic => new SolidColorBrush(Color.Parse("#603B82F6")),
            VadGateController.ActiveSource.Loopback => new SolidColorBrush(Color.Parse("#60F59E0B")),
            _ => new SolidColorBrush(Color.Parse("#40FFFFFF"))
        };

        public FloatingSubtitleViewModel(SubtitleSyncService syncService,
            VadGateController.ActiveSource sourceFilter = VadGateController.ActiveSource.None)
        {
            _syncService = syncService;
            SourceFilter = sourceFilter;
            _syncService.SubtitleUpdated += OnSubtitleUpdated;
        }        public string SubtitleText
        {
            get => _subtitleText;
            set
            {
                if (_subtitleText != value)
                {
                    _subtitleText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FormattedSubtitleText
        {
            get => _formattedSubtitleText;
            private set
            {
                if (_formattedSubtitleText != value)
                {
                    _formattedSubtitleText = value;
                    OnPropertyChanged();
                }
            }
        }

        public int BackgroundMode
        {
            get => _backgroundMode;
            set
            {
                if (_backgroundMode != value)
                {
                    _backgroundMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BackgroundBrush));
                    OnPropertyChanged(nameof(TextBrush));
                }
            }
        }

        public double FontScaleBias
        {
            get => _fontScaleBias;
            set
            {
                var clamped = Math.Clamp(value, 0.75, 1.35);
                if (Math.Abs(_fontScaleBias - clamped) > 0.001)
                {
                    _fontScaleBias = clamped;
                    OnPropertyChanged();
                }
            }
        }

        public IBrush BackgroundBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Colors.Transparent),
                    1 => new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    2 => new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                    _ => new SolidColorBrush(Colors.Transparent)
                };
            }
        }        public IBrush TextBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Color.FromRgb(255, 20, 147)),
                    1 => new SolidColorBrush(Colors.White),
                    2 => new SolidColorBrush(Colors.Black),
                    _ => new SolidColorBrush(Color.FromRgb(255, 20, 147))
                };
            }
        }

        public void ToggleTransparency()
        {
            BackgroundMode = (BackgroundMode + 1) % 3;
        }

        public void IncreaseFontSize()
        {
            FontScaleBias += 0.08;
        }

        public void DecreaseFontSize()
        {
            FontScaleBias -= 0.08;
        }

        public void ResetFontSize()
        {
            FontScaleBias = 1.0;
        }

        public void UpdateFormattedSubtitleText(string text)
        {
            FormattedSubtitleText = string.IsNullOrWhiteSpace(text) ? SubtitleText : text;
        }        private void OnSubtitleUpdated(string newSubtitle)
        {
            var processedText = ProcessSubtitleText(newSubtitle);

            SubtitleText = processedText;
            FormattedSubtitleText = processedText;
        }        private string ProcessSubtitleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "等待字幕内容...";

            text = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
            text = text.Trim().Replace('\n', ' ').Replace('\r', ' ');

            if (string.IsNullOrEmpty(text))
                return "等待字幕内容...";

            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            return text;
        }

        public void OnWindowClosed()
        {
            if (_syncService != null)
            {
                _syncService.SubtitleUpdated -= OnSubtitleUpdated;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

