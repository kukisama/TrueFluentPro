using Avalonia.Controls;
using Avalonia.Interactivity;
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
        if (SubShowPwdCheckBox != null)
        {
            SubShowPwdCheckBox.IsChecked = false;
        }
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubscriptionSectionVM.SelectedSubscription))
        {
            ResetTransientUiState();
        }
    }
}
