using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class AudioLabSection : UserControl
{
    private AudioLabSectionVM? _boundVm;

    public AudioLabSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is AudioLabSectionVM vm)
            WireHandlers(vm);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        UnwireHandlers();
        base.OnUnloaded(e);
    }

    private void WireHandlers(AudioLabSectionVM vm)
    {
        if (ReferenceEquals(_boundVm, vm)) return;
        UnwireHandlers();
        _boundVm = vm;
        StagePresetsEditorControl.ItemsChanged += OnStagePresetsChanged;
    }

    private void UnwireHandlers()
    {
        StagePresetsEditorControl.ItemsChanged -= OnStagePresetsChanged;
        _boundVm = null;
    }

    private void OnStagePresetsChanged()
    {
        _boundVm?.NotifyStagePresetsChanged();
    }
}
