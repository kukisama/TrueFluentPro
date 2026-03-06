using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class TextSection : UserControl
{
    private TextSectionVM? _boundVm;
    private PropertyChangedEventHandler? _settingsPropertyChangedHandler;

    public TextSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is TextSectionVM vm)
        {
            WireSettings(vm);
            SelectFontSizeComboBox(vm.DefaultFontSize);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        UnwireSettings();
        base.OnUnloaded(e);
    }

    private void FontSizeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DefaultFontSizeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var size) &&
            DataContext is TextSectionVM vm)
        {
            vm.DefaultFontSize = size;
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

    private void WireSettings(TextSectionVM vm)
    {
        if (ReferenceEquals(_boundVm, vm))
            return;

        UnwireSettings();

        _boundVm = vm;
        _settingsPropertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(TextSectionVM.DefaultFontSize))
            {
                SelectFontSizeComboBox(vm.DefaultFontSize);
            }
        };

        vm.PropertyChanged += _settingsPropertyChangedHandler;
    }

    private void UnwireSettings()
    {
        if (_boundVm != null && _settingsPropertyChangedHandler != null)
        {
            _boundVm.PropertyChanged -= _settingsPropertyChangedHandler;
        }

        _boundVm = null;
        _settingsPropertyChangedHandler = null;
    }
}
