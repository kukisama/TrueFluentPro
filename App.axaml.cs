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
        services.AddSingleton<ILegacyImportService, LegacyImportService>();

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

        // --- SQLite 启动初始化 ---
        InitializeSqliteStorage();

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeSqliteStorage()
    {
        try
        {
            // 1. 确保数据库和表结构已创建
            var db = Services.GetRequiredService<ISqliteDbService>();
            db.EnsureCreated();

            // 2. 执行老资源导入（幂等，已存在的记录会被跳过）
            var importService = Services.GetRequiredService<ILegacyImportService>();
            importService.ImportAll();

            // 3. 启用全部功能开关 —— 进入 SQLite 主路径
            var switches = Services.GetRequiredService<SqliteFeatureSwitches>();
            switches.UseSqliteSessionList = true;
            switches.UseSqliteSessionWrite = true;
            switches.UseSqliteMessagePaging = true;
            switches.UseSqliteAssetCatalog = true;
            switches.UseSqliteWorkspaceWrite = true;
            switches.UseSqliteAudioIndexWrite = true;
            switches.EnableLegacyImport = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SQLite] 初始化失败: {ex.Message}");
        }
    }
}

