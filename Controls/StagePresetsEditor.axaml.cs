using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.Models;

namespace TrueFluentPro.Controls
{
    public partial class StagePresetsEditor : UserControl
    {
        public event Action? ItemsChanged;

        public static readonly StyledProperty<ObservableCollection<AudioLabStagePreset>?> ItemsProperty =
            AvaloniaProperty.Register<StagePresetsEditor, ObservableCollection<AudioLabStagePreset>?>(nameof(Items));

        public ObservableCollection<AudioLabStagePreset>? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public StagePresetsEditor()
        {
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ItemsProperty)
            {
                StagePresetsItemsControl.ItemsSource = Items;
            }
        }

        private void AddPreset_Click(object? sender, RoutedEventArgs e)
        {
            Items?.Add(new AudioLabStagePreset
            {
                Stage = "Custom",
                DisplayName = "新阶段",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true,
                ShowInTab = true,
                IncludeInBatch = true,
            });
            ItemsChanged?.Invoke();
        }

        private void RemovePreset_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AudioLabStagePreset item)
            {
                Items?.Remove(item);
                ItemsChanged?.Invoke();
            }
        }

        private void Field_Changed(object? sender, RoutedEventArgs e)
        {
            ItemsChanged?.Invoke();
        }

        private void Field_Changed(object? sender, SelectionChangedEventArgs e)
        {
            ItemsChanged?.Invoke();
        }
    }
}
