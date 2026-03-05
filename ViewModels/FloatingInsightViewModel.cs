using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace TrueFluentPro.ViewModels
{
    public class FloatingInsightViewModel : INotifyPropertyChanged
    {
        private string _insightMarkdown = "";
        private int _backgroundMode = 1; // default: semi-transparent dark
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
                var clamped = Math.Clamp(value, 12, 36);
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
                    0 => new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                    1 => new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                    2 => new SolidColorBrush(Colors.Transparent),
                    _ => new SolidColorBrush(Color.FromArgb(230, 30, 30, 30))
                };
            }
        }

        public IBrush TextBrush
        {
            get
            {
                return _backgroundMode switch
                {
                    0 => new SolidColorBrush(Colors.Black),
                    1 => new SolidColorBrush(Colors.White),
                    2 => new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    _ => new SolidColorBrush(Colors.White)
                };
            }
        }

        public void ToggleBackground()
        {
            BackgroundMode = (BackgroundMode + 1) % 3;
        }

        public void IncreaseFontSize()
        {
            FontSize = Math.Min(_fontSize + 2, 36);
        }

        public void DecreaseFontSize()
        {
            FontSize = Math.Max(_fontSize - 2, 12);
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
