using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views;

// 只链接生产代码；csproj 的 VoiceImeToolPage / VoiceImeToolsWindow 链接由主 agent 添加。
// 本入口不启动 UIClient/Host、IPC、维护、设备枚举或录音；所有文件均为工具内合成数据。
internal static class SheetInteraction
{
    private const string OriginalText = "合成原文仅供专项验证";
    private static readonly string LongOriginalText = string.Join("\n\n", Enumerable.Repeat(
        "这是为长原文阅读验收准备的合成中文段落，不来自真实录音或用户资料。正文应沿会话详情的整宽自然换行，保留足够的阅读高度；用户滚动内部正文时，页面标题、日期筛选和导航必须留在原处，不能被图表挤窄。段落末尾仍应能滚到并完整读出。", 6))
        + "\n\n长原文合成尾段：阅读已到末尾。";

    internal static int Run()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts/",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-sheets"));
        Directory.CreateDirectory(root);
        Console.WriteLine("ARTIFACTS=" + root);
        var lines = new List<string>();
        var pages = new Dictionary<VoiceImeToolTab, VoiceImeToolPage>();
        var counts = Enum.GetValues<VoiceImeToolTab>().ToDictionary(kind => kind, _ => 0);
        var liveWindows = new HashSet<Window>();
        var seenWindows = new HashSet<Window>();
        VoiceImeToolsWindow? tools = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var succeeded = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var loop = new CancellationTokenSource();
        using var watchdog = deadline.Token.Register(() => loop.Cancel());
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sheet 专项需要 Windows。");
            // 与官方 classic lifetime 相同的 Window 事件计数；PopupRoot 不是 Window，不计入。
            using var openedSubscription = Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
            {
                var window = (Window)sender!;
                liveWindows.Add(window);
                seenWindows.Add(window);
            });
            using var closedSubscription = Window.WindowClosedEvent.AddClassHandler(typeof(Window), (sender, _) =>
                liveWindows.Remove((Window)sender!));
            AppBuilder.Configure<FixtureApplication>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            Dispatcher.UIThread.Post(async () =>
            {
                try { await VerifyAsync(deadline.Token); succeeded = true; }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    // 只请求关闭本入口构造的外壳，并等待其原有异步 flush；不强杀或绕过 Closing。
                    if (tools is { IsVisible: true })
                    {
                        try { tools.Close(); await closed.Task.WaitAsync(deadline.Token); }
                        catch (Exception ex) { failure ??= ex; succeeded = false; }
                    }
                    loop.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(loop.Token);
            if (deadline.IsCancellationRequested)
            {
                succeeded = false;
                failure ??= new TimeoutException("45 秒 Sheet 专项期限已到；未重试或放宽断言。");
            }

            async Task VerifyAsync(CancellationToken token)
            {
                Check(Application.Current is FixtureApplication && TrueFluentPro.App.Services == null,
                    "仅最小 FixtureApplication，无主 DI");
                var path = Path.Combine(root, "config.json");
                var database = Path.Combine(root, "usage.db");
                var initial = new AzureSpeechConfig
                {
                    VoiceIme = new VoiceImeConfig
                    {
                        RecognitionLanguage = "auto", EnableTranslation = false,
                        TargetLanguage = "en-US", EnablePrewarm = true
                    }
                };
                var baseline = JsonSerializer.SerializeToNode(initial)!.AsObject();
                baseline["otherSentinel"] = new JsonObject { ["keep"] = "sheet-fixture-only", ["number"] = 27 };
                File.WriteAllText(path, baseline.ToJsonString());
                Seed(database);
                var databaseHash = Hash(database);
                var walHash = Hash(database + "-wal");
                var loads = 0;
                var saves = 0;
                var saveAttempts = 0;
                var failSave = false;
                var refreshes = 0;
                var systemCalls = 0;
                var shells = 0;
                var report = new VoiceImeUsageReportService(database, new FixedClock());
                var registration = new VoiceImeRegistrationService();
                Task<VoiceImeRegistrationService.Result> FakeSystem()
                {
                    systemCalls++;
                    return Task.FromResult(new VoiceImeRegistrationService.Result(false, "专项不执行系统操作"));
                }
                var settingsVm = new VoiceImeSectionVM(registration, _ => FakeSystem(), FakeSystem, FakeSystem);
                Task<AzureSpeechConfig> Load()
                {
                    Interlocked.Increment(ref loads);
                    return Task.FromResult(baseline.Deserialize<AzureSpeechConfig>()!);
                }
                Task Save(AzureSpeechConfig config)
                {
                    saveAttempts++;
                    if (failSave) return Task.FromException(new IOException("合成设置保存失败；不写入文件。"));
                    // 模拟设置的完整 VoiceIme 快照写入，绝不从磁盘合并语言字段来掩盖陈旧快照。
                    var snapshot = baseline.DeepClone().AsObject();
                    snapshot["VoiceIme"] = JsonSerializer.SerializeToNode(config.VoiceIme);
                    File.WriteAllText(path, snapshot.ToJsonString());
                    saves++;
                    return Task.CompletedTask;
                }
                VoiceImeToolPage PageFactory(VoiceImeToolTab kind)
                {
                    counts[kind]++;
                    VoiceImeToolPage page = kind switch
                    {
                        VoiceImeToolTab.Settings => new VoiceImeSettingsPage(Load, Save, _ => refreshes++, settingsVm, unified: true),
                        VoiceImeToolTab.Language => new VoiceImeLanguagePage(path, autoSave: true),
                        VoiceImeToolTab.Usage => new VoiceImeUsagePage(report, unified: true),
                        _ => throw new ArgumentOutOfRangeException(nameof(kind))
                    };
                    pages.Add(kind, page);
                    return page;
                }
                var manager = new VoiceImeUiWindows(kind =>
                {
                    shells++;
                    var tab = kind switch
                    {
                        VoiceImeUiCommand.Settings => VoiceImeToolTab.Settings,
                        VoiceImeUiCommand.Usage => VoiceImeToolTab.Usage,
                        VoiceImeUiCommand.Language => VoiceImeToolTab.Language,
                        _ => throw new ArgumentOutOfRangeException(nameof(kind))
                    };
                    tools = new VoiceImeToolsWindow(tab, PageFactory);
                    tools.Closed += (_, _) => closed.TrySetResult();
                    return tools;
                });
                Check(manager.Count == 0 && pages.Count == 0 && loads == 0, "manager 构造不读配置、不构造页面");
                var window = (VoiceImeToolsWindow)manager.Open(VoiceImeUiCommand.Settings);
                await Navigation(VoiceImeToolTab.Settings);
                var hwnd = Handle(window);
                var settings = (VoiceImeSettingsPage)pages[VoiceImeToolTab.Settings];
                Check(shells == 1 && manager.Count == 1 && counts[VoiceImeToolTab.Settings] == 1
                    && counts[VoiceImeToolTab.Language] == 0 && counts[VoiceImeToolTab.Usage] == 0
                    && loads == 1 && refreshes == 1 && saves == 0, "Settings Show/LastNavigation/Rendered：只构造设置页一次");
                Check(Field(settings, "_configService") == null && !settingsVm.IsRefreshingMicrophones
                    && settingsVm.MicrophoneStatus == "尚未枚举麦克风。" && systemCalls == 0,
                    "设置 fake load/save 与 no-op refresh 未读取真实配置或设备/登记");
                Cards(settings, 4);
                var prewarm = Find<ToggleSwitch>(settings, "EnablePrewarm");
                Check(prewarm.IsChecked == true, "合成预热初值已绑定真实控件");
                var settingsSheets = ((Control[])Field(settings, "_settingsSheets")!).ToArray();
                var settingsControls = new[] { "SpeechResource", "SpeechModel", "Microphone", "EnablePrewarm", "EndSilenceTimeoutSeconds" }
                    .ToDictionary(name => name, name => Find<Control>(settings, name));
                await SettingsSheets();

                Enter(Find<Button>(window, "VoiceImeTabLanguage"), window);
                await Navigation(VoiceImeToolTab.Language);
                var language = (VoiceImeLanguagePage)pages[VoiceImeToolTab.Language];
                Check((string)Field(language, "_path")! == path && (bool)Field(language, "_autoSave")!, "语言页显式隔离路径与 AutoSave=true");
                var source = Find<ComboBox>(language, "RecognitionLanguage");
                var translate = Find<ToggleSwitch>(language, "EnableTranslation");
                var target = Find<ComboBox>(language, "TargetLanguage");
                var saveLanguage = Find<Button>(language, "SaveLanguage");
                Check(source.SelectedItem is LanguageOption { Code: "auto" }
                    && target.SelectedItem is LanguageOption { Code: "en-US" }
                    && translate.IsChecked == false && !target.IsEffectivelyEnabled && !saveLanguage.IsVisible,
                    "语言真实初始选中/目标禁用/SaveLanguage 隐藏");
                Cards(language, 2);

                source.BringIntoView();
                await Render(window, token);
                // 两个真实控件编辑后，同一 UI 调用栈立即离页，不先 Render 或等防抖。
                SelectDown(source, "zh-CN", window);
                translate.BringIntoView();
                window.UpdateLayout();
                Enter(translate, window);
                Check(translate.IsChecked == true && target.IsEffectivelyEnabled
                    && target.SelectedItem is LanguageOption { Code: "en-US" }, "routed Down/Enter 改语言与翻译，保留有效 en-US 目标");
                PendingLanguage(language);
                Check(JsonNode.DeepEquals(baseline, ReadConfig()), "离页前语言防抖待保存，合成 JSON 尚未改变");
                Enter(Find<Button>(window, "VoiceImeTabSettings"), window);
                await Navigation(VoiceImeToolTab.Settings);
                var expected = baseline.DeepClone();
                expected["VoiceIme"]!["RecognitionLanguage"] = "zh-CN";
                expected["VoiceIme"]!["EnableTranslation"] = true;
                Check(JsonNode.DeepEquals(expected, ReadConfig()) && !saveLanguage.IsVisible
                    && !(bool)Field(language, "_dirty")!, "PrepareToLeave 立即 flush：仅三个语言字段，未知 sentinel 保留、保存按钮隐藏");
                var settingsConfig = (AzureSpeechConfig)Field(settings, "_config")!;
                Check(LanguageMatches(settingsConfig.VoiceIme, true) && settingsVm.SelectedLanguage?.Code == "zh-CN"
                    && saves == 0, "缓存设置 _config 同步实际已保存三字段，未触发虚假设置保存");

                var savedHash = Hash(path);
                failSave = true;
                Enter(prewarm, window);
                Check(prewarm.IsChecked == false && !((Task)Field(settings, "_pendingSaveTask")!).IsCompleted,
                    "预热编辑触发设置待保存任务");
                var settingsDraft = JsonSerializer.SerializeToNode(settingsConfig)!;
                settingsDraft["VoiceIme"]!["EnablePrewarm"] = false; // 编辑先保留在 VM，排到保存时才 ApplyTo。
                // 同栈点击离页；失败开关持续开启到留页、草稿、磁盘与提示断言全部完成。
                Enter(Find<Button>(window, "VoiceImeTabUsage"), window);
                await window.LastNavigation.WaitAsync(token);
                await Render(window, token);
                var notice = Find<TextBlock>(window, "VoiceImeSheetStatus");
                Check(failSave && saveAttempts > 0 && saves == 0 && window.SelectedTab == VoiceImeToolTab.Settings
                    && settings.IsEffectivelyVisible && pages.Values.Count(page => page.IsVisible) == 1
                    && counts[VoiceImeToolTab.Usage] == 0 && liveWindows.Count == 1
                    && seenWindows.Count == 1 && liveWindows.Contains(window),
                    "设置保存失败阻止离页：仍为唯一可见 Settings，未构造 Usage 或额外外壳");
                Check(ReferenceEquals(Field(settings, "_config"), settingsConfig)
                    && JsonNode.DeepEquals(settingsDraft, JsonSerializer.SerializeToNode(settingsConfig))
                    && !settingsConfig.VoiceIme.EnablePrewarm && !settingsVm.EnablePrewarm
                    && prewarm.IsChecked == false && LanguageMatches(settingsConfig.VoiceIme, true),
                    "设置保存失败保留完整配置草稿、预热控件与已同步语言字段");
                Check(Hash(path) == savedHash && JsonNode.DeepEquals(expected, ReadConfig())
                    && notice.IsVisible && VisibleFraction(notice, window) > .99
                    && notice.Text?.Contains("尚未保存", StringComparison.Ordinal) == true,
                    "设置保存失败未改变合成磁盘配置，真实外壳显示未保存提示");
                var failedAttempts = saveAttempts;
                failSave = false;
                Enter(Find<Button>(settings, "RetrySaveSettings"), window);
                await settings.PrepareToLeaveAsync();
                Enter(Find<Button>(window, "VoiceImeTabUsage"), window);
                await Navigation(VoiceImeToolTab.Usage);
                expected["VoiceIme"]!["EnablePrewarm"] = false;
                Check(saveAttempts > failedAttempts && saves == 1 && JsonNode.DeepEquals(expected, ReadConfig()),
                    "重试保存后离页成功：语言三字段与 otherSentinel 未被旧快照覆盖");
                var usage = (VoiceImeUsagePage)pages[VoiceImeToolTab.Usage];
                Check((string)Field(report, "_databasePath")! == database
                    && Find<TextBlock>(usage, "UsageCharacters").Text == "10"
                    && Find<TextBlock>(usage, "UsageSessions").Text == "1", "只读合成双表已实际呈现一个会话");
                Check(usage.SelectedSheet == 0 && (int)Field(usage, "_days")! == 7,
                    "统一用量页默认 7 天概览，先核验真实指标");
                Cards(usage, 10);
                var usageViews = ((Control?[])Field(usage, "_sheetViews")!).ToArray();
                var usageRequest = (long)Field(usage, "_request")!;
                for (var sheet = 0; sheet < 5; sheet++) await UsageSheet(sheet, usageRequest, usageViews);
                Check(counts.Values.All(count => count == 1) && shells == 1 && manager.Count == 1,
                    "三 kind 页面工厂各一次，只有一个 shell");

                Check(ReferenceEquals(manager.Open(VoiceImeUiCommand.Language), window) && Handle(window) == hwnd
                    && manager.Count == 1 && shells == 1, "manager.OpenLanguage 复用同 shell/HWND");
                await Navigation(VoiceImeToolTab.Language);
                // 不等待中间导航，覆盖 latest-request-wins；终态不能只是最后一个任务完成。
                Enter(Find<Button>(window, "VoiceImeTabUsage"), window);
                Enter(Find<Button>(window, "VoiceImeTabLanguage"), window);
                await Navigation(VoiceImeToolTab.Language);
                Check(source.SelectedItem is LanguageOption { Code: "zh-CN" } && translate.IsChecked == true,
                    "快速 Usage→Language 最终语言页及草稿仍正确");

                Enter(Find<Button>(window, "VoiceImeTabUsage"), window);
                await Navigation(VoiceImeToolTab.Usage);
                await UsageSheet(4, usageRequest, usageViews);
                var list = Find<ListBox>(usage, "UsageSessionList");
                Check(list.ItemCount == 1 && list.SelectedItem == null
                    && FindOptional<SelectableTextBlock>(usage, "UsageSessionOriginalText") == null,
                    "汇总初始不加载原文，只有一条合成会话");
                list.BringIntoView();
                await Render(window, token);
                var firstRow = list.ContainerFromIndex(0) ?? throw new InvalidOperationException("缺少实际会话条目容器。");
                firstRow.BringIntoView();
                await Render(window, token);
                RequireInput(firstRow, window); // ListBox 的键盘焦点由真实 ListBoxItem 持有。
                KeyDown(firstRow, Key.Home);
                await WaitVisibleText();
                var selected = list.SelectedItem;
                var original = Find<SelectableTextBlock>(usage, "UsageSessionOriginalText");
                Check(selected is VoiceImeUsageReportSession { SessionId: "sheet-session" }
                    && original.Text == OriginalText, "ListBox routed Home 自动展示合成原文");
                Enter(Find<Button>(window, "VoiceImeTabSettings"), window);
                await Navigation(VoiceImeToolTab.Settings);
                Enter(Find<Button>(window, "VoiceImeTabUsage"), window);
                await Navigation(VoiceImeToolTab.Usage);
                Check(ReferenceEquals(pages[VoiceImeToolTab.Usage], usage)
                    && usage.SelectedSheet == 4 && (long)Field(usage, "_request")! == usageRequest
                    && ReferenceEquals(Find<ListBox>(usage, "UsageSessionList"), list)
                    && ReferenceEquals(list.SelectedItem, selected)
                    && ReferenceEquals(Find<SelectableTextBlock>(usage, "UsageSessionOriginalText"), original)
                    && original.Text == OriginalText, "Settings 往返 Usage：选中行与原文控件身份保留、不重建报表");
                original.BringIntoView();
                await Render(window, token);
                Check(VisibleFraction(original, window) > .99, "合成原文在实际可见视口完整展示");
                UsageLayout();

                // 既有选中行/原文身份往返断言完成后才刷新；后续不再使用旧 ListBox 或原文引用。
                foreach (var days in new[] { 30, 7 })
                {
                    var request = (long)Field(usage, "_request")!;
                    Enter(Find<Button>(usage, "UsageRange" + days), window);
                    var wait = Stopwatch.StartNew();
                    while (!Find<Button>(usage, "UsageRefresh").IsEnabled || !Find<Grid>(usage, "UsageSheetsHost").IsVisible)
                    {
                        if (wait.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("日期筛选刷新未完成。");
                        await Render(window, token);
                    }
                    await Render(window, token);
                    Check((int)Field(usage, "_days")! == days && (long)Field(usage, "_request")! == request + 1
                        && usage.SelectedSheet == 4, "7→30→7 日期刷新一次且保留会话 Sheet：" + days);
                    UsageLayout();
                    // 新快照的概览尚未展示，先真实切入再验证视觉树中的指标，不对隐藏模板做可见性假设。
                    var refreshedViews = ((Control?[])Field(usage, "_sheetViews")!).ToArray();
                    await UsageSheet(0, request + 1, refreshedViews);
                    Check(Find<TextBlock>(usage, "UsageCharacters").Text == (days == 30
                            ? (10 + LongOriginalText.Length).ToString("N0", CultureInfo.GetCultureInfo("zh-CN")) : "10")
                        && Find<TextBlock>(usage, "UsageSessions").Text == (days == 30 ? "2" : "1"),
                        "刷新范围的真实概览指标正确：" + days);
                    await UsageSheet(4, request + 1, refreshedViews);
                }

                window.Width = 760;
                window.Height = 600;
                await Render(window, token);
                foreach (var kind in new[] { VoiceImeToolTab.Usage, VoiceImeToolTab.Settings, VoiceImeToolTab.Language })
                {
                    Enter(Find<Button>(window, "VoiceImeTab" + kind), window);
                    await Navigation(kind);
                    Check(Math.Abs(window.ClientSize.Width - 760) < 1 && Math.Abs(window.ClientSize.Height - 600) < 1,
                        kind + "：最小客户区仍为 760×600");
                    ShellLayout(kind);
                    if (kind == VoiceImeToolTab.Settings)
                    {
                        await SettingsSheets();
                        Capture(window, "pageSettings");
                    }
                    else if (kind == VoiceImeToolTab.Usage)
                    {
                        var views = ((Control?[])Field(usage, "_sheetViews")!).ToArray();
                        var request = (long)Field(usage, "_request")!;
                        for (var sheet = 0; sheet < 5; sheet++) await UsageSheet(sheet, request, views);
                        var currentList = Find<ListBox>(usage, "UsageSessionList");
                        Check(currentList.ItemCount == 1 && currentList.SelectedItem == null,
                            "日期刷新后从当前会话 Sheet 获取未选中的真实 ListBox");
                        var currentRow = currentList.ContainerFromIndex(0) ?? throw new InvalidOperationException("缺少当前会话条目容器。");
                        currentRow.BringIntoView();
                        await Render(window, token);
                        RequireInput(currentRow, window);
                        KeyDown(currentRow, Key.Home);
                        await WaitVisibleText();
                        Check(currentList.SelectedItem is VoiceImeUsageReportSession { SessionId: "sheet-session" }
                            && Find<SelectableTextBlock>(usage, "UsageSessionOriginalText").Text == OriginalText
                            && VisibleFraction(Find<SelectableTextBlock>(usage, "UsageSessionOriginalText"), window) > .99,
                            "760×600 当前会话单条展开原文完整可见");
                        Check(Find<SelectableTextBlock>(usage, "UsageSessionOriginalText").Bounds.Height <= 150,
                            "760×600 短原文高度不超过 150，不为长原文放宽短句布局");
                        UsageLayout();
                        Capture(window, "pageUsage");
                        await LongOriginalReading();
                    }
                    else
                    {
                        CheckScroll(BodyScroll(language), "Language");
                        Capture(window, "pageLanguage");
                    }
                }
                Check(Hash(database) == databaseHash && Hash(database + "-wal") == walHash
                    && loads == 1 && refreshes == 1 && systemCalls == 0 && TrueFluentPro.App.Services == null,
                    "全过程只读合成 DB/WAL、加载一次，无真实配置/设备/维护/主 DI");

                // 关闭也命中尚未到期的语言防抖，必须由生产 Closing flush 后发出 Closed。
                translate.BringIntoView();
                await Render(window, token);
                Enter(translate, window);
                PendingLanguage(language);
                var managerClosed = manager.WaitForCloseAsync();
                Check(!managerClosed.IsCompleted, "关闭前 manager 仍等待唯一外壳");
                Enter(Find<Button>(window, "VoiceImeClose"), window);
                await closed.Task.WaitAsync(token);
                await managerClosed.WaitAsync(token);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                expected["VoiceIme"]!["EnableTranslation"] = false;
                Check(manager.Count == 0 && !window.IsVisible && !IsWindow(hwnd)
                    && (bool)Field(window, "_closed")! && pages.Values.All(page => (bool)Field(page, "_closed")!),
                    "异步关闭完成：Closed、manager.Count=0、HWND 销毁、三页 Dispose/_closed=true");
                Check(JsonNode.DeepEquals(expected, ReadConfig()) && LanguageMatches(settingsConfig.VoiceIme, false)
                    && saves == 1 && counts.Values.All(count => count == 1) && shells == 1
                    && liveWindows.Count == 0 && seenWindows.Count == 1 && seenWindows.Contains(window),
                    "关闭前待保存语言已 flush，缓存设置同步；框架始终只有单一 shell（不计 PopupRoot）");
                Check(Hash(database) == databaseHash && Hash(database + "-wal") == walHash,
                    "关闭后合成 DB/WAL 摘要仍不变");

                JsonNode ReadConfig() => JsonNode.Parse(File.ReadAllText(path))!;
                async Task Navigation(VoiceImeToolTab kind)
                {
                    await window.LastNavigation.WaitAsync(token);
                    await Render(window, token);
                    Check(window.SelectedTab == kind && pages.TryGetValue(kind, out var current)
                        && current.IsEffectivelyVisible && pages.Values.Count(page => page.IsVisible) == 1
                        && !Find<TextBlock>(window, "VoiceImeSheetStatus").IsVisible
                        && liveWindows.Count == 1 && seenWindows.Count == 1 && liveWindows.Contains(window),
                        "LastNavigation/Rendered 与唯一可见页一致：" + kind);
                }
                async Task WaitVisibleText(string expectedText = OriginalText)
                {
                    var wait = Stopwatch.StartNew();
                    while (FindOptional<SelectableTextBlock>(usage, "UsageSessionOriginalText")?.Text != expectedText
                        || !Find<Button>(usage, "UsageViewSessionText").IsEnabled)
                    {
                        if (wait.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("选中会话未展示合成原文。");
                        await Render(window, token);
                    }
                    await Render(window, token);
                }
                async Task LongOriginalReading()
                {
                    // 短句的最小窗验收与截图已完成；长文只读同一 seed，绝不运行中写库。
                    var request = await ReadingRange(30);
                    var views = ((Control?[])Field(usage, "_sheetViews")!).ToArray();
                    await UsageSheet(0, request, views);
                    Check(Find<TextBlock>(usage, "UsageCharacters").Text
                            == (10 + LongOriginalText.Length).ToString("N0", CultureInfo.GetCultureInfo("zh-CN"))
                        && Find<TextBlock>(usage, "UsageSessions").Text == "2",
                        "长原文 30 天真实概览：字数包含长文、两个会话；7 天短句不受影响");

                    window.Width = 1040;
                    window.Height = 820;
                    await Render(window, token);
                    Check(Math.Abs(window.ClientSize.Width - 1040) < 1 && Math.Abs(window.ClientSize.Height - 820) < 1,
                        "长原文阅读显式恢复正常客户区 1040×820，不要求最小窗一次露出全文");
                    await UsageSheet(4, request, views);
                    var currentList = Find<ListBox>(usage, "UsageSessionList");
                    Check(currentList.ItemCount == 2 && currentList.SelectedItem == null
                        && currentList.ItemsSource!.Cast<VoiceImeUsageReportSession>().Select(session => session.SessionId)
                            .SequenceEqual(new[] { "sheet-session", "long-sheet-session" }),
                        "30 天当前会话列表按日期降序：旧会话在前、长会话在第二行，刷新后未选中");
                    var currentRow = currentList.ContainerFromIndex(1)
                        ?? throw new InvalidOperationException("缺少长会话的实际第二行容器。");
                    currentRow.BringIntoView();
                    await Render(window, token);
                    RequireInput(currentRow, window);
                    KeyDown(currentRow, Key.End);
                    await WaitVisibleText(LongOriginalText);
                    var longBody = Find<SelectableTextBlock>(usage, "UsageSessionOriginalText");
                    var textArea = Find<Border>(usage, "UsageSessionTextArea");
                    var textRows = Find<StackPanel>(usage, "UsageSessionTextRows");
                    var rowHost = ((Dictionary<string, StackPanel>)Field(usage, "_sessionRows")!)["long-sheet-session"];
                    Check(currentList.SelectedItem is VoiceImeUsageReportSession { SessionId: "long-sheet-session" }
                        && (string?)Field(usage, "_selectedSessionId") == "long-sheet-session"
                        && longBody.Text == LongOriginalText && ReferenceEquals(Field(usage, "_textArea"), textArea)
                        && ReferenceEquals(textArea.Parent, rowHost) && rowHost.GetVisualAncestors().Contains(currentRow)
                        && textArea.GetVisualAncestors().Contains(currentRow) && textRows.Children.Count == 1
                        && usage.GetVisualDescendants().OfType<Border>().Count(border => border.Name == "UsageSessionTextArea") == 1
                        && !usage.GetVisualDescendants().OfType<SelectableTextBlock>().Any(body => body.Text == OriginalText),
                        "routed End 选中长会话：原文精确匹配、唯一详情只在第二行宿主、短原文已清除");
                    var host = Find<Grid>(usage, "UsageSheetsHost");
                    Check(longBody.TextWrapping == TextWrapping.Wrap && longBody.Bounds.Width >= host.Bounds.Width * .70,
                        "长原文扣除卡片及详情 padding 后仍占右侧宿主至少 70% 整宽，自然换行不被图表挤窄");
                    var textScroll = textArea.GetVisualDescendants().OfType<ScrollViewer>().Single();
                    Check(ReferenceEquals(textScroll.Content, textRows) && longBody.GetVisualAncestors().Contains(textScroll)
                        && textScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                        && textScroll.Extent.Height > textScroll.Viewport.Height + 1,
                        "长原文使用生产内部垂直滚动区，正文 extent 大于 viewport");
                    textScroll.Offset = new Vector(0, 0);
                    await Render(window, token);
                    UsageLayout();
                    CheckScroll(textScroll, "长原文阅读起点");
                    var fixedHeader = Find<StackPanel>(usage, "UsageFixedHeader");
                    var fixedControls = new Control[]
                    {
                        fixedHeader, Find<Grid>(usage, "UsageSheetNavigation"),
                        fixedHeader.Children.OfType<Grid>().Single(grid => grid.Name != "UsageSheetNavigation"),
                        Find<Border>(window, "VoiceImePageHeader")
                    };
                    Point Position(Control control) => control.TranslatePoint(default, window)
                        ?? throw new InvalidOperationException("长原文阅读控件未挂在实际窗口。");
                    var fixedPositions = fixedControls.Select(Position).ToArray();
                    var visibleTextRegion = new Rect(Position(textScroll), textScroll.Bounds.Size);
                    var bodyStart = Position(longBody);
                    Check(Math.Abs(textScroll.Offset.Y) < 1 && textScroll.Viewport.Height > 80
                        && VisibleFraction(textScroll, window) > .99
                        && fixedControls.All(control => VisibleFraction(control, window) > .99
                            && !control.GetVisualAncestors().OfType<ScrollViewer>().Any()),
                        "长原文起点：实际阅读视口完整可见且高于 80，标题/导航/日期工具栏无外层滚动");

                    // 只改变生产 ScrollViewer 的公开 Offset；不改 MaxHeight、模板或布局参数。
                    var endOffset = textScroll.Extent.Height - textScroll.Viewport.Height;
                    textScroll.Offset = new Vector(0, endOffset);
                    await Render(window, token);
                    var bodyEnd = longBody.TranslatePoint(default, textScroll)
                        ?? throw new InvalidOperationException("长原文未在内部滚动区布局。");
                    Check(Math.Abs(textScroll.Offset.Y - endOffset) < 1 && textScroll.Offset.Y > 0
                        && Position(longBody).Y < bodyStart.Y && bodyEnd.Y < 0
                        && bodyEnd.Y + longBody.Bounds.Height > 80
                        && bodyEnd.Y + longBody.Bounds.Height <= textScroll.Viewport.Height + 1
                        && longBody.Text == LongOriginalText,
                        "长原文已滚读到末尾：正文实际上移，全文末端位于阅读视口内且内容不变");
                    Check(new Rect(Position(textScroll), textScroll.Bounds.Size) == visibleTextRegion
                        && fixedControls.Select(Position).SequenceEqual(fixedPositions)
                        && textScroll.Viewport.Height > 80 && VisibleFraction(textScroll, window) > .99,
                        "滚读前后阅读区域、固定页头、导航、日期栏及窗口标题位置完全相同");
                    CheckScroll(textScroll, "长原文阅读末尾");
                    UsageLayout(); // 主卡仍填满宿主、外层 ListBox 无溢出，沿用原严格断言。
                    Capture(window, "pageUsageLong");

                    request = await ReadingRange(7);
                    views = ((Control?[])Field(usage, "_sheetViews")!).ToArray();
                    await UsageSheet(0, request, views);
                    Check(Find<TextBlock>(usage, "UsageCharacters").Text == "10"
                        && Find<TextBlock>(usage, "UsageSessions").Text == "1", "长原文阅读后回到 7 天，真实概览仍为 10 字 / 1 会话");
                    await UsageSheet(4, request, views);
                    Check(Find<ListBox>(usage, "UsageSessionList").ItemCount == 1
                        && Find<ListBox>(usage, "UsageSessionList").SelectedItem == null
                        && FindOptional<SelectableTextBlock>(usage, "UsageSessionOriginalText") == null,
                        "7 天恢复新会话快照，长原文已清除，后续不使用旧 ListBox");
                    window.Width = 760;
                    window.Height = 600;
                    await Render(window, token);
                    Check(Math.Abs(window.ClientSize.Width - 760) < 1 && Math.Abs(window.ClientSize.Height - 600) < 1,
                        "长原文验收结束恢复 760×600，继续原设置与语言专项");
                    UsageLayout();
                }
                async Task<long> ReadingRange(int days)
                {
                    var request = (long)Field(usage, "_request")!;
                    Enter(Find<Button>(usage, "UsageRange" + days), window);
                    var wait = Stopwatch.StartNew();
                    while (!Find<Button>(usage, "UsageRefresh").IsEnabled || !Find<Grid>(usage, "UsageSheetsHost").IsVisible)
                    {
                        if (wait.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("长原文日期筛选刷新未完成。");
                        await Render(window, token);
                    }
                    await Render(window, token);
                    Check((int)Field(usage, "_days")! == days && (long)Field(usage, "_request")! == request + 1
                        && usage.SelectedSheet == 4, "长原文日期刷新一次且保留会话 Sheet：" + days);
                    return request + 1;
                }
                async Task SettingsSheets()
                {
                    var editVersion = (long)Field(settings, "_editVersion")!;
                    var attempts = saveAttempts;
                    var root = (Grid)settings.Content!;
                    var scroll = BodyScroll(settings);
                    var section = (StackPanel)Field(settings, "_sectionView")!;
                    var host = (Grid)section.Children.Single();
                    Check(root.RowDefinitions.Count == 3 && root.RowDefinitions[0].Height == GridLength.Auto
                        && root.RowDefinitions[1].Height == GridLength.Star && root.RowDefinitions[2].Height == GridLength.Auto
                        && ReferenceEquals(root.Children[1], scroll) && Grid.GetRow(scroll) == 1
                        && scroll.Name == "SettingsSheetScroll" && host.Name == "SettingsSheetsHost" && host.Children.Count == 3,
                        "Settings 固定三项页头、单一 Sheet 宿主与星号行滚动正文");
                    foreach (var sheet in new[] { 0, 1, 2, 0 })
                    {
                        var names = new[] { "Recognition", "Segmentation", "Management" };
                        Enter(Find<Button>(settings, "SettingsSheet" + names[sheet]), window);
                        await Render(window, token);
                        Check(settings.SelectedSheet == sheet && host.Children.Count(view => view.IsVisible) == 1
                            && host.Children.Count(view => view.IsEffectivelyVisible) == 1
                            && host.Children[sheet].IsEffectivelyVisible
                            && settingsSheets.SequenceEqual(host.Children)
                            && settingsSheets.SequenceEqual((Control[])Field(settings, "_settingsSheets")!)
                            && settingsControls.All(pair => ReferenceEquals(pair.Value, Find<Control>(settings, pair.Key)))
                            && (long)Field(settings, "_editVersion")! == editVersion && saveAttempts == attempts,
                            "Settings Enter 切 Sheet：唯一可见、控件身份不变、无编辑或保存 / " + names[sheet]);
                        for (var index = 0; index < names.Length; index++)
                            SheetButton(Find<Button>(settings, "SettingsSheet" + names[index]), index == sheet);
                        CheckScroll(scroll, "Settings " + names[sheet]);
                        if (sheet == 0)
                        {
                            var recognition = (Grid)host.Children[0];
                            var resource = (Border)recognition.Children[0];
                            var audio = (Border)recognition.Children[1];
                            Check(recognition.Name == "SettingsSheetRecognitionContent" && recognition.ColumnDefinitions.Count == 2
                                && recognition.ColumnDefinitions.All(column => column.Width == GridLength.Star)
                                && resource.Name == "SettingsRecognitionResourceCard" && audio.Name == "SettingsRecognitionAudioCard"
                                && Grid.GetColumn(resource) == 0 && Grid.GetColumn(audio) == 1
                                && resource.Bounds.Width > 0 && audio.Bounds.Width > 0
                                && resource.Bounds.Right <= audio.Bounds.Left && Math.Abs(resource.Bounds.Top - audio.Bounds.Top) < 1
                                && audio.Bounds.Right <= recognition.Bounds.Width + 1,
                                "识别页两个实际卡片保持横向双列，不覆盖、不退成纵向");
                        }
                        ShellLayout(VoiceImeToolTab.Settings);
                    }
                }
                async Task UsageSheet(int sheet, long request, Control?[] views)
                {
                    var names = new[] { "Overview", "Daily", "Hours", "Distribution", "Sessions" };
                    Enter(Find<Button>(usage, "UsageSheet" + names[sheet]), window);
                    await Render(window, token);
                    Check(usage.SelectedSheet == sheet && (long)Field(usage, "_request")! == request
                        && views.SequenceEqual((Control?[])Field(usage, "_sheetViews")!),
                        "Usage Enter 切 Sheet 不重新读库或重建视图 / " + names[sheet]);
                    UsageLayout();
                }
                void UsageLayout()
                {
                    var root = (Grid)usage.Content!;
                    var host = (Grid)Field(usage, "_sheetHost")!;
                    var views = (Control?[])Field(usage, "_sheetViews")!;
                    var header = Find<StackPanel>(usage, "UsageFixedHeader");
                    var navigation = Find<Grid>(usage, "UsageSheetNavigation");
                    Check(root.RowDefinitions.Count == 4 && root.RowDefinitions[0].Height == GridLength.Auto
                        && root.RowDefinitions[1].Height == GridLength.Auto && root.RowDefinitions[2].Height == GridLength.Star
                        && root.RowDefinitions[3].Height == GridLength.Auto && ReferenceEquals(root.Children[0], header)
                        && ReferenceEquals(root.Children[2], host) && Grid.GetRow(host) == 2
                        && host.Name == "UsageSheetsHost" && host.Margin == new Thickness(24, 0, 24, 16)
                        && host.IsEffectivelyVisible && host.Children.Count == 5 && views.SequenceEqual(host.Children)
                        && views.Select((view, index) => view != null && view.Name == "UsageSheetContent" + index
                            && view.IsVisible == (index == usage.SelectedSheet)
                            && (index < 4 ? view is ScrollViewer : view is Border)).All(valid => valid)
                        && host.Children.Count(view => view.IsEffectivelyVisible) == 1
                        && views[usage.SelectedSheet]!.IsEffectivelyVisible,
                        "Usage 固定页头与五 Sheet：只有当前视图可见，正文星号行不外滚");
                    Check(navigation.Children.Count == 5 && VisibleFraction(navigation, window) > .99,
                        "Usage 五项 Sheet 导航完整可见");
                    var names = new[] { "Overview", "Daily", "Hours", "Distribution", "Sessions" };
                    for (var index = 0; index < names.Length; index++)
                        SheetButton(Find<Button>(usage, "UsageSheet" + names[index]), index == usage.SelectedSheet);
                    foreach (var days in new[] { 1, 7, 30 })
                    {
                        var button = Find<Button>(usage, "UsageRange" + days);
                        Check(button.GetVisualAncestors().Contains(header) && !button.GetVisualAncestors().Contains(host)
                            && !button.GetVisualAncestors().OfType<ScrollViewer>().Any() && VisibleFraction(button, window) > .99,
                            "日期筛选在正文宿主与滚动区之外完整可见：" + days);
                    }
                    if (usage.SelectedSheet < 4) CheckScroll(BodyScroll(usage), "Usage " + names[usage.SelectedSheet]);
                    else
                    {
                        var card = (Border)views[4]!;
                        var rows = (Grid)card.Child!;
                        var currentList = Find<ListBox>(usage, "UsageSessionList");
                        var listScroll = currentList.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll =>
                            !scroll.GetVisualAncestors().TakeWhile(ancestor => ancestor != currentList).OfType<ScrollViewer>().Any());
                        lines.Add($"LAYOUT host={host.Bounds}; card={card.Bounds}, visible={VisibleFraction(card, window):F4}; rows={rows.Bounds}; list={currentList.Bounds}, visible={VisibleFraction(currentList, window):F4}, align={currentList.VerticalAlignment}, max={currentList.MaxHeight}, parent={ReferenceEquals(currentList.Parent, rows)}; star={rows.RowDefinitions[2].ActualHeight:F4}; cardAlign={card.VerticalAlignment}; rowKinds={string.Join(',', rows.RowDefinitions.Select(row => row.Height))}");
                        Check(!card.GetVisualAncestors().TakeWhile(ancestor => ancestor != usage).OfType<ScrollViewer>().Any()
                            && host.Bounds.Width > 0 && host.Bounds.Height > 0 && Math.Abs(card.Bounds.X) < 1 && Math.Abs(card.Bounds.Y) < 1
                            && Math.Abs(card.Bounds.Width - host.Bounds.Width) < 1 && Math.Abs(card.Bounds.Height - host.Bounds.Height) < 1
                            && VisibleFraction(card, window) > .99 && ReferenceEquals(currentList.Parent, rows)
                            && rows.RowDefinitions.Count == 4 && rows.RowDefinitions[0].Height == GridLength.Auto
                            && rows.RowDefinitions[1].Height == GridLength.Auto && rows.RowDefinitions[3].Height == GridLength.Auto
                            && Grid.GetRow(currentList) == 2 && rows.RowDefinitions[2].Height == GridLength.Star
                            && double.IsPositiveInfinity(currentList.MaxHeight)
                            && Math.Abs(currentList.Bounds.Width - rows.Bounds.Width) < 1
                            // 11.3.11 的 ActualHeight 是相邻 FinalOffset 差，含 RowSpacing；控件 cellRect 不含它。
                            && Math.Abs(currentList.Bounds.Height - (rows.RowDefinitions[2].ActualHeight - rows.RowSpacing)) < 1
                            && VisibleFraction(currentList, window) > .99,
                            "Sessions 当前主卡填满右侧 body host，ListBox 受星号行约束而非整页外滚");
                        CheckScroll(listScroll, "Usage Sessions ListBox");
                        Check(listScroll.Extent.Height <= listScroll.Viewport.Height + 1,
                            "合成单条会话及展开正文不超出当前 ListBox 高度视口");
                    }
                    ShellLayout(VoiceImeToolTab.Usage);
                }
                void SheetButton(Button button, bool selected)
                {
                    Check(button.Tag is string label && button.Content is Grid content
                        && content.Children.OfType<TextBlock>().Any(text => text.Text == label)
                        && VisibleFraction(button, window) > .99 && button.Classes.Contains("accent") == selected
                        && AutomationProperties.GetName(button) == label + (selected ? "，已选中" : ""),
                        "Sheet 导航实际标签、选中状态与可见性：" + button.Name);
                }
                void ShellLayout(VoiceImeToolTab kind)
                {
                    var root = (Grid)window.Content!;
                    var columns = Find<Grid>(window, "VoiceImeToolColumns");
                    var sidebar = Find<Border>(window, "VoiceImeSidebar");
                    var workspace = Find<Grid>(window, "VoiceImeWorkspace");
                    Check(root.RowDefinitions.Count == 2 && root.RowDefinitions[0].Height == GridLength.Auto
                        && root.RowDefinitions[1].Height == GridLength.Star && ReferenceEquals(root.Children[1], columns)
                        && Grid.GetRow(columns) == 1 && columns.ColumnDefinitions.Count == 2
                        && columns.ColumnDefinitions[0].Width == new GridLength(176) && columns.ColumnDefinitions[1].Width == GridLength.Star
                        && ReferenceEquals(columns.Children[0], sidebar) && ReferenceEquals(columns.Children[1], workspace)
                        && Grid.GetColumn(sidebar) == 0 && Grid.GetColumn(workspace) == 1 && sidebar.Width == 176
                        && Math.Abs(sidebar.Bounds.Width - 176) < 1 && Math.Abs(columns.ColumnDefinitions[0].ActualWidth - 176) < 1
                        && sidebar.Bounds.Right <= workspace.Bounds.Left && workspace.Bounds.Width > 0
                        && VisibleFraction(sidebar, window) > .99
                        && Math.Abs(Find<Border>(window, "VoiceImePageHeader").Bounds.Height - 38) < 1,
                        kind + "：固定 176 左侧分类与右工作区不覆盖，紧凑标题栏 38");
                    foreach (var (tab, label) in new[]
                    {
                        (VoiceImeToolTab.Usage, "用量"), (VoiceImeToolTab.Language, "语言与翻译"), (VoiceImeToolTab.Settings, "设置")
                    })
                    {
                        var button = Find<Button>(window, "VoiceImeTab" + tab);
                        Check(Equals(button.Tag, label) && button.Content is Grid content
                            && content.Children.OfType<TextBlock>().Any(text => text.Text == label)
                            && button.GetVisualAncestors().Contains(sidebar) && VisibleFraction(button, window) > .99
                            && AutomationProperties.GetName(button) == label + (kind == tab ? "，已选中" : ""),
                            kind + "：侧栏导航完整可见且 Tag/Name 标签正确 / " + tab);
                    }
                }
                void CheckScroll(ScrollViewer scroll, string name)
                {
                    var body = (Control)scroll.Content!;
                    var origin = body.TranslatePoint(default, scroll) ?? throw new InvalidOperationException("正文不在当前页视口内。");
                    Check(scroll.IsEffectivelyVisible && VisibleFraction(scroll, window) > .99
                        && scroll.Viewport.Width > 0 && scroll.Viewport.Height > 0
                        && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                        && scroll.Extent.Width <= scroll.Viewport.Width + 1 && Math.Abs(scroll.Offset.X) < 1
                        && scroll.Offset.Y >= 0 && scroll.Offset.Y <= Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) + 1
                        && origin.X >= -1 && origin.X + body.Bounds.Width <= scroll.Viewport.Width + 1,
                        name + "：当前正文无横向溢出，保留真实滚动视口");
                }
            }
        }
        catch (Exception ex) { failure = ex; succeeded = false; }
        finally
        {
            var summary = succeeded ? "PASS sheets" : "FAIL sheets: " + (failure?.ToString() ?? "未完成");
            lines.Add(summary);
            if (tools is { IsVisible: true }) lines.Add("关闭未完成；未强杀或跳过保存。");
            lines.Add("BOUNDARY: linked production pages; real Avalonia/Win32 + routed keys; synthetic config/2-table SQLite only; no user data/audio/cloud/registration/IPC.");
            lines.Add("SCREENSHOTS: pageSettings.png / pageUsage.png (Sessions 4) / pageLanguage.png when available; RenderTargetBitmap of actual shell, not physical screen; visual review pending.");
            lines.Add("LONG SCREENSHOT: pageUsageLong.png when available; 1040×820 / 30 days / long-sheet-session / production text ScrollViewer at end; visual review pending.");
            File.WriteAllLines(Path.Combine(root, "result.txt"), lines);
            Console.WriteLine(summary);
            Console.WriteLine("RESULT=" + Path.Combine(root, "result.txt"));
        }
        return succeeded ? 0 : 1;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            lines.Add("PASS " + message);
            Console.WriteLine("PASS " + message);
        }
        void PendingLanguage(VoiceImeLanguagePage page)
            => Check((bool)Field(page, "_dirty")! && Field(page, "_saveDebounceCts") is CancellationTokenSource { IsCancellationRequested: false }
                && !((Task)Field(page, "_pendingSave")!).IsCompleted, "语言编辑仍在 200ms 自动保存防抖内");
        void Cards(VoiceImeToolPage page, int minimum)
        {
            // 只选生产 helper 创建的正文直接卡片，排除模板 Border、页头/页脚及嵌套说明框。
            IEnumerable<Border> cards;
            if (page is VoiceImeSettingsPage)
            {
                var section = (StackPanel)Field(page, "_sectionView")!;
                var host = (Grid)section.Children.Single();
                cards = host.Children.SelectMany(view => view is Grid grid
                    ? grid.Children.OfType<Border>() : new[] { (Border)view });
            }
            else if (page is VoiceImeUsagePage)
            {
                cards = ((Control?[])Field(page, "_sheetViews")!).SelectMany(view =>
                {
                    var content = view is ScrollViewer scroll ? (Control)scroll.Content! : view!;
                    return content switch
                    {
                        Border card => new[] { card },
                        Grid grid => grid.Children.OfType<Border>(),
                        StackPanel overview => overview.Children.OfType<Grid>().SelectMany(grid => grid.Children.OfType<Border>())
                            .Concat(overview.Children.OfType<Border>()),
                        _ => throw new InvalidOperationException("未知用量 Sheet 卡片结构。")
                    };
                });
            }
            else cards = ((StackPanel)BodyScroll(page).Content!).Children.OfType<Border>();
            var actualCards = cards.ToArray();
            Check(actualCards.Length == minimum && actualCards.All(card => card.CornerRadius == new CornerRadius(12)
                && card.Background is ISolidColorBrush fill && fill.Color == Colors.White
                && card.BorderBrush is ISolidColorBrush stroke && stroke.Color == ((ISolidColorBrush)VoiceImeWindowDesign.Line).Color
                && card.BorderThickness == new Thickness(1)), page.GetType().Name + " 正文卡片：统一白底/线色/Radius12");
        }
        void Enter(Button button, Window window)
        {
            RequireInput(button, window);
            var clicks = 0;
            void Observe(object? sender, RoutedEventArgs args) { if (ReferenceEquals(args.Source, button)) clicks++; }
            window.AddHandler(Button.ClickEvent, Observe, RoutingStrategies.Bubble, handledEventsToo: true);
            try { KeyDown(button, Key.Enter); Check(clicks == 1, "routed Enter → 一次真实 Click：" + button.Name); }
            finally { window.RemoveHandler(Button.ClickEvent, Observe); }
        }
        void SelectDown(ComboBox box, string code, Window window)
        {
            RequireInput(box, window);
            var items = box.ItemsSource!.Cast<LanguageOption>().ToArray();
            var index = Array.FindIndex(items, item => item.Code == code);
            Check(index > box.SelectedIndex && box.SelectedIndex >= 0, "实际 ComboBox 目标位于当前项下方：" + code);
            var steps = index - box.SelectedIndex;
            for (var step = 0; step < steps; step++) KeyDown(box, Key.Down);
            Check(box.SelectedItem is LanguageOption option && option.Code == code, "实际 ComboBox routed Down 选中：" + code);
        }
        void RequireInput(Control control, Window window)
            => Check(control.GetVisualAncestors().Contains(window) && control.IsEffectivelyEnabled
                && VisibleFraction(control, window) > .99 && control.Focus(), "输入仅针对自身实际可见控件：" + (control.Name ?? control.GetType().Name));
        void KeyDown(Control control, Key key)
        {
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control };
            control.RaiseEvent(input);
            Check(input.Handled, "真实控件已处理 routed " + key);
        }
        void Capture(Window window, string name)
        {
            try
            {
                var scale = window.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.ClientSize.Width * scale),
                    (int)Math.Ceiling(window.ClientSize.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(root, name + ".png"));
                lines.Add("SCREENSHOT " + name + ".png (actual shell RenderTargetBitmap; visual review pending)");
            }
            catch (Exception ex) { lines.Add("SCREENSHOT unavailable " + name + ": " + ex.Message); }
        }
    }

    private static bool LanguageMatches(VoiceImeConfig config, bool translation)
        => config.RecognitionLanguage == "zh-CN" && config.EnableTranslation == translation && config.TargetLanguage == "en-US";
    private static Task Render(Window window, CancellationToken token) => VoiceImeToolPage.RenderedAsync(window, token);
    private static ScrollViewer BodyScroll(VoiceImeToolPage page) => page is VoiceImeUsagePage usage
        ? (ScrollViewer)((Control?[])Field(usage, "_sheetViews")!)[usage.SelectedSheet]!
        : ((Grid)page.Content!).Children.OfType<ScrollViewer>().Single();
    private static T? FindOptional<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);
    private static T Find<T>(Control root, string name) where T : Control
        => FindOptional<T>(root, name) ?? throw new InvalidOperationException("当前真实页缺少控件：" + name);
    private static object? Field(object target, string name)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);
    private static IntPtr Handle(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !IsWindow(handle) || !window.IsVisible
            || GetWindowThreadProcessId(handle, out var pid) == 0 || pid != (uint)Environment.ProcessId)
            throw new InvalidOperationException("外壳缺少当前工具进程的真实 Win32 HWND。");
        return handle;
    }
    private static double VisibleFraction(Control control, Window window)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return 0;
        var origin = control.TranslatePoint(default, window);
        if (origin == null) return 0;
        var bounds = new Rect(origin.Value, control.Bounds.Size);
        var visible = bounds.Intersect(new Rect(window.ClientSize));
        foreach (var ancestor in control.GetVisualAncestors().TakeWhile(ancestor => ancestor != window).Where(ancestor => ancestor.ClipToBounds))
        {
            var point = ancestor.TranslatePoint(default, window);
            if (point != null) visible = visible.Intersect(new Rect(point.Value, ancestor.Bounds.Size));
        }
        return Math.Max(0, visible.Width) * Math.Max(0, visible.Height) / (bounds.Width * bounds.Height);
    }
    private static string Hash(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    private static void Seed(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE voice_ime_sessions(session_id TEXT PRIMARY KEY, owner_scope TEXT NOT NULL,
                created_utc TEXT NOT NULL, created_local_date TEXT NOT NULL, created_offset_minutes INTEGER NOT NULL,
                recording_started_utc TEXT, checkpoint_utc TEXT NOT NULL, checkpoint_local_date TEXT NOT NULL,
                checkpoint_offset_minutes INTEGER NOT NULL, ended_utc TEXT, recording_ms INTEGER NOT NULL,
                state TEXT NOT NULL, stop_reason TEXT, revision INTEGER NOT NULL);
            CREATE TABLE voice_ime_utterances(session_id TEXT NOT NULL, sequence_no INTEGER NOT NULL,
                finalized_utc TEXT NOT NULL, local_date TEXT NOT NULL, utc_offset_minutes INTEGER NOT NULL,
                character_count INTEGER NOT NULL, original_text TEXT, PRIMARY KEY(session_id,sequence_no));
            INSERT INTO voice_ime_sessions VALUES('sheet-session','sheet-fixture',@utc,'2026-11-07',480,
                @utc,@utc,'2026-11-07',480,@utc,10000,'ended','manual_stop',1);
            INSERT INTO voice_ime_utterances VALUES('sheet-session',1,@utc,'2026-11-07',480,10,@text);
            INSERT INTO voice_ime_sessions VALUES('long-sheet-session','sheet-fixture',@longUtc,'2026-10-20',480,
                @longUtc,@longUtc,'2026-10-20',480,@longUtc,10000,'ended','manual_stop',1);
            INSERT INTO voice_ime_utterances VALUES('long-sheet-session',1,@longUtc,'2026-10-20',480,@longCharacters,@longText);
            """;
        command.Parameters.AddWithValue("@utc", "2026-11-07T04:00:00.0000000+00:00");
        command.Parameters.AddWithValue("@text", OriginalText);
        command.Parameters.AddWithValue("@longUtc", "2026-10-20T04:00:00.0000000+00:00");
        command.Parameters.AddWithValue("@longCharacters", LongOriginalText.Length);
        command.Parameters.AddWithValue("@longText", LongOriginalText);
        command.ExecuteNonQuery();
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 7, 4, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("SheetFixture+8", TimeSpan.FromHours(8), "SheetFixture+8", "SheetFixture+8");
    }
    private sealed class FixtureApplication : Application
    {
        public override void Initialize() => VoiceImeUiTheme.Apply(this);
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}