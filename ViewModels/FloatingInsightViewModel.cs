using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TrueFluentPro.ViewModels
{
    public class FloatingInsightViewModel : INotifyPropertyChanged
    {
        private const double MinFontSize = 12;
        private const double MaxFontSize = 36;

        private string _insightMarkdown = "";
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
