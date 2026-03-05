using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.Models;

namespace TrueFluentPro.Controls
{
    public partial class ReviewSheetsEditor : UserControl
    {
        public event Action? ItemsChanged;

        public static readonly StyledProperty<ObservableCollection<ReviewSheetPreset>?> ItemsProperty =
            AvaloniaProperty.Register<ReviewSheetsEditor, ObservableCollection<ReviewSheetPreset>?>(nameof(Items));

        public ObservableCollection<ReviewSheetPreset>? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public ReviewSheetsEditor()
        {
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ItemsProperty)
            {
                ReviewSheetsItemsControl.ItemsSource = Items;
            }
        }

        private void AddReviewSheetButton_Click(object? sender, RoutedEventArgs e)
        {
            Items?.Add(new ReviewSheetPreset
            {
                Name = "新复盘",
                FileTag = "custom",
                Prompt = "",
                IncludeInBatch = true
            });
            ItemsChanged?.Invoke();
        }

        private void RemoveReviewSheet_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ReviewSheetPreset item)
            {
                Items?.Remove(item);
                ItemsChanged?.Invoke();
            }
        }

        private void ReviewField_LostFocus(object? sender, RoutedEventArgs e)
        {
            ItemsChanged?.Invoke();
        }
    }
}
