using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace TrueFluentPro.ViewModels
{
    public class FloatingInsightViewModel : INotifyPropertyChanged
    {
        private const double MinFontSize = 12;
        private const double MaxFontSize = 36;

        private string _insightMarkdown = "";
        private int _backgroundMode = 0; // default: transparent (matches subtitle)
        private double _fontSize = 14;
        private readonly AiInsightViewModel _aiInsight;

        public FloatingInsightViewModel(AiInsightViewModel aiInsight)
        {
            _aiInsight = aiInsight;
            _aiInsight.PropertyChanged += OnAiInsightPropertyChanged;

            // Sync initial value
            _insightMarkdown = _aiInsight.InsightMarkdown;
        }

        public string InsightMarkdown
        {
            get => _insightMarkdown;
            set
            {
                if (_insightMarkdown != value)
                {
                    _insightMarkdown = value;
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
                    OnPropertyChanged(nameof(ContentBackgroundBrush));
                    OnPropertyChanged(nameof(BorderBrush));
                    OnPropertyChanged(nameof(TextBrush));
                }
            }
        }

        public double FontSize
        {
            get => _fontSize;
            set
            {
                var clamped = Math.Clamp(value, MinFontSize, MaxFontSize);
                if (Math.Abs(_fontSize - clamped) > 0.01)
                {
                    _fontSize = clamped;
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
                    0 => new SolidColorBrush(Color.FromArgb(40, 15, 23, 42)),
                    1 => new SolidColorBrush(Color.FromArgb(238, 15, 23, 42)),
                    2 => new SolidColorBrush(Color.FromArgb(244, 248, 250, 252)),
                    _ => new SolidColorBrush(Color.FromArgb(40, 15, 23, 42))
                };
            }
        }

        public IBrush ContentBackgroundBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Color.FromArgb(76, 15, 23, 42)),
                    1 => new SolidColorBrush(Color.FromArgb(128, 30, 41, 59)),
                    2 => new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                    _ => new SolidColorBrush(Color.FromArgb(76, 15, 23, 42))
                };
            }
        }

        public IBrush BorderBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Color.FromArgb(120, 244, 114, 182)),
                    1 => new SolidColorBrush(Color.FromArgb(150, 99, 102, 241)),
                    2 => new SolidColorBrush(Color.FromArgb(120, 148, 163, 184)),
                    _ => new SolidColorBrush(Color.FromArgb(120, 244, 114, 182))
                };
            }
        }

        public IBrush TextBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Color.FromRgb(255, 85, 170)),
                    1 => new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    2 => new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    _ => new SolidColorBrush(Color.FromRgb(255, 85, 170))
                };
            }
        }

        public void ToggleBackground()
        {
            BackgroundMode = (BackgroundMode + 1) % 3;
        }

        public void IncreaseFontSize()
        {
            FontSize = Math.Min(_fontSize + 2, MaxFontSize);
        }

        public void DecreaseFontSize()
        {
            FontSize = Math.Max(_fontSize - 2, MinFontSize);
        }

        private void OnAiInsightPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AiInsightViewModel.InsightMarkdown))
            {
                InsightMarkdown = _aiInsight.InsightMarkdown;
            }
        }

        public void OnWindowClosed()
        {
            _aiInsight.PropertyChanged -= OnAiInsightPropertyChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
