using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class SubscriptionSection : UserControl
{
    public SubscriptionSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is SubscriptionSectionVM vm)
        {
            vm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is SubscriptionSectionVM vm)
        {
            vm.PropertyChanged -= Vm_PropertyChanged;
        }

        base.OnUnloaded(e);
    }

    public void ResetTransientUiState()
    {
    }

    private async void CopySubscriptionKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SubscriptionSectionVM vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(vm.SubscriptionEditorKey ?? string.Empty);
        vm.NotifyStatus("已复制 Azure Speech 订阅密钥");
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubscriptionSectionVM.SelectedSubscription))
        {
            ResetTransientUiState();
        }
    }
}
