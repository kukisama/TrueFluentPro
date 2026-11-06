using System.Threading.Tasks;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TrueFluentPro.Views;

internal static class VoiceImeElevationDialog
{
    internal static async Task<bool> ConfirmAsync(bool install)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await ShowAsync(install);
        // 明确返回 Task<bool>，不依赖 Dispatcher 对异步委托的隐式解包重载。
        var pending = await Dispatcher.UIThread.InvokeAsync<Task<bool>>(() => ShowAsync(install));
        return await pending;
    }

    private static async Task<bool> ShowAsync(bool install)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = ResolveOwner(desktop);
        if (owner == null || !owner.IsVisible) return false;
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6) };
        var confirm = new Button
        {
            Content = install ? "授权并安装 / 修复" : "授权并卸载",
            Padding = new Thickness(16, 6), Classes = { "accent" }
        };
        var dialog = new Window
        {
            Title = "输入法维护需要管理员权限",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 20,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"本次操作：{(install ? "安装 / 修复输入法" : "卸载输入法")}。\n\n"
                            + "需要管理员权限；将以管理员身份重新启动本次输入法维护流程并自动继续；主窗口和当前任务保留；UAC请使用当前用户。\n\n"
                            + "取消将保留原程序，不执行本次维护。",
                        TextWrapping = TextWrapping.Wrap, FontSize = 14
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm }
                    }
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.Close(false);
        };
        return await dialog.ShowDialog<bool>(owner);
    }

    internal static Window? ResolveOwner(IClassicDesktopStyleApplicationLifetime? desktop)
        => VoiceImeSettingsWindow.Current
            ?? desktop?.Windows.FirstOrDefault(window => window.IsVisible && window.IsActive)
            ?? (desktop?.MainWindow is { IsVisible: true } main ? main : null);
}