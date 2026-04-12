using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Services;
using TrueFluentPro.Services.EndpointProfiles;
using TrueFluentPro.Services.EndpointTesting;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static readonly CancellationTokenSource _appShutdownCts = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLogger.HookAvaloniaUiThread();

        Services = BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) => _appShutdownCts.Cancel();
            var mainWindow = new MainWindow();
            var configService = Services.GetRequiredService<ConfigurationService>();
            var startupShellPreferencesTask = Task.Run(configService.LoadShellStartupPreferences);
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var startupShellPreferences = startupShellPreferencesTask.GetAwaiter().GetResult();
            vm.ApplyStartupShellPreferences(startupShellPreferences);
            vm.SetMainWindow(mainWindow);
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;
        }

        _ = Task.Run(InitializeSqliteStorage);

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServiceProvider()
    {
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
        services.AddSingleton<IAudioLifecycleRepository, AudioLifecycleRepository>();
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

        // --- 音频生命周期 TTS 服务 ---
        services.AddSingleton<Services.Speech.SpeechSynthesisService>();
        services.AddSingleton<AudioLifecyclePipelineService>();

        // --- 音频任务队列 ---
        services.AddSingleton<IAudioTaskRepository, AudioTaskRepository>();
        services.AddSingleton<ITaskEventBus, TaskEventBus>();
        services.AddSingleton<AudioTaskStageHandlerService>();
        services.AddSingleton<IAudioTaskQueueService, AudioTaskQueueService>();
        services.AddSingleton<IAudioTaskExecutor, AudioTaskExecutor>();

        // --- ViewModel ---
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
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

            // 3. 标记 SQLite 就绪 —— 所有读写进入 SQLite 主路径
            var switches = Services.GetRequiredService<SqliteFeatureSwitches>();
            switches.IsReady = true;

            // 4. 启动音频任务执行器 —— 后台调度循环
            var executor = Services.GetRequiredService<IAudioTaskExecutor>();
            var queueService = Services.GetRequiredService<IAudioTaskQueueService>();

            // 将队列服务的 NewTaskEnqueued 事件连接到执行器
            if (queueService is AudioTaskQueueService concreteQueue)
            {
                concreteQueue.NewTaskEnqueued += () => executor.NotifyNewTask();
            }

            _ = Task.Run(() => executor.StartAsync(_appShutdownCts.Token));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SQLite] 初始化失败: {ex.Message}");
        }
    }
}

