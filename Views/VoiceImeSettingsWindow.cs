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
    private ConfigurationService? _configService;
    private ConfigurationService Configuration => _configService ??= new ConfigurationService();
    private readonly Func<Task<AzureSpeechConfig>> _load;
    private readonly Func<AzureSpeechConfig, Task> _save;
    private readonly Action<VoiceImeSectionVM> _refresh;
    private readonly VoiceImeRegistrationService _registrationService = new();
    private readonly VoiceImeSectionVM _vm;
    private readonly TextBlock _saveStatusText;
    private readonly CancellationTokenSource _closedCts = new();
    private static WeakReference<VoiceImeSettingsWindow>? _current;
    internal static Window? Current => _current != null && _current.TryGetTarget(out var window) && window.IsVisible ? window : null;
    private bool _closed;
    private AzureSpeechConfig? _config;
    private CancellationTokenSource? _saveDebounceCts;

    public VoiceImeSettingsWindow() : this(null, null, null) { }

    // 隔离首帧 fixture 注入内存配置与刷新，不提供生产命令行开关。
    internal VoiceImeSettingsWindow(Func<Task<AzureSpeechConfig>>? load,
        Func<AzureSpeechConfig, Task>? save, Action<VoiceImeSectionVM>? refresh, VoiceImeSectionVM? viewModel = null)
    {
        _load = load ?? (() => Configuration.LoadConfigAsync());
        _save = save ?? (config => Configuration.SaveConfigAsync(config));
        _refresh = refresh ?? (vm =>
        {
            // 两条原有命令独立启动；设备枚举不再挡住安装状态查询。
            vm.RefreshOptionsCommand.Execute(null);
            vm.RefreshStatusCommand.Execute(null);
        });
        var ver = VoiceImeRegistrationService.GetVoiceImeVersionString();
        Title = $"译见pro输入法设置 v{ver}";
        Width = 640;
        Height = 620;
        MinWidth = 540;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var args = Environment.GetCommandLineArgs();
        var autoCloseSelfTest = Array.IndexOf(args, "--self-test") >= 0;

        _vm = viewModel ?? new VoiceImeSectionVM(_registrationService);
        _vm.Changed += OnVmChanged;

        _saveStatusText = new TextBlock
        {
            Text = "正在加载共享配置…",
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
            IsEnabled = false,
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
            _current = new WeakReference<VoiceImeSettingsWindow>(this);
            try
            {
                // Opened 不代表已经渲染；配置解析、迁移、设备/状态均让出真实第一帧。
                await VoiceImeUiWindows.RenderedAsync(this, _closedCts.Token);
                await InitializeAsync();
                if (!_closed) sectionView.IsEnabled = _config != null;
                if (autoCloseSelfTest && !_closed)
                {
                    await Task.Delay(300, _closedCts.Token);
                    Close();
                }
            }
            catch (OperationCanceledException) when (_closed) { }
            catch { if (!_closed) _saveStatusText.Text = "设置加载未完成，请关闭后重试。"; }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _closedCts.Cancel();
            _vm.Changed -= OnVmChanged;
            if (_current != null && _current.TryGetTarget(out var current) && ReferenceEquals(current, this)) _current = null;
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var loaded = await Task.Run(_load).WaitAsync(_closedCts.Token);
            if (_closed) return;
            _config = loaded;
            _vm.LoadFrom(_config);
            _saveStatusText.Text = "已加载当前配置（修改后自动保存）";
            _refresh(_vm);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            if (!_closed) _saveStatusText.Text = "加载配置失败，请检查共享配置后重新打开设置。";
        }
    }

    private void OnVmChanged()
    {
        if (_closed || _config == null) return;
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
            await _save(_config); // 保留原关闭后的已提交防抖保存语义，不丢用户最后一次修改。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closed) _saveStatusText.Text = $"已自动保存（{DateTime.Now:HH:mm:ss}）";
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closed) _saveStatusText.Text = "保存失败，请检查共享配置写入权限后重试。";
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
            mainAppPath ??= VoiceImeUiProtocol.SourceExecutable(Path.Combine(AppContext.BaseDirectory, "TrueFluentPro.exe"));
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
