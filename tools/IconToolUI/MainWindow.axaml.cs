using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using IconToolUI.ViewModels;

namespace IconToolUI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is null) return;

        ViewModel.ConfirmDialog = ShowConfirmDialogAsync;
        ViewModel.ShowToast = ShowToastNotification;

        // 双击图标项直接设为目录图标
        IconListBox.DoubleTapped += IconListBox_DoubleTapped;

        // 管理员标识
        if (ViewModel.IsAdmin)
            Title += " [管理员]";

        // 命令行参数（UAC 提权重启后自动加载目录）
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
        {
            ViewModel.DirectoryPath = args[1];
            _ = ViewModel.LoadPathCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// 右下角浮窗通知。
    /// </summary>
    private async void ShowToastNotification(string message, bool isSuccess)
    {
        ToastText.Text = message;
        ToastPanel.Background = isSuccess
            ? new SolidColorBrush(Color.Parse("#1B7D3A"))
            : new SolidColorBrush(Color.Parse("#C42B1C"));
        ToastPanel.IsVisible = true;
        await Task.Delay(1500);
        ToastPanel.IsVisible = false;
    }

    /// <summary>
    /// 显示确认对话框。
    /// </summary>
    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        bool result = false;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var cancelButton = new Button { Content = "取消" };
        var confirmButton = new Button { Content = "以管理员重启" };

        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) => { result = true; dialog.Close(); };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(confirmButton);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;
        await dialog.ShowDialog(this);
        return result;
    }

    /// <summary>
    /// 双击图标项 — 直接设为目录图标。
    /// </summary>
    private void IconListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ViewModel?.SelectedIcon is not null)
            ViewModel.SetAsDirectoryIconCommand.Execute(null);
    }

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
