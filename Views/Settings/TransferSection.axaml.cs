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

    private async void OnExportFullConfigClick(object? sender, RoutedEventArgs e)
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

        await vm.ExportFullConfigAsync(provider);
    }

    private async void OnExportBasicAiConfigClick(object? sender, RoutedEventArgs e)
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

        await vm.ExportBasicAiConfigAsync(provider);
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