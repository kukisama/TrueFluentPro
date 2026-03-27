using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using TrueFluentPro.Services.EndpointProfiles;
using TrueFluentPro.Services;
using TrueFluentPro.Services.EndpointTesting;
using TrueFluentPro.Services.Storage;
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

        // --- SQLite 存储层 ---
        services.AddSingleton<SqliteFeatureSwitches>();
        services.AddSingleton<ISqliteDbService, SqliteDbService>();
        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();
        services.AddSingleton<ICreativeSessionRepository, CreativeSessionRepository>();
        services.AddSingleton<ISessionMessageRepository, SessionMessageRepository>();
        services.AddSingleton<ISessionContentRepository, SessionContentRepository>();
        services.AddSingleton<IAudioLibraryRepository, AudioLibraryRepository>();
        services.AddSingleton<ITranslationHistoryRepository, TranslationHistoryRepository>();

        // --- 基础服务 ---
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<AzureSubscriptionValidator>();
        services.AddSingleton<IEndpointPlatformDefaultPolicyService, EndpointPlatformDefaultPolicyService>();
        services.AddSingleton<IEndpointProfileCatalogService, EndpointProfileCatalogService>();
        services.AddSingleton<IAiEndpointModelDiscoveryService, AiEndpointModelDiscoveryService>();
        services.AddSingleton<IEndpointTemplateService, EndpointTemplateService>();
        services.AddSingleton<ISettingsImportExportService, SettingsImportExportService>();
        services.AddSingleton<ISettingsTransferFileService, SettingsTransferFileService>();
        services.AddSingleton<IModelRuntimeResolver, ModelRuntimeResolver>();
        services.AddSingleton<ISpeechResourceRuntimeResolver, SpeechResourceRuntimeResolver>();
        services.AddSingleton<IRealtimeConnectionSpecResolver, RealtimeConnectionSpecResolver>();
        services.AddSingleton<IRealtimeTranslationServiceFactory, RealtimeTranslationServiceFactory>();
        services.AddSingleton<IAiAudioTranscriptionService, AiAudioTranscriptionService>();
        services.AddSingleton<IAboutSectionService, AboutSectionService>();
        services.AddSingleton<IEndpointBatchTestService, EndpointBatchTestService>();
        services.AddSingleton<IBatchPackageStateService, BatchPackageStateService>();
        services.AddSingleton<IAzureTokenProviderStore, AzureTokenProviderStore>();

        // --- AI 媒体/洞察服务 ---
        services.AddSingleton<IAiInsightService>(sp =>
        {
            var store = sp.GetRequiredService<IAzureTokenProviderStore>();
            return new AiInsightService(store.GetProvider("ai"));
        });
        services.AddSingleton<IAiImageGenService, AiImageGenService>();
        services.AddSingleton<IAiVideoGenService, AiVideoGenService>();

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

