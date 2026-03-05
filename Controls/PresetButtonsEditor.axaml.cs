using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.Models;

namespace TrueFluentPro.Controls
{
    public partial class PresetButtonsEditor : UserControl
    {
        public event Action? ItemsChanged;

        public static readonly StyledProperty<ObservableCollection<InsightPresetButton>?> ItemsProperty =
            AvaloniaProperty.Register<PresetButtonsEditor, ObservableCollection<InsightPresetButton>?>(nameof(Items));

        public ObservableCollection<InsightPresetButton>? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public PresetButtonsEditor()
        {
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ItemsProperty)
            {
                PresetButtonsItemsControl.ItemsSource = Items;
            }
        }

        private void AddPresetButton_Click(object? sender, RoutedEventArgs e)
        {
            Items?.Add(new InsightPresetButton { Name = "新按钮", Prompt = "" });
            ItemsChanged?.Invoke();
        }

        private void RemovePresetButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is InsightPresetButton item)
            {
                Items?.Remove(item);
                ItemsChanged?.Invoke();
            }
        }

        private void PresetField_LostFocus(object? sender, RoutedEventArgs e)
        {
            ItemsChanged?.Invoke();
        }
    }
}
