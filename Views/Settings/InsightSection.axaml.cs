using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views.Settings;

public partial class InsightSection : UserControl
{
    public InsightSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel vm)
        {
            PresetButtonsEditorControl.ItemsChanged += () => vm.Settings.NotifyPresetButtonsChanged();
        }
    }
}
