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
                    0 => new SolidColorBrush(Colors.Transparent),
                    1 => new SolidColorBrush(Color.FromArgb(230, 0, 0, 0)),
                    2 => new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                    _ => new SolidColorBrush(Colors.Transparent)
                };
            }
        }

        public IBrush TextBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Color.FromRgb(255, 20, 147)), // 透明→粉色
                    1 => new SolidColorBrush(Colors.White),                // 黑底→白字
                    2 => new SolidColorBrush(Colors.Black),                // 白底→黑字
                    _ => new SolidColorBrush(Color.FromRgb(255, 20, 147))
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
