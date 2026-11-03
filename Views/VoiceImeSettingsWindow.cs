using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views.Settings;

namespace TrueFluentPro.Views;

/// <summary>独立语音输入法设置窗口（点击浮窗齿轮弹出），与主程序共享同一份 config.json。</summary>
public sealed class VoiceImeSettingsWindow : Window
{
    private readonly ConfigurationService _configService = new();
    private readonly VoiceImeRegistrationService _registrationService = new();
    private readonly VoiceImeSectionVM _vm;
    private readonly TextBlock _saveStatusText;
    private AzureSpeechConfig? _config;
    private CancellationTokenSource? _saveDebounceCts;

    public VoiceImeSettingsWindow()
    {
        var ver = VoiceImeRegistrationService.GetProductVersionString();
        Title = $"译见pro输入法设置 v{ver}";
        Width = 640;
        Height = 620;
        MinWidth = 540;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var args = Environment.GetCommandLineArgs();
        var autoCloseSelfTest = Array.IndexOf(args, "--self-test") >= 0;

        _vm = new VoiceImeSectionVM(_registrationService);
        _vm.Changed += OnVmChanged;

        _saveStatusText = new TextBlock
        {
            Text = "修改后将自动保存到共享配置",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = Brushes.Gray
        };

        var openMainAppButton = new Button
        {
            Content = "打开主程序管理终结点 / AAD 登录"
        };
        openMainAppButton.Click += (_, _) => LaunchMainApplication();

        var closeButton = new Button
        {
            Content = "完成",
            Width = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        closeButton.Click += (_, _) => Close();

        var footerBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(20, 8, 20, 16)
        };
        Grid.SetColumn(_saveStatusText, 0);
        Grid.SetColumn(openMainAppButton, 1);
        Grid.SetColumn(closeButton, 2);
        footerBar.Children.Add(_saveStatusText);
        footerBar.Children.Add(openMainAppButton);
        footerBar.Children.Add(closeButton);

        var sectionView = new VoiceImeSection
        {
            DataContext = _vm,
            Margin = new Thickness(16, 16, 16, 8)
        };

        var scroll = new ScrollViewer
        {
            Content = sectionView
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(footerBar, 1);
        root.Children.Add(scroll);
        root.Children.Add(footerBar);

        Content = root;
        Opened += async (_, _) =>
        {
            await InitializeAsync();
            if (autoCloseSelfTest)
            {
                await Task.Delay(300);
                Close();
            }
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            _config = await _configService.LoadConfigAsync();
            _vm.LoadFrom(_config);
            await _vm.RefreshAsync();
            _saveStatusText.Text = "已加载当前配置（修改后自动保存）";
        }
        catch (Exception ex)
        {
            _saveStatusText.Text = "加载配置失败：" + ex.Message;
        }
    }

    private void OnVmChanged()
    {
        if (_config == null) return;
        _vm.ApplyTo(_config);
        _saveDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        _ = SaveDebouncedAsync(cts.Token);
    }

    private async Task SaveDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            if (_config == null) return;
            await _configService.SaveConfigAsync(_config);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _saveStatusText.Text = $"已自动保存（{DateTime.Now:HH:mm:ss}）";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _saveStatusText.Text = "保存失败：" + ex.Message;
            });
        }
    }

    private static void LaunchMainApplication()
    {
        try
        {
            string? mainAppPath = null;
            if (OperatingSystem.IsWindows())
            {
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = user.OpenSubKey(@"Software\TrueFluentPro\VoiceImeUI") ?? user.OpenSubKey(@"Software\TrueFluentPro\VoiceIme");
                var candidate = key?.GetValue("MainAppExe") as string ?? key?.GetValue("HostExe") as string;
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    mainAppPath = candidate;
            }
            mainAppPath ??= Path.Combine(AppContext.BaseDirectory, "TrueFluentPro.exe");
            if (!File.Exists(mainAppPath)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = mainAppPath,
                WorkingDirectory = Path.GetDirectoryName(mainAppPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
