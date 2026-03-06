using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views.Settings;

public partial class TextSection : UserControl
{
    public TextSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel vm)
        {
            SelectFontSizeComboBox(vm.Settings.DefaultFontSize);
        }
    }

    private void FontSizeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DefaultFontSizeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var size) &&
            DataContext is MainWindowViewModel vm)
        {
            vm.Settings.DefaultFontSize = size;
        }
    }

    private void SelectFontSizeComboBox(int fontSize)
    {
        for (var i = 0; i < DefaultFontSizeComboBox.Items.Count; i++)
        {
            if (DefaultFontSizeComboBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == fontSize.ToString())
            {
                DefaultFontSizeComboBox.SelectedIndex = i;
                return;
            }
        }
        if (DefaultFontSizeComboBox.Items.Count > 0)
            DefaultFontSizeComboBox.SelectedIndex = 0;
    }
}
