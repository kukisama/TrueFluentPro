using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueFluentPro.Models;
using TrueFluentPro.Views;

namespace TrueFluentPro.Services;

public sealed partial class AboutSectionService : ObservableObject, IAboutSectionService
{
    private readonly UpdateService _updateService = new();
    private UpdateInfo? _latestUpdate;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateVersionText = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    public string AppVersion { get; } = LoadAppVersion();

    public async Task ShowAboutAsync(Action<string>? reportStatus = null)
    {
        try
        {
            var owner = GetMainWindow();
            var about = new AboutView();
            if (owner != null)
            {
                await about.ShowDialog(owner);
            }
            else
            {
                about.Show();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Console.Error.WriteLine(ex);
            reportStatus?.Invoke($"打开关于失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task ShowHelpAsync(Action<string>? reportStatus = null)
    {
        try
        {
            var owner = GetMainWindow();
            var help = new HelpView();
            if (owner != null)
            {
                await help.ShowDialog(owner);
            }
            else
            {
                help.Show();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Console.Error.WriteLine(ex);
            reportStatus?.Invoke($"打开说明失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void OpenAzureSpeechPortal(Action<string>? reportStatus = null)
        => OpenUrl("https://portal.azure.com/#view/Microsoft_Azure_ProjectOxford/CognitiveServicesHub/~/SpeechServices", reportStatus);

    public void Open21vAzureSpeechPortal(Action<string>? reportStatus = null)
        => OpenUrl("https://portal.azure.cn/#create/Microsoft.CognitiveServicesSpeechServices", reportStatus);

    public void OpenStoragePortal(Action<string>? reportStatus = null)
        => OpenUrl("https://portal.azure.com/#create/Microsoft.StorageAccount", reportStatus);

    public void Open21vStoragePortal(Action<string>? reportStatus = null)
        => OpenUrl("https://portal.azure.cn/#create/Microsoft.StorageAccount", reportStatus);

    public void OpenFoundryPortal(Action<string>? reportStatus = null)
        => OpenUrl("https://ai.azure.com", reportStatus);

    public void OpenProjectGitHub(Action<string>? reportStatus = null)
        => OpenUrl("https://github.com/kukisama/TrueFluentPro", reportStatus);

    public async Task CheckForUpdateAsync(bool silent, bool isAutoUpdateEnabled, Action<string>? reportStatus = null)
    {
        try
        {
            if (silent && !isAutoUpdateEnabled)
            {
                return;
            }

            var currentVersion = UpdateService.ParseCurrentVersion();
            var info = await _updateService.CheckForUpdateAsync(currentVersion);

            if (info == null)
            {
                if (!silent)
                {
                    reportStatus?.Invoke("当前已是最新版本");
                }
                return;
            }

            _latestUpdate = info;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateVersionText = $"发现新版本 v{info.LatestVersion}";
                IsUpdateAvailable = true;
            });

            if (silent && !string.IsNullOrEmpty(info.DownloadUrl))
            {
                await SilentDownloadAndPromptAsync(reportStatus);
            }
            else if (!silent)
            {
                reportStatus?.Invoke($"发现新版本 v{info.LatestVersion}，请在关于中查看");
            }
        }
        catch
        {
            if (!silent)
            {
                reportStatus?.Invoke("检查更新失败");
            }
        }
    }

    public async Task DownloadAndApplyUpdateAsync(Action<string>? reportStatus = null)
    {
        if (_latestUpdate == null || string.IsNullOrEmpty(_latestUpdate.DownloadUrl))
        {
            reportStatus?.Invoke("无可用的下载地址，请前往 GitHub 手动下载");
            OpenUrl(_latestUpdate?.ReleasePageUrl ?? "https://github.com/kukisama/TrueFluentPro/releases", reportStatus);
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDownloading = true;
                DownloadProgress = 0;
            });
            reportStatus?.Invoke("正在下载更新...");

            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p));
            var zipPath = await _updateService.DownloadUpdateAsync(_latestUpdate.DownloadUrl, _latestUpdate.AssetSize, progress);

            if (string.IsNullOrEmpty(zipPath))
            {
                reportStatus?.Invoke("下载更新失败，请检查网络连接");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => DownloadProgress = 1.0);
            reportStatus?.Invoke("下载完成，正在启动更新...");
            await Task.Delay(500);

            if (!_updateService.LaunchUpdaterAndExit(zipPath))
            {
                reportStatus?.Invoke($"未找到 Updater.exe，更新包已下载到: {zipPath}");
            }
        }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"更新失败: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsDownloading = false);
        }
    }

    private async Task SilentDownloadAndPromptAsync(Action<string>? reportStatus)
    {
        if (_latestUpdate == null || string.IsNullOrEmpty(_latestUpdate.DownloadUrl))
        {
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDownloading = true;
                DownloadProgress = 0;
            });

            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p));
            var zipPath = await _updateService.DownloadUpdateAsync(_latestUpdate.DownloadUrl, _latestUpdate.AssetSize, progress);
            if (string.IsNullOrEmpty(zipPath))
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => DownloadProgress = 1.0);
            var confirmed = await ShowUpdateReadyDialogAsync();
            if (confirmed && !_updateService.LaunchUpdaterAndExit(zipPath))
            {
                reportStatus?.Invoke($"未找到 Updater.exe，更新包已下载到: {zipPath}");
            }
        }
        catch
        {
            // 静默失败，不打扰用户。
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsDownloading = false);
        }
    }

    private static Window? GetMainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static void OpenUrl(string url, Action<string>? reportStatus)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            reportStatus?.Invoke($"已打开链接: {url}");
        }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"打开链接失败: {ex.Message}");
        }
    }

    private static string LoadAppVersion()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "RELEASE_NOTES.md"),
                Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "RELEASE_NOTES.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RELEASE_NOTES.md")
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var firstLine = File.ReadLines(path).FirstOrDefault() ?? string.Empty;
                var match = Regex.Match(firstLine, @"v(\d+\.\d+\.\d+)");
                if (match.Success)
                {
                    return $"版本 {match.Groups[1].Value}";
                }
            }
        }
        catch
        {
            // ignore
        }

        return "版本 未知";
    }

    private async Task<bool> ShowUpdateReadyDialogAsync()
    {
        var owner = GetMainWindow();
        if (owner == null)
        {
            return false;
        }

        var result = false;
        var dialog = new Window
        {
            Title = "更新准备就绪",
            Width = 400,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"新版本 v{_latestUpdate?.LatestVersion} 已下载完成。\n点击「确定」将关闭程序并开始更新。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "稍后再说", Padding = new Thickness(16, 6) },
                            new Button { Content = "确定", Padding = new Thickness(16, 6), Classes = { "accent" } }
                        }
                    }
                }
            }
        };

        var buttons = (StackPanel)((StackPanel)dialog.Content!).Children[1];
        ((Button)buttons.Children[0]).Click += (_, _) => { result = false; dialog.Close(); };
        ((Button)buttons.Children[1]).Click += (_, _) => { result = true; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return result;
    }
}
