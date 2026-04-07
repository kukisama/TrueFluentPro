using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using IconToolUI.ViewModels;

namespace IconToolUI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// 在目录路径 TextBox 中按 Enter 时加载。
    /// </summary>
    private void DirectoryPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.LoadPathCommand.Execute(null);
        }
    }

    /// <summary>
    /// 浏览按钮 — 打开文件夹选择对话框。
    /// </summary>
    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is Avalonia.Visual visual)
        {
            await ViewModel.BrowseDirectoryCommand.ExecuteAsync(visual);
        }
    }
}
