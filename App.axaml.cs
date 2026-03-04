using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLogger.HookAvaloniaUiThread();

        var services = new ServiceCollection();

        // --- 基础服务（Phase 1 只注册这些） ---
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<AzureSubscriptionValidator>();

        // --- ViewModel ---
        services.AddSingleton<MainWindowViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            vm.SetMainWindow(mainWindow);
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

