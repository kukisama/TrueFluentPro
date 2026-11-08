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
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views;

internal static class Program
{
    private static string Root = "";
    private static StreamWriter? LogFile;
    private static readonly List<Window> Owned = new();
    private static readonly List<object> Measurements = new();
    private static readonly List<string> Screenshots = new();
    private static readonly List<string> Passes = new();
    private static readonly List<string> LayoutWarnings = new();
    private static readonly FixedClock Clock = new();
    private static readonly List<string> Gaps = new()
    {
        "真实 Win32/Avalonia 窗口 + 框架 routed KeyDown/Click/选择；标题栏按钮只验真实 routed Click 与原生状态，不使用物理 SendInput；未验标题拖动、双击、鼠标/原生边缘命中或系统键盘派发。",
        "PNG 为 Show 后等待 CompositionBatch.Rendered 的实际窗口 RenderTargetBitmap；非物理桌面/DWM 截图。",
        "只测本机实际 RenderScaling 与窗口缩小，不代表多显示器或 200%/400% DPI。",
        "未读写真实配置或真实数据库，未启动主窗/主 DI、音频、云端、Host、TSF、注册/安装或 UAC；未关闭用户程序。",
        "设置交互覆盖预热开关与识别语言；句尾静音只核对加载呈现，未验证数值编辑；引用刷新仅调用 LoadFrom，不执行设备枚举或维护命令。",
        "只验证绑定、持久化、报表、基础布局与最小标题栏契约；Win11 顶沿使用工具自有窗口的屏幕像素定向检查，不代表整页桌面视觉验收；不覆盖全部错误态、生产录音/翻译或跨主题行为。"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        Root = Path.GetFullPath(args[0]);
        var tool = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
        if (!Root.StartsWith(tool + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(Root);
        using var log = new StreamWriter(Path.Combine(Root, "assertions.log")) { AutoFlush = true };
        LogFile = log;
        var code = 1;
        Exception? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var loop = new CancellationTokenSource();
        using var watchdog = deadline.Token.Register(() => loop.Cancel());
        try
        {
            var production = Path.GetFullPath(args[1]);
            var loaded = typeof(VoiceImeUsageWindow).Assembly;
            Check(Hash(production) == Hash(loaded.Location), "实际加载生产 DLL 副本 SHA256 与指定 Debug DLL 一致");
            Write("ASSEMBLY=" + loaded.Location + "; sha256=" + Hash(production) + "; mvid=" + loaded.ManifestModule.ModuleVersionId);
            Write("AVALONIA=" + typeof(Application).Assembly.FullName);
            IconProvider.Current.Register<FontAwesomeIconProvider>();
            AppBuilder.Configure<FixtureApplication>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            Check(TrueFluentPro.App.Services == null, "未初始化生产主 DI/后台服务");
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var usage = await VerifyUsageAsync(deadline.Token);
                    await VerifyLanguageAsync(usage, deadline.Token);
                    usage.Close();
                    await VerifySettingsAsync(deadline.Token);
                    Check(TrueFluentPro.App.Services == null, "验收结束仍未启动生产主 DI");
                    code = 0;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    Write("FAIL=" + ex);
                    foreach (var window in Owned.Where(w => w.IsVisible && w.WindowState != WindowState.Minimized).ToArray())
                    {
                        try { Capture(window, "failure-" + window.GetType().Name); }
                        catch (Exception captureError) { Write("FAILURE_CAPTURE=" + captureError.Message); }
                    }
                }
                finally
                {
                    foreach (var window in Owned.Where(w => w.IsVisible).ToArray()) window.Close();
                    loop.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(loop.Token);
            if (deadline.IsCancellationRequested)
            {
                code = 1;
                failure ??= new TimeoutException("90 秒桌面验收期限已到；不重试交互。");
                foreach (var window in Owned.Where(w => w.IsVisible).ToArray()) window.Close();
            }
        }
        catch (Exception ex) { failure = ex; Write("FAIL=" + ex); }
        finally
        {
            var result = new { exitCode = code, failure = failure?.ToString(), passes = Passes, layoutWarnings = LayoutWarnings,
                measurements = Measurements, screenshots = Screenshots, gaps = Gaps };
            File.WriteAllText(Path.Combine(Root, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var gap in Gaps) Write("GAP=" + gap);
            Write("EXIT_CODE=" + code);
        }
        return code;
    }

    private static async Task<Window> VerifyUsageAsync(CancellationToken token)
    {
        var database = Path.Combine(Root, "isolated-usage.db");
        SeedDatabase(database);
        var before = Hash(database);
        var service = new VoiceImeUsageReportService(database, Clock);
        var constructor = typeof(VoiceImeUsageWindow).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(VoiceImeUsageReportService) }, null) ?? throw new MissingMethodException("用量隔离构造");
        var window = (Window)constructor.Invoke(new object[] { service });
        Owned.Add(window);
        Check(ReferenceEquals(Field(window, "_service"), service) && (string)Field(service, "_databasePath") == database,
            "生产用量窗口服务严格指向项目内隔离 SQLite");
        window.Show();
        await WaitAsync(window, () => FindOptional<TextBlock>(window, "UsageCharacters")?.Text == "325", token);
        RequireWindow(window);
        await ReportAsync(window, 7, 325, 3, 2, "3 分 00 秒", 3, token);
        await VerifyTitleBarAsync(window, token);
        Capture(window, "usage-default-7days");
        await ClickAsync(window, Find<Button>(window, "UsageRange1"), token);
        await ReportAsync(window, 1, 125, 2, 1, "1 分 00 秒", 2, token);
        await ClickAsync(window, Find<Button>(window, "UsageRange30"), token);
        await ReportAsync(window, 30, 625, 4, 3, "6 分 00 秒", 4, token);
        await ClickAsync(window, Find<Button>(window, "UsageRange7"), token);
        await ReportAsync(window, 7, 325, 3, 2, "3 分 00 秒", 3, token);
        Check(Hash(database) == before, "范围切换与显示均未改变隔离 DB 字节");

        // 对照组：fixture 独立提交一次新定稿，显示必须维持旧快照，点击刷新后才变化。
        using (var writer = Open(database)) Utterance(writer, "today", 2, "2026-11-06", 7);
        var committed = Hash(database);
        Check(Find<TextBlock>(window, "UsageCharacters").Text == "325", "无自动轮询：写入后报表仍为旧快照");
        await ClickAsync(window, Find<Button>(window, "UsageRefresh"), token);
        await ReportAsync(window, 7, 332, 4, 2, "3 分 00 秒", 3, token);
        Check(Hash(database) == committed, "刷新读取未修改已提交 DB");

        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await RenderAsync(window, token);
        Check(Math.Abs(window.ClientSize.Width - 760) < 1 && Math.Abs(window.ClientSize.Height - 600) < 1,
            "报表真实窗口缩至生产最小客户区 760×600");
        CheckLayout(window, "usage-min-top");
        Capture(window, "usage-min-760x600-top");
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Check(maxY > 0, "最小尺寸下超长报表有真实可滚动视口");
        scroll.Offset = new Vector(0, maxY);
        await RenderAsync(window, token);
        var lastDefinition = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text?.StartsWith("进行中或异常中断会话只显示") == true);
        Check(VisibleFraction(lastDefinition, window) > .99, "滚动到底部后统计口径末段完整进入可见视口");
        Capture(window, "usage-min-760x600-bottom");
        Check(Hash(database) == committed, "缩放及滚动后 DB 摘要不变");
        return window; // 复用这一个已验窗口作标题栏关闭的共存对照，不额外创建实例。
    }

    private static async Task ReportAsync(Window window, int days, int characters, int utterances, int sessions,
        string duration, int detailCount, CancellationToken token)
    {
        await WaitAsync(window, () => Find<Button>(window, "UsageRefresh").IsEnabled
            && FindOptional<TextBlock>(window, "UsageCharacters") is { IsEffectivelyVisible: true } value
            && value.Text == characters.ToString("N0", CultureInfo.GetCultureInfo("zh-CN"))
            && FindOptional<UniformGrid>(window, "UsageDailyChart")?.Children.Count == days, token);
        Check(Find<TextBlock>(window, "UsageUtterances").Text == utterances.ToString(), $"{days}天定稿句数={utterances}");
        Check(Find<TextBlock>(window, "UsageSessions").Text == sessions.ToString(), $"{days}天会话数={sessions}");
        Check(Find<TextBlock>(window, "UsageRecordingTime").Text == duration, $"{days}天录音时长={duration}");
        Check(Find<ListBox>(window, "UsageSessionList").ItemCount == detailCount, $"{days}天实际明细列表条数={detailCount}");
        foreach (var range in new[] { 1, 7, 30 })
            Check((AutomationProperties.GetName(Find<Button>(window, "UsageRange" + range))?.Contains("已选中") == true) == (range == days),
                $"范围按钮选中呈现一致 days={days}, button={range}");
        foreach (var name in new[] { "UsageCharacters", "UsageUtterances", "UsageSessions", "UsageRecordingTime" })
            Check(VisibleFraction(Find<TextBlock>(window, name), window) > .99, "指标实际文字可见：" + name);
        Write($"REPORT days={days};chars={characters};utterances={utterances};sessions={sessions};duration={duration};details={detailCount};chartColumns={days}");
    }

    private static async Task VerifyLanguageAsync(Window usage, CancellationToken token)
    {
        var path = Path.Combine(Root, "isolated-config.json");
        File.WriteAllText(path, """
            { "fixtureSentinel": { "keep": "unchanged", "number": 27 }, "Endpoints": [],
              "VoiceIme": { "RecognitionLanguage": "auto", "EnableTranslation": false, "TargetLanguage": "en-US",
              "KeepOriginalHistory": false, "fixtureUnknown": "preserve-me" } }
            """);
        var original = JsonNode.Parse(File.ReadAllText(path))!;
        var window = NewLanguage(path);
        window.Show();
        await LanguageReadyAsync(window, token);
        RequireWindow(window);
        Check(window.CanResize && window.Width == 520 && window.Height == 720
            && window.MinWidth == 480 && window.MinHeight == 560, "语言窗默认 520×720、最小 480×560，允许用户缩放");
        await VerifyTitleBarAsync(window, token);
        Capture(window, "language-default-520x720");
        var vm = (VoiceImeLanguageViewModel)window.DataContext!;
        var source = Find<ComboBox>(window, "RecognitionLanguage");
        var target = Find<ComboBox>(window, "TargetLanguage");
        var toggle = Find<ToggleSwitch>(window, "EnableTranslation");
        var save = Find<Button>(window, "SaveLanguage");
        Check(vm.SelectedSource?.Code == "auto" && ((LanguageOption)source.SelectedItem!).Code == "auto"
            && vm.SelectedTarget?.Code == "en-US" && toggle.IsChecked == false && !target.IsEnabled
            && !target.IsEffectivelyEnabled && !save.IsEnabled, "初始读取 → 生产 VM → 控件绑定及禁用目标正确");
        Check(target.ItemsSource!.Cast<LanguageOption>().All(o => o.Code != "auto"), "目标语言下拉不含 auto");
        await SelectAsync(window, source, "zh-CN", token);
        Check(vm.SelectedSource?.Code == "zh-CN" && save.IsEnabled, "真实方向键选择识别语言经 TwoWay 绑定写入 VM 并启用保存");
        await ClickAsync(window, toggle, token);
        Check(toggle.IsChecked == true && vm.EnableTranslation && target.IsEnabled && target.IsEffectivelyEnabled,
            "真实 ToggleSwitch Click → VM → 目标控件启用");
        await SelectAsync(window, target, "ja-JP", token);
        Check(vm.SelectedTarget?.Code == "ja-JP", "真实方向键选择目标语言经 TwoWay 写入 VM");
        await ClickAsync(window, toggle, token);
        Check(toggle.IsChecked == false && !vm.EnableTranslation && !target.IsEffectivelyEnabled
            && vm.SelectedTarget?.Code == "ja-JP", "关闭翻译禁用目标且保留语言选择");
        await ClickAsync(window, toggle, token);
        Check(vm.EnableTranslation && target.IsEffectivelyEnabled && vm.SelectedTarget?.Code == "ja-JP", "重新开启恢复目标控件可用且绑定仍有效");
        Check(JsonNode.DeepEquals(original, JsonNode.Parse(File.ReadAllText(path))), "UI 修改尚未保存时配置字节语义未变");

        // 模拟其它配置编辑器在窗口打开后修改不相关字段，实际保存必须保留最新值。
        var external = JsonNode.Parse(File.ReadAllText(path))!;
        external["fixtureSentinel"]!["number"] = 28;
        File.WriteAllText(path, external.ToJsonString());
        await ClickAsync(window, save, token);
        await WaitAsync(window, () => Find<TextBlock>(window, "SaveStatus").Text?.StartsWith("已保存 ·") == true
            && toggle.IsEffectivelyEnabled, token);
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Check(saved["VoiceIme"]!["RecognitionLanguage"]!.GetValue<string>() == "zh-CN"
            && saved["VoiceIme"]!["EnableTranslation"]!.GetValue<bool>()
            && saved["VoiceIme"]!["TargetLanguage"]!.GetValue<string>() == "ja-JP", "真实保存 Click 已将三个语言字段写到隔离文件");
        var expected = external.DeepClone();
        expected["VoiceIme"]!["RecognitionLanguage"] = "zh-CN";
        expected["VoiceIme"]!["EnableTranslation"] = true;
        expected["VoiceIme"]!["TargetLanguage"] = "ja-JP";
        Check(JsonNode.DeepEquals(expected, saved) && JsonNode.DeepEquals(external, JsonNode.Parse(File.ReadAllText(path + ".bak"))),
            "实际保存仅 patch 三字段；保留最新不相关/未知字段且 .bak 为保存前配置");
        CheckLayout(window, "language-saved");
        Capture(window, "language-saved");
        window.Close();
        Check(!window.IsVisible, "语言窗口实际关闭");
        var reopened = NewLanguage(path);
        reopened.Show();
        await LanguageReadyAsync(reopened, token);
        var restored = (VoiceImeLanguageViewModel)reopened.DataContext!;
        Check(!ReferenceEquals(window, reopened) && !ReferenceEquals(vm, restored) && restored.SelectedSource?.Code == "zh-CN"
            && restored.SelectedTarget?.Code == "ja-JP" && restored.EnableTranslation
            && ((LanguageOption)Find<ComboBox>(reopened, "RecognitionLanguage").SelectedItem!).Code == "zh-CN"
            && ((LanguageOption)Find<ComboBox>(reopened, "TargetLanguage").SelectedItem!).Code == "ja-JP"
            && Find<ToggleSwitch>(reopened, "EnableTranslation").IsChecked == true
            && Find<ComboBox>(reopened, "TargetLanguage").IsEffectivelyEnabled,
            "全新窗口/VM 重新读取文件，源语言、目标语言及开关实际控件全部恢复");
        Check(!Find<Button>(reopened, "SaveLanguage").IsEnabled, "重开加载不产生虚假未保存状态");
        CheckLayout(reopened, "language-reopened");
        Capture(reopened, "language-reopened");
        var reload = reopened.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "重新加载"));
        await ClickAsync(reopened, reload, token);
        await LanguageReadyAsync(reopened, token);
        Check(restored.SelectedSource?.Code == "zh-CN" && restored.SelectedTarget?.Code == "ja-JP"
            && restored.EnableTranslation, "重新加载按钮真实 Click 保持保存值");
        var savedHash = Hash(path);
        reopened.Width = reopened.MinWidth;
        reopened.Height = reopened.MinHeight;
        await RenderAsync(reopened, token);
        Check(reopened.CanResize && Math.Abs(reopened.ClientSize.Width - 480) < 1
            && Math.Abs(reopened.ClientSize.Height - 560) < 1, "语言真实窗口缩至最小客户区 480×560");
        var scroll = BodyScroll(reopened);
        CheckHorizontalBody(reopened, scroll, "language-min-top");
        CheckLayout(reopened, "language-min-top", requireLanguageFields: false, viewportButtonsOnly: true);
        CheckFooter(reopened, Find<TextBlock>(reopened, "SaveStatus"), Find<Button>(reopened, "SaveLanguage"), reload);
        Capture(reopened, "language-min-480x560-top");
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Check(maxY > 0, "语言短屏正文有真实纵向滚动，不要求全部字段同时可见");
        scroll.Offset = new Vector(0, maxY);
        await RenderAsync(reopened, token);
        var note = reopened.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text?.StartsWith("复用主程序文本模型与登录") == true);
        Check(VisibleFraction(note, reopened) > .99, "语言正文滚到底后末段说明完整可见");
        CheckHorizontalBody(reopened, scroll, "language-min-bottom");
        CheckFooter(reopened, Find<TextBlock>(reopened, "SaveStatus"), Find<Button>(reopened, "SaveLanguage"), reload);
        Capture(reopened, "language-min-480x560-bottom");
        Check(Hash(path) == savedHash, "语言缩放与滚动不改写已保存配置");
        var database = Path.Combine(Root, "isolated-usage.db");
        var usageHash = Hash(database);
        await VerifyCaptionCloseAsync(reopened, usage, token);
        Check(Hash(path) == savedHash && Hash(database) == usageHash,
            "自绘关闭语言窗未改写已保存语言配置或共存报表数据库");
        // 共存窗仍停在既有底部截图位置，不用 ReportAsync 的“顶部指标可见”前提误判关闭副作用。
        Check(Find<TextBlock>(usage, "UsageCharacters").Text == "332"
            && Find<TextBlock>(usage, "UsageUtterances").Text == "4" && Find<TextBlock>(usage, "UsageSessions").Text == "2"
            && Find<TextBlock>(usage, "UsageRecordingTime").Text == "3 分 00 秒"
            && Find<UniformGrid>(usage, "UsageDailyChart").Children.Count == 7
            && Find<ListBox>(usage, "UsageSessionList").ItemCount == 3 && Find<Button>(usage, "UsageRefresh").IsEffectivelyEnabled,
            "关闭语言窗后共存报表仍保留已验的 7 天指标/明细，刷新控件仍可用");
    }

    private static async Task VerifySettingsAsync(CancellationToken token)
    {
        // 不可用引用作为对照：空选项列表不能把已保存的资源、模型或麦克风清空。
        var memory = new AzureSpeechConfig
        {
            SourceLanguage = "en-US", TargetLanguage = "de-DE", TimeoutSeconds = 17,
            ActiveSpeechResourceId = "fixture-main-resource", SelectedAudioDeviceId = "fixture-main-microphone",
            VoiceIme = new VoiceImeConfig
            {
                EnablePrewarm = true, RecognitionLanguage = "auto", EnableTranslation = true,
                TargetLanguage = "ja-JP", EndSilenceTimeoutSeconds = 0.9,
                SpeechResourceId = "fixture-offline-resource", MicrophoneDeviceId = "fixture-offline-microphone",
                ModelRef = new ModelReference { EndpointId = "fixture-offline-endpoint", ModelId = "fixture-offline-model" }
            }
        };
        var baseline = JsonSerializer.SerializeToNode(memory)!;
        var saves = new List<JsonNode>();
        Func<Task<AzureSpeechConfig>> load = () => Task.FromResult(CloneConfig(memory));
        Func<AzureSpeechConfig, Task> save = config =>
        {
            var snapshot = JsonSerializer.SerializeToNode(config)!;
            saves.Add(snapshot); // 快照与生产正在编辑的对象分离，后续修改不能篡改先前保存证据。
            memory = snapshot.Deserialize<AzureSpeechConfig>()!;
            return Task.CompletedTask;
        };
        Action<VoiceImeSectionVM> refresh = _ => { }; // 禁止默认刷新枚举设备或查询系统登记。
        var window = NewSettings(load, save, refresh);
        var vm = (VoiceImeSectionVM)window.DataContext!;
        var changes = 0;
        void Changed() => changes++;
        vm.Changed += Changed;
        try
        {
            window.Show();
            await SettingsReadyAsync(window, token);
            RequireWindow(window, viewportButtonsOnly: true);
            Check(window.CanResize && window.Width == 820 && window.Height == 820
                && window.MinWidth == 640 && window.MinHeight == 560, "设置窗默认 820×820、最小 640×560且可缩放");
            var scroll = BodyScroll(window);
            var body = (StackPanel)scroll.Content!;
            Check(body.Children.OfType<Border>().Count() == 3
                && body.GetVisualDescendants().OfType<TextBlock>().Count(t => t.FontSize == 19
                    && t.Text is "识别与连接" or "声音与断句" or "输入法安装管理") == 3,
                "设置正文保留识别与连接、声音与断句、输入法安装管理三卡");
            CheckSettingsReferences(window, memory);
            Check(Find<ToggleSwitch>(window, "EnablePrewarm").IsChecked == true
                && vm.EnablePrewarm && vm.SelectedLanguage?.Code == "auto"
                && ReferenceEquals(Find<ComboBox>(window, "RecognitionLanguage").SelectedItem, vm.SelectedLanguage)
                && vm.EndSilenceTimeoutSeconds == 0.9
                && Find<NumericUpDown>(window, "EndSilenceTimeoutSeconds").Value == 0.9m,
                "设置初次加载：预热、识别语言和句尾静音真实控件与 VM 一致");
            foreach (var (title, command) in new[]
            {
                ("安装 / 修复输入法", vm.RegisterCommand), ("卸载输入法", vm.UnregisterCommand),
                ("刷新状态", vm.RefreshStatusCommand), ("刷新资源与模型", vm.RefreshOptionsCommand),
                ("刷新设备", vm.RefreshMicrophonesCommand)
            })
                Check(ReferenceEquals(ButtonWithContent(window, title).Command, command), "只核对、不执行真实命令绑定：" + title);
            Check(vm.MicrophoneStatus == "尚未枚举麦克风。" && vm.RegistrationStatus == "尚未查询输入法安装状态。"
                && !vm.IsRegistrationBusy && !vm.IsRefreshingMicrophones && Field(window, "_configService") == null,
                "内存构造未创建真实配置服务、枚举设备或执行系统维护");
            await CheckNoSettingsSaveAsync(window, saves, 0, token);
            Check(changes == 0 && Field(window, "_saveDebounceCts") == null
                && JsonNode.DeepEquals(baseline, JsonSerializer.SerializeToNode((AzureSpeechConfig)Field(window, "_config"))),
                "初始化选中引用未触发 Changed、防抖保存或配置回写");
            CheckFooter(window, (TextBlock)Field(window, "_saveStatusText"), ButtonWithContent(window, "完成"));
            CheckHorizontalBody(window, scroll, "settings-default");
            await VerifyTitleBarAsync(window, token);
            Capture(window, "settings-default-820x820");

            var oldResource = vm.SelectedResource;
            var oldModel = vm.SelectedModel;
            var oldMicrophone = vm.SelectedMicrophone;
            vm.LoadFrom((AzureSpeechConfig)Field(window, "_config")); // 公共引用重建契约；绝不调用枚举设备的 RefreshOptionsCommand。
            await RenderAsync(window, token);
            CheckSettingsReferences(window, memory);
            await CheckNoSettingsSaveAsync(window, saves, 0, token);
            Check(!ReferenceEquals(oldResource, vm.SelectedResource) && !ReferenceEquals(oldModel, vm.SelectedModel)
                && !ReferenceEquals(oldMicrophone, vm.SelectedMicrophone) && changes == 0
                && Field(window, "_saveDebounceCts") == null
                && JsonNode.DeepEquals(baseline, JsonSerializer.SerializeToNode((AzureSpeechConfig)Field(window, "_config"))),
                "刷新重建实际选中对象但不丢引用、不触发 Changed 或保存回写");

            await ClickAsync(window, Find<ToggleSwitch>(window, "EnablePrewarm"), token);
            Check(!vm.EnablePrewarm && Find<ToggleSwitch>(window, "EnablePrewarm").IsChecked == false && changes == 1,
                "预热开关 routed Enter 真 Click 经 TwoWay 改变默认公共 VM");
            var expected = baseline.DeepClone();
            expected["VoiceIme"]!["EnablePrewarm"] = false;
            await SettingsSavedAsync(window, saves, 1, expected, token);
            Check(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(memory)), "预热自动保存仅改变内存配置的 EnablePrewarm");

            var language = Find<ComboBox>(window, "RecognitionLanguage");
            await ScrollToControlAsync(window, scroll, language, token);
            await SelectAsync(window, language, "zh-CN", token);
            Check(vm.SelectedLanguage?.Code == "zh-CN" && ReferenceEquals(language.SelectedItem, vm.SelectedLanguage),
                "设置识别语言真实方向键选择经 TwoWay 改变 VM");
            expected["VoiceIme"]!["RecognitionLanguage"] = "zh-CN";
            await SettingsSavedAsync(window, saves, 2, expected, token);
            Check(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(memory))
                && JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode((AzureSpeechConfig)Field(window, "_config"))),
                "两次自动保存仅修改预热与识别语言，其余 VoiceIme 及主程序字段全部保留");
            window.Close();
            Check(!window.IsVisible, "设置窗口保存完成后实际关闭");

            var reopened = NewSettings(load, save, refresh);
            reopened.Show();
            await SettingsReadyAsync(reopened, token);
            RequireWindow(reopened, viewportButtonsOnly: true);
            var restored = (VoiceImeSectionVM)reopened.DataContext!;
            Check(!ReferenceEquals(vm, restored) && !ReferenceEquals(Field(window, "_config"), Field(reopened, "_config"))
                && !restored.EnablePrewarm && Find<ToggleSwitch>(reopened, "EnablePrewarm").IsChecked == false
                && restored.SelectedLanguage?.Code == "zh-CN"
                && ReferenceEquals(Find<ComboBox>(reopened, "RecognitionLanguage").SelectedItem, restored.SelectedLanguage)
                && Find<NumericUpDown>(reopened, "EndSilenceTimeoutSeconds").Value == 0.9m,
                "全新设置窗口/默认 VM 从内存保存快照恢复预热、语言与未编辑静音值");
            CheckSettingsReferences(reopened, memory);
            await CheckNoSettingsSaveAsync(reopened, saves, 2, token);
            Check(Field(reopened, "_saveDebounceCts") == null && Field(reopened, "_configService") == null
                && JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode((AzureSpeechConfig)Field(reopened, "_config"))),
                "设置重开加载不产生自动保存、不读取真实配置、不改其它字段");
            reopened.Width = reopened.MinWidth;
            reopened.Height = reopened.MinHeight;
            await RenderAsync(reopened, token);
            Check(reopened.CanResize && Math.Abs(reopened.ClientSize.Width - 640) < 1
                && Math.Abs(reopened.ClientSize.Height - 560) < 1, "设置真实窗口缩至最小客户区 640×560");
            var minScroll = BodyScroll(reopened);
            CheckHorizontalBody(reopened, minScroll, "settings-min-top");
            CheckLayout(reopened, "settings-min-top", viewportButtonsOnly: true);
            CheckFooter(reopened, (TextBlock)Field(reopened, "_saveStatusText"), ButtonWithContent(reopened, "完成"));
            Check(minScroll.Extent.Height > minScroll.Viewport.Height, "设置三卡在短屏保留真实纵向滚动");
            Capture(reopened, "settings-min-640x560-top");
            var installTitle = reopened.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "输入法安装管理");
            await ScrollToControlAsync(reopened, minScroll, installTitle, token);
            foreach (var title in new[] { "安装 / 修复输入法", "卸载输入法", "刷新状态" })
                Check(VisibleFraction(ButtonWithContent(reopened, title), reopened) > .99, "最小尺寸可滚动到安装卡并完整查看命令按钮：" + title);
            CheckHorizontalBody(reopened, minScroll, "settings-min-install");
            CheckLayout(reopened, "settings-min-install", viewportButtonsOnly: true);
            CheckFooter(reopened, (TextBlock)Field(reopened, "_saveStatusText"), ButtonWithContent(reopened, "完成"));
            Capture(reopened, "settings-min-640x560-install");
            await ClickAsync(reopened, ButtonWithContent(reopened, "完成"), token);
            Check(!reopened.IsVisible && saves.Count == 2 && JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(memory)),
                "完成按钮真实 Click 关闭设置，缩放/滚动/关闭未触发额外保存或系统维护");
        }
        finally { vm.Changed -= Changed; }
    }

    private static Window NewSettings(Func<Task<AzureSpeechConfig>> load, Func<AzureSpeechConfig, Task> save,
        Action<VoiceImeSectionVM> refresh)
    {
        var constructor = typeof(VoiceImeSettingsWindow).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(Func<Task<AzureSpeechConfig>>), typeof(Func<AzureSpeechConfig, Task>),
                typeof(Action<VoiceImeSectionVM>), typeof(VoiceImeSectionVM) }, null)
            ?? throw new MissingMethodException("设置内存隔离构造");
        var window = (Window)constructor.Invoke(new object?[] { load, save, refresh, null });
        Owned.Add(window);
        Check(ReferenceEquals(Field(window, "_load"), load) && ReferenceEquals(Field(window, "_save"), save)
            && ReferenceEquals(Field(window, "_refresh"), refresh)
            && window.DataContext is VoiceImeSectionVM && ReferenceEquals(window.DataContext, Field(window, "_vm")),
            "生产设置反射构造注入内存 load/save、no-op refresh，由窗口生成默认公共 VM");
        return window;
    }

    private static AzureSpeechConfig CloneConfig(AzureSpeechConfig config)
        => JsonSerializer.Deserialize<AzureSpeechConfig>(JsonSerializer.Serialize(config))!;

    private static Task SettingsReadyAsync(Window window, CancellationToken token) => WaitAsync(window,
        () => Field(window, "_config") is AzureSpeechConfig
            && ((TextBlock)Field(window, "_saveStatusText")).Text?.StartsWith("已加载当前配置") == true
            && new[] { "EnablePrewarm", "SpeechResource", "SpeechModel", "Microphone", "RecognitionLanguage", "EndSilenceTimeoutSeconds" }
                .All(name => FindOptional<Control>(window, name)?.IsEffectivelyEnabled == true), token);

    private static async Task CheckNoSettingsSaveAsync(Window window, List<JsonNode> saves, int count, CancellationToken token)
    {
        // 覆盖生产 200ms 防抖窗口，防止把“尚未保存”误判成“不会保存”。
        await Task.Delay(250, token);
        await RenderAsync(window, token);
        Check(saves.Count == count, "加载/引用刷新没有额外自动保存；计数=" + count);
    }

    private static async Task SettingsSavedAsync(Window window, List<JsonNode> saves, int count,
        JsonNode expected, CancellationToken token)
    {
        await WaitAsync(window, () => saves.Count >= count
            && ((TextBlock)Field(window, "_saveStatusText")).Text?.StartsWith("已自动保存（") == true, token);
        Check(saves.Count == count && JsonNode.DeepEquals(expected, saves[count - 1]),
            "生产自动保存委托次数/独立完整配置快照正确；计数=" + count);
    }

    private static void CheckSettingsReferences(Window window, AzureSpeechConfig expected)
    {
        var vm = (VoiceImeSectionVM)window.DataContext!;
        foreach (var (name, items, selected) in new (string, object, object?)[]
        {
            ("SpeechResource", vm.SpeechResources, vm.SelectedResource),
            ("SpeechModel", vm.SpeechModels, vm.SelectedModel),
            ("Microphone", vm.Microphones, vm.SelectedMicrophone)
        })
        {
            var box = Find<ComboBox>(window, name);
            Write($"REFERENCE {name}: itemsSame={ReferenceEquals(box.ItemsSource, items)}; selectedSame={ReferenceEquals(box.SelectedItem, selected)}; selectedEqual={Equals(box.SelectedItem, selected)}; control={box.SelectedItem}; vm={selected}");
        }
        Check(ReferenceEquals(Find<ComboBox>(window, "SpeechResource").ItemsSource, vm.SpeechResources)
            && ReferenceEquals(Find<ComboBox>(window, "SpeechResource").SelectedItem, vm.SelectedResource)
            && vm.SelectedResource?.Id == expected.VoiceIme.SpeechResourceId
            && ReferenceEquals(Find<ComboBox>(window, "SpeechModel").ItemsSource, vm.SpeechModels)
            && ReferenceEquals(Find<ComboBox>(window, "SpeechModel").SelectedItem, vm.SelectedModel)
            && vm.SelectedModel?.Reference.EndpointId == expected.VoiceIme.ModelRef?.EndpointId
            && vm.SelectedModel?.Reference.ModelId == expected.VoiceIme.ModelRef?.ModelId
            && ReferenceEquals(Find<ComboBox>(window, "Microphone").ItemsSource, vm.Microphones)
            && ReferenceEquals(Find<ComboBox>(window, "Microphone").SelectedItem, vm.SelectedMicrophone)
            && vm.SelectedMicrophone?.DeviceId == expected.VoiceIme.MicrophoneDeviceId,
            "资源/模型/麦克风真实 ItemsSource 与 SelectedItem 引用一致，保留离线已保存引用");
    }

    private static Button ButtonWithContent(Window window, string title)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, title));

    private static ScrollViewer BodyScroll(Window window)
        => ((Grid)window.Content!).Children.OfType<ScrollViewer>().Single();

    private static void CheckFooter(Window window, params Control[] controls)
    {
        foreach (var control in controls)
            Check(!control.GetVisualAncestors().Contains(BodyScroll(window)) && VisibleFraction(control, window) > .99,
                "固定页脚完整可见、不会随正文滚走：" + (control.Name ?? (control as Button)?.Content?.ToString() ?? "保存状态"));
    }

    private static void CheckHorizontalBody(Window window, ScrollViewer scroll, string stage)
    {
        Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.Viewport.Width > 0
            && scroll.Extent.Width <= scroll.Viewport.Width + 1 && Math.Abs(scroll.Offset.X) < 1
            && FitsHorizontally((Control)scroll.Content!, window), "正文无横向溢出/横向滚动：" + stage);
        foreach (var control in ((Control)scroll.Content!).GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0
                && c is ComboBox or NumericUpDown or Button))
            Check(FitsHorizontally(control, window), "正文编辑控件横向边界在视口内：" + (control.Name ?? control.GetType().Name));
    }

    private static async Task ScrollToControlAsync(Window window, ScrollViewer scroll, Control control, CancellationToken token)
    {
        var origin = control.TranslatePoint(default, scroll) ?? throw new InvalidOperationException("滚动目标不在正文树中");
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(0, Math.Clamp(scroll.Offset.Y + origin.Y
            - (scroll.Viewport.Height - control.Bounds.Height) / 2, 0, maxY));
        await RenderAsync(window, token);
        Check(VisibleFraction(control, window) > .99, "滚动后真实目标完整进入视口：" + (control.Name ?? (control as TextBlock)?.Text));
    }

    private static Window NewLanguage(string path)
    {
        var constructor = typeof(VoiceImeLanguageWindow).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string) }, null) ?? throw new MissingMethodException("语言隔离构造");
        var window = (Window)constructor.Invoke(new object[] { path });
        Owned.Add(window);
        Check((string)Field(window, "_path") == path, "生产语言窗口严格指向项目内隔离配置");
        return window;
    }

    private static Task LanguageReadyAsync(Window window, CancellationToken token) => WaitAsync(window,
        () => Find<TextBlock>(window, "SaveStatus").Text?.StartsWith("已加载已保存设置") == true
            && Find<ComboBox>(window, "RecognitionLanguage").IsEffectivelyEnabled, token);

    private static async Task ClickAsync(Window window, Button button, CancellationToken token)
    {
        Check(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && VisibleFraction(button, window) > .99,
            "可交互实际按钮：" + (button.Name ?? button.Content?.ToString()));
        var clicks = 0;
        void Observe(object? sender, RoutedEventArgs args) { if (ReferenceEquals(args.Source, button)) clicks++; }
        window.AddHandler(Button.ClickEvent, Observe, RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = button };
            button.RaiseEvent(input); // Actual Button.OnKeyDown -> virtual OnClick, including ToggleButton.Toggle.
            // 自绘最小化/关闭及“完成”会令窗口不能渲染；只在仍可渲染时等待真实合成帧。
            if (window.IsVisible && window.WindowState != WindowState.Minimized) await RenderAsync(window, token);
            else token.ThrowIfCancellationRequested();
            Check(input.Handled && clicks == 1, "路由 Enter 产生且冒泡一次真实 Click：" + (button.Name ?? button.Content));
        }
        finally { window.RemoveHandler(Button.ClickEvent, Observe); }
    }

    private static async Task SelectAsync(Window window, ComboBox box, string code, CancellationToken token)
    {
        var items = box.ItemsSource!.Cast<LanguageOption>().ToArray();
        var index = Array.FindIndex(items, x => x.Code == code);
        Check(index >= 0 && box.IsEffectivelyEnabled && VisibleFraction(box, window) > .99, "选择目标存在且实际控件在视口内可用：" + code);
        var selectionEvents = 0;
        void Observe(object? sender, SelectionChangedEventArgs args) => selectionEvents++;
        box.SelectionChanged += Observe;
        try
        {
            var steps = Math.Abs(index - box.SelectedIndex);
            var key = index > box.SelectedIndex ? Key.Down : Key.Up;
            for (var step = 0; step < steps; step++)
            {
                var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = box };
                box.RaiseEvent(input);
                Check(input.Handled, "ComboBox 实际处理方向键");
            }
            await RenderAsync(window, token);
            Check(((LanguageOption)box.SelectedItem!).Code == code && selectionEvents == steps,
                $"ComboBox 路由选择 {code}；SelectionChanged={selectionEvents}，预期={steps}");
        }
        finally { box.SelectionChanged -= Observe; }
    }

    private static async Task RenderAsync(Window window, CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window) ?? throw new InvalidOperationException("无真实合成器");
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered
            .WaitAsync(TimeSpan.FromSeconds(8), token), DispatcherPriority.Background);
    }

    private static async Task WaitAsync(Window window, Func<bool> ready, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("窗口加载/绑定未满足断言：" + window.Title);
            await RenderAsync(window, token);
        }
        await RenderAsync(window, token);
    }

    private static void RequireWindow(Window window, bool viewportButtonsOnly = false)
    {
        Check(window.IsVisible && window.TryGetPlatformHandle()?.Handle != IntPtr.Zero
            && window.TryGetPlatformHandle() != null && window.ClientSize.Width > 0 && window.ClientSize.Height > 0,
            "真实 Show 的生产 Win32/Avalonia 窗口：" + window.Title);
        Write($"WINDOW title={window.Title};hwnd={window.TryGetPlatformHandle()!.Handle};client={window.ClientSize};RenderScaling={window.RenderScaling}");
        CheckNativeChrome(window);
        CheckLayout(window, "initial-" + window.GetType().Name, viewportButtonsOnly: viewportButtonsOnly);
    }

    private static async Task VerifyTitleBarAsync(Window window, CancellationToken token)
    {
        var hwnd = OwnedHandle(window);
        var size = window.ClientSize;
        var width = window.Width;
        var height = window.Height;
        var context = window.DataContext;
        var content = window.Content;
        await CheckNativeFramePixelsAsync(window, token);
        Check(window.WindowState == WindowState.Normal && window.CanResize && window.CanMaximize && window.CanMinimize,
            "三窗正常态支持缩放、最大化与最小化：" + window.GetType().Name);
        await ClickAsync(window, Find<Button>(window, "VoiceImeMaximize"), token);
        await NativeStateAsync(window, hwnd, WindowState.Maximized, token);
        CheckNativeChrome(window);
        CheckCaptionLayout(window, "caption-maximized");
        await ClickAsync(window, Find<Button>(window, "VoiceImeMaximize"), token);
        await NativeStateAsync(window, hwnd, WindowState.Normal, token);
        await CheckRestoredAsync();
        await ClickAsync(window, Find<Button>(window, "VoiceImeMinimize"), token);
        await NativeStateAsync(window, hwnd, WindowState.Minimized, token);
        CheckCaptionState(window); // 最小化不可点标题栏，不请求 Rendered，也不把属性设值冒充恢复点击。
        Check(OwnedHandle(window) == hwnd, "最小化仍是工具自己的同一个 HWND");
        _ = ShowWindow(hwnd, 9); // 复用 WindowChromeRegression 的 SW_RESTORE；BOOL 是此前可见性，不是成功码。
        await NativeStateAsync(window, hwnd, WindowState.Normal, token);
        await CheckRestoredAsync();

        async Task CheckRestoredAsync()
        {
            var tolerance = 1 / window.RenderScaling + 0.01;
            await WaitAsync(window, () => Math.Abs(window.ClientSize.Width - size.Width) <= tolerance
                && Math.Abs(window.ClientSize.Height - size.Height) <= tolerance, token);
            Check(OwnedHandle(window) == hwnd && window.Width == width && window.Height == height
                && window.IsVisible && BodyScroll(window).IsEffectivelyEnabled && BodyScroll(window).Viewport.Height > 0
                && ReferenceEquals(window.DataContext, context) && ReferenceEquals(window.Content, content),
                "还原至原客户区/声明大小，VM/内容不更换且正文仍可用：" + window.GetType().Name);
            CheckNativeChrome(window);
            CheckCaptionLayout(window, "caption-restored");
            await CheckNativeFramePixelsAsync(window, token);
        }
    }

    private static async Task NativeStateAsync(Window window, IntPtr hwnd, WindowState state, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (window.WindowState != state || IsIconic(hwnd) != (state == WindowState.Minimized)
            || IsZoomed(hwnd) != (state == WindowState.Maximized))
        {
            token.ThrowIfCancellationRequested();
            if (timeout.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Avalonia/Win32 状态未同时进入 " + state);
            await Task.Delay(20, token); // 最小化只泵 UI 消息，不等待不可用的合成帧。
        }
        Check(OwnedHandle(window) == hwnd, "真实按钮改变 Avalonia/IsIconic/IsZoomed，同 HWND 状态=" + state);
        if (state != WindowState.Minimized) await RenderAsync(window, token);
    }

    private static async Task VerifyCaptionCloseAsync(Window window, Window survivor, CancellationToken token)
    {
        var hwnd = OwnedHandle(window);
        var otherHwnd = OwnedHandle(survivor);
        var context = survivor.DataContext;
        var content = survivor.Content;
        var state = survivor.WindowState;
        var size = survivor.ClientSize;
        var offset = BodyScroll(survivor).Offset;
        var closed = 0;
        var otherClosed = 0;
        void Closed(object? sender, EventArgs args) => closed++;
        void OtherClosed(object? sender, EventArgs args) => otherClosed++;
        window.Closed += Closed;
        survivor.Closed += OtherClosed;
        try
        {
            await ClickAsync(window, Find<Button>(window, "VoiceImeClose"), token);
            Check(closed == 1 && !window.IsVisible && !IsWindow(hwnd), "重开语言窗自绘 Close 真实路由 Click：Closed=1、HWND 销毁");
            await RenderAsync(survivor, token);
            Check(otherClosed == 0 && survivor.IsVisible && OwnedHandle(survivor) == otherHwnd
                && ReferenceEquals(survivor.DataContext, context) && ReferenceEquals(survivor.Content, content)
                && survivor.WindowState == state && survivor.ClientSize == size && BodyScroll(survivor).Offset == offset,
                "自绘 Close 不关闭共存用量窗，不改变其 VM/内容、状态、大小或滚动位置");
            CheckCaptionLayout(survivor, "caption-close-survivor");
        }
        finally { window.Closed -= Closed; survivor.Closed -= OtherClosed; }
    }

    private static IntPtr OwnedHandle(Window window)
    {
        Check(OperatingSystem.IsWindows() && Owned.Contains(window), "原生调用只针对工具持有的 Windows 窗口");
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Check(hwnd != IntPtr.Zero && IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out var pid) != 0
            && pid == (uint)Environment.ProcessId, "原生 HWND 存活且属于当前工具进程");
        return hwnd;
    }

    private static void CheckNativeChrome(Window window)
    {
        var hwnd = OwnedHandle(window);
        var style = GetWindowLong(hwnd, -16);
        // 与 WindowChromeRegression 相同：WS_CAPTION 包含 WS_BORDER，BorderOnly 不能要求全部 caption 位清零。
        Check(window.SystemDecorations == SystemDecorations.BorderOnly && (style & 0x00400000) == 0
            && (window.WindowState != WindowState.Normal || (style & 0x00040000) != 0),
            "BorderOnly：WS_DLGFRAME=0，正常态保留 WS_THICKFRAME 系统缩放边框");
        Write($"CHROME window={window.GetType().Name};state={window.WindowState};style=0x{style:X8}");
        // Avalonia 11.3.11 WindowImpl.UpdateWindowProperties 在未扩展客户区的最大化态移除 THICKFRAME。
        Check(((ISolidColorBrush)Find<Border>(window, "VoiceImePageHeader").Background!).Color == Color.Parse("#132B49"),
            "自绘 Header 保留生产深蓝底色");
    }

    private static async Task CheckNativeFramePixelsAsync(Window window, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            const string gap = "本机非 Win11 Build 22000+，不验 DWM 顶沿像素。";
            if (!Gaps.Contains(gap)) Gaps.Add(gap);
            return;
        }
        var hwnd = OwnedHandle(window);
        var color = ((ISolidColorBrush)Find<Border>(window, "VoiceImePageHeader").Background!).Color;
        var expected = (uint)(color.R | color.G << 8 | color.B << 16);
        var wasTopmost = window.Topmost;
        window.Topmost = true;
        try
        {
            await RenderAsync(window, token);
            Check(DwmFlush() == 0, "顶沿像素采样前 DWM flush 成功");
            // 颜色属性 34/35 在本机 Get 返回 E_INVALIDARG；用可读取的实际边界和屏幕像素验效果。
            Check(DwmGetWindowAttribute(hwnd, 9, out var frame, (uint)Marshal.SizeOf<FrameRect>()) == 0,
                "读取真实 DWM 可见窗口边界");
            var client = window.PointToScreen(default);
            var top = window.WindowState == WindowState.Maximized ? client.Y : frame.Top;
            var width = (int)Math.Round(window.ClientSize.Width * window.RenderScaling);
            var dc = GetDC(IntPtr.Zero);
            Check(dc != IntPtr.Zero, "取得屏幕像素只读 DC");
            try
            {
                var samples = 0;
                foreach (var fraction in new[] { 0.35, 0.5, 0.65 })
                {
                    var x = client.X + (int)(width * fraction);
                    Check(WindowFromPoint(new ScreenPoint { X = x, Y = client.Y + 3 }) == hwnd,
                        "顶沿采样位置确实属于当前工具窗口，未被遮挡");
                    for (var y = top; y < client.Y + 3; y++)
                    {
                        var actual = GetPixel(dc, x, y);
                        Check(actual == expected, $"窗口顶沿与深蓝页头连续：x={x};y={y};actual=0x{actual:X8};expected=0x{expected:X8}");
                        samples++;
                    }
                }
                Write($"CHROME_PIXELS window={window.GetType().Name};state={window.WindowState};frameTop={frame.Top};clientTop={client.Y};samples={samples};COLORREF=0x{expected:X8}");
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        finally { window.Topmost = wasTopmost; }
    }

    private static void CheckCaptionState(Window window)
    {
        var maximize = Find<Button>(window, "VoiceImeMaximize");
        var maximized = window.WindowState == WindowState.Maximized;
        var label = maximized ? "还原" : "最大化";
        Check(maximize.Content is TextBlock glyph && glyph.Text == (maximized ? "\uE923" : "\uE922")
            && Equals(ToolTip.GetTip(maximize), label) && AutomationProperties.GetName(maximize) == label
            && maximize.IsEnabled == window.CanMaximize, "最大化/还原 glyph、tooltip、自动化名称与 CanMaximize 同步");
        var minimize = Find<Button>(window, "VoiceImeMinimize");
        var close = Find<Button>(window, "VoiceImeClose");
        Check(minimize.Content is TextBlock minGlyph && minGlyph.Text == "\uE921"
            && minimize.IsEnabled == window.CanMinimize && AutomationProperties.GetName(minimize) == "最小化"
            && Equals(ToolTip.GetTip(minimize), "最小化") && close.Content is TextBlock closeGlyph
            && closeGlyph.Text == "\uE8BB" && close.IsEffectivelyEnabled
            && AutomationProperties.GetName(close) == "关闭" && Equals(ToolTip.GetTip(close), "关闭"),
            "最小化/关闭 glyph 与无障碍标签正确，最小化启用跟随 CanMinimize");
    }

    private static bool IsCaptionButton(Button button)
        => button.Name is "VoiceImeMinimize" or "VoiceImeMaximize" or "VoiceImeClose";

    private static Rect BoundsIn(Visual visual, Visual ancestor) => new(visual.TranslatePoint(default, ancestor)
        ?? throw new InvalidOperationException("标题栏控件不在同一视觉树"), visual.Bounds.Size);

    private static bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 0.1 && inner.Top >= outer.Top - 0.1
        && inner.Right <= outer.Right + 0.1 && inner.Bottom <= outer.Bottom + 0.1;

    private static void CheckCaptionLayout(Window window, string stage)
    {
        var root = (Grid)window.Content!;
        var header = Find<Border>(window, "VoiceImePageHeader");
        var bar = Find<Grid>(window, "VoiceImeTitleBar");
        var drag = Find<Border>(window, "VoiceImeTitleDragArea");
        var bounds = BoundsIn(bar, window);
        var tolerance = 1 / window.RenderScaling + 0.01; // 沿用既有标题栏专项的一物理像素布局舍入预算。
        Check(root.RowDefinitions.Count == 3 && root.Children.Count == 3 && root.Children.Contains(header)
            && Grid.GetRow(header) == 0 && Grid.GetRow(BodyScroll(window)) == 1
            && root.Children.Count(c => Grid.GetRow(c) == 2) == 1
            && BoundsIn(BodyScroll(window), window).Top >= BoundsIn(header, window).Bottom - 0.1,
            "Header/正文/页脚仍为 Grid 三行且正文不覆盖 Header：" + stage);
        Check(Math.Abs(bounds.Left) < 0.1 && Math.Abs(bounds.Top) < 0.1
            && Math.Abs(bounds.Width - window.ClientSize.Width) <= tolerance
            && Math.Abs(bounds.Height - 38) <= tolerance && VisibleFraction(bar, window) > .99,
            "自绘标题条位于客户区 (0,0)、全宽且高 38：" + stage);
        var dragBounds = BoundsIn(drag, bar);
        Check(drag.Height == 38 && dragBounds.Width > 100 && Math.Abs(dragBounds.Left) < 0.1
            && Math.Abs(dragBounds.Top) < 0.1 && Math.Abs(dragBounds.Height - 38) <= tolerance,
            "标题拖动区有独立有效布局（未物理验拖动）：" + stage);
        var previousRight = dragBounds.Right;
        foreach (var name in new[] { "VoiceImeMinimize", "VoiceImeMaximize", "VoiceImeClose" })
        {
            var button = Find<Button>(window, name);
            var buttonBounds = BoundsIn(button, bar);
            Check(button.Width == 46 && button.Height == 38 && Math.Abs(buttonBounds.Width - 46) <= tolerance
                && Math.Abs(buttonBounds.Height - 38) <= tolerance && Math.Abs(buttonBounds.Top) < 0.1
                && Math.Abs(buttonBounds.Left - previousRight) < 0.1
                && Contains(new Rect(bar.Bounds.Size), buttonBounds) && VisibleFraction(button, window) > .99,
                "caption 46×38、相邻不重叠且完整可见：" + name + "/" + stage);
            previousRight = buttonBounds.Right;
            var glyph = button.Content as TextBlock ?? throw new InvalidOperationException(name + " 缺少实际 Segoe 字形控件");
            Check(button.GetVisualDescendants().Contains(glyph) && glyph.FontSize == 16
                && VisibleFraction(glyph, window) > .99 && Contains(new Rect(button.Bounds.Size), BoundsIn(glyph, button))
                && glyph.TextLayout.Width > 0 && glyph.TextLayout.Height > 0
                && glyph.TextLayout.Width <= glyph.Bounds.Width + 0.1 && glyph.TextLayout.Height <= glyph.Bounds.Height + 0.1,
                "caption font16 的真实字形布局完整落在按钮内：" + name);
            var runs = glyph.TextLayout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>().ToArray();
            Check(runs.Length == 1 && runs[0].GlyphRun.GlyphInfos.Count == 1
                && runs[0].GlyphRun.GlyphInfos[0].GlyphIndex != 0 && runs[0].GlyphRun.FontRenderingEmSize == 16
                && (runs[0].GlyphRun.GlyphTypeface.FamilyName is "Segoe Fluent Icons" or "Segoe MDL2 Assets")
                && runs[0].GlyphRun.InkBounds.Width > 0 && runs[0].GlyphRun.InkBounds.Height > 0,
                "caption 实际 Segoe GlyphRun 非缺失字形且有绘制范围：" + name);
            Measurements.Add(new { stage, window = window.GetType().Name, state = window.WindowState.ToString(),
                caption = name, bounds = buttonBounds, glyph = glyph.Text,
                font = runs[0].GlyphRun.GlyphTypeface.FamilyName, glyphIndex = runs[0].GlyphRun.GlyphInfos[0].GlyphIndex,
                inkBounds = runs[0].GlyphRun.InkBounds, tooltip = ToolTip.GetTip(button), automation = AutomationProperties.GetName(button) });
        }
        Check(Math.Abs(previousRight - bar.Bounds.Width) < 0.1, "三个 caption 按钮右贴标题条边缘：" + stage);
        CheckCaptionState(window);
    }

    private static void CheckLayout(Window window, string stage, bool requireLanguageFields = true, bool viewportButtonsOnly = false)
    {
        CheckCaptionLayout(window, stage); // caption Content 已是 TextBlock，不能沿用 string-only 按钮过滤漏验。
        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && VisibleFraction(t, window) > 0).ToArray();
        foreach (var text in texts)
        {
            var origin = text.TranslatePoint(default, window)!.Value;
            var row = new { stage, text = text.Text, x = origin.X, y = origin.Y, width = text.Bounds.Width,
                height = text.Bounds.Height, textWidth = text.TextLayout.Width, textHeight = text.TextLayout.Height,
                visibleFraction = VisibleFraction(text, window), trimming = text.TextTrimming.ToString() };
            Measurements.Add(row);
            // TextLayout measures actual formatted glyph lines, not merely nonempty Text properties.
            var overflow = text.TextLayout.Width > text.Bounds.Width + 1 || text.TextLayout.Height > text.Bounds.Height + 1;
            var outsideX = origin.X < -1 || origin.X + text.Bounds.Width > window.ClientSize.Width + 1;
            if (overflow || outsideX)
            {
                var warning = $"{stage}: text={text.Text};bounds={text.Bounds};glyphs={text.TextLayout.Width:F1}x{text.TextLayout.Height:F1};outsideX={outsideX}";
                LayoutWarnings.Add(warning);
                Write("LAYOUT_WARNING=" + warning);
            }
        }
        Check(texts.Length > 0, "实际文字布局可测：" + stage);
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible
            && (IsCaptionButton(b) || !viewportButtonsOnly || VisibleFraction(b, window) > 0)))
        {
            if (!IsCaptionButton(button) && button.GetVisualAncestors().OfType<ScrollViewer>().Any())
            {
                if (VisibleFraction(button, window) <= 0) continue; // 正文视口外无需同时可见，不放宽标题栏/页脚。
                Check(FitsHorizontally(button, window), "视口内正文按钮无横向裁剪（允许纵向滚动裁剪）：" + (button.Name ?? button.Content?.ToString()));
            }
            else
                Check(VisibleFraction(button, window) > .99, "标题栏/固定按钮完整可见：" + (button.Name ?? button.Content?.ToString()));
        }
        if (requireLanguageFields && window is VoiceImeLanguageWindow)
            foreach (var name in new[] { "RecognitionLanguage", "TargetLanguage", "EnableTranslation", "SaveStatus", "SaveLanguage" })
                Check(VisibleFraction(Find<Control>(window, name), window) > .99, "语言控件/状态完整可见：" + name);
        Write($"LAYOUT stage={stage};client={window.ClientSize};scale={window.RenderScaling};visibleTexts={texts.Length};warnings={LayoutWarnings.Count}");
    }

    private static bool FitsHorizontally(Control control, Window window)
    {
        var origin = control.TranslatePoint(default, window);
        if (origin == null || control.Bounds.Width <= 0) return false;
        var left = 0d;
        var right = window.ClientSize.Width;
        foreach (var ancestor in control.GetVisualAncestors().TakeWhile(a => a != window).Where(a => a.ClipToBounds))
        {
            var point = ancestor.TranslatePoint(default, window);
            if (point == null) return false;
            left = Math.Max(left, point.Value.X);
            right = Math.Min(right, point.Value.X + ancestor.Bounds.Width);
        }
        return origin.Value.X >= left - 1 && origin.Value.X + control.Bounds.Width <= right + 1;
    }

    private static double VisibleFraction(Control control, Window window)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return 0;
        var origin = control.TranslatePoint(default, window);
        if (origin == null) return 0;
        var rectangle = new Rect(origin.Value, control.Bounds.Size);
        var clipped = rectangle.Intersect(new Rect(window.ClientSize));
        foreach (var ancestor in control.GetVisualAncestors().TakeWhile(a => a != window).Where(a => a.ClipToBounds))
        {
            var point = ancestor.TranslatePoint(default, window);
            if (point != null) clipped = clipped.Intersect(new Rect(point.Value, ancestor.Bounds.Size));
        }
        return Math.Max(0, clipped.Width) * Math.Max(0, clipped.Height) / (rectangle.Width * rectangle.Height);
    }

    private static void Capture(Window window, string name)
    {
        var scale = window.RenderScaling;
        var path = Path.Combine(Root, name + ".png");
        using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.ClientSize.Width * scale),
            (int)Math.Ceiling(window.ClientSize.Height * scale)), new Vector(96 * scale, 96 * scale));
        image.Render(window);
        image.Save(path);
        Screenshots.Add(path);
        Write($"SCREENSHOT={path};pixels={image.PixelSize};source=RenderTargetBitmap(actual shown production window)");
    }

    private static T? FindOptional<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().SingleOrDefault(c => c.Name == name);
    private static T Find<T>(Window root, string name) where T : Control => FindOptional<T>(root, name) ?? throw new InvalidOperationException("找不到实际控件 " + name);
    private static object Field(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        // 包装器自身字段（如 _closedCts）优先；仅缺少字段的窗口才解包，不按字段值是否为 null 回退。
        if (target is Window && field == null)
        {
            var page = target.GetType().GetField("_page", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
            return Field(page, name); // 实际页面不是 Window，只解包一次。
        }
        return field!.GetValue(target)!;
    }
    private static void Check(bool success, string description)
    {
        if (!success) throw new InvalidOperationException(description);
        Passes.Add(description);
        Write("PASS=" + description);
    }
    private static void Write(string line) { Console.WriteLine(line); LogFile?.WriteLine(line); }
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // 仅工具自有 HWND：只读 style/DWM/状态；唯一原生动作是最小化后的 SW_RESTORE，不抢前台或注入输入。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out FrameRect value, uint size);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }
    private static void SeedDatabase(string path)
    {
        // Only the two report tables, in this run's private file; never instantiate the production writer/default path.
        using var connection = Open(path);
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE voice_ime_sessions(session_id TEXT PRIMARY KEY, owner_scope TEXT NOT NULL,
                created_utc TEXT NOT NULL, created_local_date TEXT NOT NULL, created_offset_minutes INTEGER NOT NULL,
                recording_started_utc TEXT, checkpoint_utc TEXT NOT NULL, checkpoint_local_date TEXT NOT NULL,
                checkpoint_offset_minutes INTEGER NOT NULL, ended_utc TEXT, recording_ms INTEGER NOT NULL,
                state TEXT NOT NULL, stop_reason TEXT, revision INTEGER NOT NULL);
            CREATE TABLE voice_ime_utterances(session_id TEXT NOT NULL, sequence_no INTEGER NOT NULL,
                finalized_utc TEXT NOT NULL, local_date TEXT NOT NULL, utc_offset_minutes INTEGER NOT NULL,
                character_count INTEGER NOT NULL, original_text TEXT, PRIMARY KEY(session_id,sequence_no));
            """;
        schema.ExecuteNonQuery();
        Session(connection, "today", "2026-11-06", 60000);
        Session(connection, "week", "2026-10-31", 120000);
        Session(connection, "month", "2026-10-08", 180000);
        Session(connection, "outside", "2026-10-07", 240000);
        Utterance(connection, "today", 1, "2026-11-06", 120);
        Utterance(connection, "week", 1, "2026-10-31", 200);
        Utterance(connection, "month", 1, "2026-10-08", 300);
        Utterance(connection, "outside", 1, "2026-10-07", 999);
        Utterance(connection, "outside", 2, "2026-11-06", 5);
    }
    private static void Session(SqliteConnection connection, string id, string date, int milliseconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO voice_ime_sessions VALUES(@id,'ui-fixture',@utc,@date,480,@utc,@utc,@date,480,@utc,@ms,'ended','manual_stop',1);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@date", date);
        command.Parameters.AddWithValue("@utc", date + "T04:00:00+00:00");
        command.Parameters.AddWithValue("@ms", milliseconds);
        command.ExecuteNonQuery();
    }
    private static void Utterance(SqliteConnection connection, string id, int sequence, string date, int characters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO voice_ime_utterances VALUES(@id,@sequence,@utc,@date,480,@characters,'SYNTHETIC-NOT-USER-TEXT');";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@sequence", sequence);
        command.Parameters.AddWithValue("@utc", date + "T04:00:00+00:00");
        command.Parameters.AddWithValue("@date", date);
        command.Parameters.AddWithValue("@characters", characters);
        command.ExecuteNonQuery();
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 6, 4, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Fixture+8", TimeSpan.FromHours(8), "Fixture+8", "Fixture+8");
    }
}

public sealed class FixtureApplication : Application
{
    public override void Initialize()
    {
        // 与生产独立输入法 UI 宿主一致：不实例化 App，不加载 App.axaml 或主主题。
        var theme = typeof(VoiceImeSettingsWindow).Assembly.GetType("TrueFluentPro.Services.VoiceImeUiTheme", throwOnError: true)!;
        var apply = theme.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(Application) }, null) ?? throw new MissingMethodException("独立输入法 UI 主题入口");
        apply.Invoke(null, new object[] { this });
    }
}