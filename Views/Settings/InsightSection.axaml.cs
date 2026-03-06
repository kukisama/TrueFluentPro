using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class InsightSection : UserControl
{
    private InsightSectionVM? _boundVm;

    public InsightSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is InsightSectionVM vm)
        {
            WireHandlers(vm);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        UnwireHandlers();
        base.OnUnloaded(e);
    }

    private void WireHandlers(InsightSectionVM vm)
    {
        if (ReferenceEquals(_boundVm, vm))
            return;

        UnwireHandlers();
        _boundVm = vm;
        PresetButtonsEditorControl.ItemsChanged += PresetButtonsEditorControl_ItemsChanged;
    }

    private void UnwireHandlers()
    {
        PresetButtonsEditorControl.ItemsChanged -= PresetButtonsEditorControl_ItemsChanged;
        _boundVm = null;
    }

    private void PresetButtonsEditorControl_ItemsChanged()
    {
        _boundVm?.NotifyPresetButtonsChanged();
    }
}
