using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using System;
using TrueFluentPro.Services;

namespace TrueFluentPro;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 维护子模式必须早于配置、Avalonia、后台及其他命令模式；混合参数直接拒绝。
        if (Array.Exists(args, value => value == VoiceImeMaintenanceElevation.Switch))
        {
            Environment.ExitCode = VoiceImeMaintenanceElevation.RunWorkerAsync(args).GetAwaiter().GetResult();
            return;
        }
        // 输入法独立设置窗口模式：只启动轻量设置弹窗，不初始化主窗口。
        if (args.Length > 0 && args[0] == "--voice-ime-settings")
        {
            WindowsDpiAwareness.TryEnablePerMonitorV2();
            Environment.ExitCode = BuildVoiceImeSettingsApp().StartWithClassicDesktopLifetime(args);
            return;
        }
        // 输入法后台不创建 Avalonia、启动画面或主程序服务，也不持有主窗口。
        if (args.Length > 0 && args[0] == "--voice-ime-host")
        {
            Environment.ExitCode = VoiceImeHost.RunAsync(args).GetAwaiter().GetResult();
            return;
        }
        CrashLogger.Init();
        Helpers.ScrollDiagLog.Reset();
        try
        {
            WindowsDpiAwareness.TryEnablePerMonitorV2();
            NativePrelaunchSplash.Show(title: "TrueFluentPro", status: "正在启动...");
            Environment.ExitCode = 0;
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Environment.ExitCode = exitCode;

            if (exitCode != 0)
            {
                CrashLogger.WriteMessage("Avalonia.StartWithClassicDesktopLifetime", $"exitCode={exitCode}");
            }

            NativePrelaunchSplash.CloseIfOpen();
        }
        catch (Exception ex)
        {
            NativePrelaunchSplash.CloseIfOpen();
            CrashLogger.Write(source: "Program.Main", exception: ex, isTerminating: true);
            Environment.Exit(1);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    public static AppBuilder BuildVoiceImeSettingsApp()
    {
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        return AppBuilder.Configure<VoiceImeSettingsApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

public sealed class VoiceImeSettingsApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://TrueFluentPro/App.axaml"));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new TrueFluentPro.Views.VoiceImeSettingsWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}


