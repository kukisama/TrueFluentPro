using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class TransferSection : UserControl
{
    public TransferSection()
    {
        InitializeComponent();
    }

    private async void OnExportSettingsPackageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TransferSectionVM vm)
        {
            return;
        }

        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider == null)
        {
            vm.ReportStorageProviderUnavailable(isImport: false);
            return;
        }

        await vm.ExportAsync(provider);
    }

    private async void OnImportSettingsPackageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TransferSectionVM vm)
        {
            return;
        }

        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider == null)
        {
            vm.ReportStorageProviderUnavailable(isImport: true);
            return;
        }

        await vm.ImportAsync(provider);
    }
}