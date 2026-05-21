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
        private bool _isClickThrough;
        private readonly SubtitleSyncService _syncService;

        /// <summary>该浮动窗口关注的音频来源（None 表示不区分来源，显示所有）。</summary>
        public VadGateController.ActiveSource SourceFilter { get; }

        /// <summary>标题标签</summary>
        public string SourceLabel => SourceFilter switch
        {
            VadGateController.ActiveSource.Mic => "\U0001f3a4 我的语音",
            VadGateController.ActiveSource.Loopback => "\U0001f50a 对方语音",
            _ => "\U0001f50a 全部字幕"
        };

        /// <summary>窗口边框颜色</summary>
        public IBrush SourceBorderBrush => SourceFilter switch
        {
            VadGateController.ActiveSource.Mic => new SolidColorBrush(Color.Parse("#603B82F6")),
            VadGateController.ActiveSource.Loopback => new SolidColorBrush(Color.Parse("#60F59E0B")),
            _ => new SolidColorBrush(Color.Parse("#40FFFFFF"))
        };

        /// <summary>当前生效的"说话源"：综合窗口跟随实际说话人，过滤窗口固定为各自来源。</summary>
        private VadGateController.ActiveSource EffectiveSource
            => SourceFilter == VadGateController.ActiveSource.None
                ? _syncService.CurrentSource
                : SourceFilter;

        /// <summary>左侧说话源图标（FontAwesome）</summary>
        public string SourceIconValue => EffectiveSource switch
        {
            VadGateController.ActiveSource.Mic => "fa-solid fa-microphone",
            VadGateController.ActiveSource.Loopback => "fa-solid fa-volume-high",
            _ => "fa-solid fa-closed-captioning"
        };

        /// <summary>左侧说话源图标颜色（在黑底上需够亮）</summary>
        public IBrush SourceIconBrush => EffectiveSource switch
        {
            VadGateController.ActiveSource.Mic => new SolidColorBrush(Color.Parse("#FF60A5FA")),       // 亮蓝
            VadGateController.ActiveSource.Loopback => new SolidColorBrush(Color.Parse("#FFFBBF24")),  // 亮橙
            _ => new SolidColorBrush(Color.Parse("#FFCBD5E1"))                                          // 亮灰
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
                    OnPropertyChanged(nameof(IsBareTextMode));
                    OnPropertyChanged(nameof(TextShadowEffect));
                }
            }
        }

        public double FontScaleBias
        {
            get => _fontScaleBias;
            set
            {
                var clamped = Math.Clamp(value, 0.55, 1.8);
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
                    0 => new SolidColorBrush(Color.FromArgb(217, 14, 16, 22)),                   // 黑底玻璃（默认，主流）
                    1 => new SolidColorBrush(Colors.Transparent),                                // 透明（裸字 + 描边）
                    2 => new SolidColorBrush(Color.FromArgb(230, 250, 250, 252)),                // 浅色雪白
                    3 => new SolidColorBrush(Color.FromArgb(240, 0, 0, 0)),                      // 高对比纯黑
                    _ => new SolidColorBrush(Color.FromArgb(217, 14, 16, 22))
                };
            }
        }

        public IBrush TextBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Color.FromRgb(245, 247, 250)),                      // 黑玻璃：象牙白
                    1 => new SolidColorBrush(Color.FromRgb(255, 255, 255)),                      // 透明：白字 + 阴影描边
                    2 => new SolidColorBrush(Color.FromRgb(15, 23, 42)),                         // 浅玻璃：宝石黑
                    3 => new SolidColorBrush(Colors.White),                                      // 高对比：纯白
                    _ => new SolidColorBrush(Color.FromRgb(245, 247, 250))
                };
            }
        }

        /// <summary>透明 / 高对比模式下给文字补阴影，提升可读性。</summary>
        public Avalonia.Media.IEffect? TextShadowEffect
        {
            get
            {
                if (_backgroundMode == 1)
                {
                    // 透明模式：黑色柔和阴影 + 轻描边
                    return new DropShadowEffect
                    {
                        BlurRadius = 6,
                        Color = Colors.Black,
                        Opacity = 0.95,
                        OffsetX = 0,
                        OffsetY = 1
                    };
                }
                if (_backgroundMode == 3)
                {
                    return new DropShadowEffect
                    {
                        BlurRadius = 2,
                        Color = Colors.Black,
                        Opacity = 0.5,
                        OffsetX = 0,
                        OffsetY = 0
                    };
                }
                return null;
            }
        }

        /// <summary>当前背景是否为"透明裸字"模式（需要描边提升可读性）。</summary>
        public bool IsBareTextMode => _backgroundMode == 1;

        /// <summary>当前是否为“点击穿透”模式（鼠标穿过字幕不被它拦截）。</summary>
        public bool IsClickThrough
        {
            get => _isClickThrough;
            set
            {
                if (_isClickThrough != value)
                {
                    _isClickThrough = value;
                    OnPropertyChanged();
                }
            }
        }

        public void ToggleClickThrough() => IsClickThrough = !IsClickThrough;

        public void ToggleTransparency()
        {
            BackgroundMode = (BackgroundMode + 1) % 4;
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

            // 综合窗口需要随当前说话人切换图标
            if (SourceFilter == VadGateController.ActiveSource.None)
            {
                OnPropertyChanged(nameof(SourceIconValue));
                OnPropertyChanged(nameof(SourceIconBrush));
            }
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

