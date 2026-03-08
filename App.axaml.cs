using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using TrueFluentPro.Services.EndpointProfiles;
using TrueFluentPro.Services;
using TrueFluentPro.Services.EndpointTesting;
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

        // --- 基础服务 ---
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<AzureSubscriptionValidator>();
        services.AddSingleton<IEndpointProfileCatalogService, EndpointProfileCatalogService>();
        services.AddSingleton<IAiEndpointModelDiscoveryService, AiEndpointModelDiscoveryService>();
        services.AddSingleton<IEndpointTemplateService, EndpointTemplateService>();
        services.AddSingleton<ISettingsImportExportService, SettingsImportExportService>();
        services.AddSingleton<ISettingsTransferFileService, SettingsTransferFileService>();
        services.AddSingleton<IModelRuntimeResolver, ModelRuntimeResolver>();
        services.AddSingleton<IAboutSectionService, AboutSectionService>();
        services.AddSingleton<IEndpointBatchTestService, EndpointBatchTestService>();

        // --- ViewModel ---
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            var configService = Services.GetRequiredService<ConfigurationService>();
            var startupShellPreferences = configService.LoadShellStartupPreferences();
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            vm.ApplyStartupShellPreferences(startupShellPreferences);
            vm.SetMainWindow(mainWindow);
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

