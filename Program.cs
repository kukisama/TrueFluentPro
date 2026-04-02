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
}


