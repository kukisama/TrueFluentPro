using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class StorageSection : UserControl
{
    public StorageSection()
    {
        InitializeComponent();
    }

    private async void BrowseSessionDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择会话目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null) return;
        var path = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && DataContext is StorageSectionVM vm)
        {
            vm.SessionDirectory = path;
        }
    }
}
