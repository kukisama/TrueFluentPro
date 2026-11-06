using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
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
    private static readonly string[] Gaps =
    {
        "真实 Win32/Avalonia 窗口 + 框架 routed KeyDown/Click/选择；未使用物理 SendInput，未验鼠标命中或系统键盘派发。",
        "PNG 为 Show 后等待 CompositionBatch.Rendered 的实际窗口 RenderTargetBitmap；非物理桌面/DWM 截图。",
        "只测本机实际 RenderScaling 与窗口缩小，不代表多显示器或 200%/400% DPI。",
        "未读写真实配置或真实数据库，未启动主窗/主 DI、音频、云端、Host、TSF、注册/安装或 UAC；未关闭用户程序。",
        "只验证绑定、持久化、报表与基础布局；不覆盖全部错误态、生产录音/翻译或跨主题行为。"
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
                    await VerifyUsageAsync(deadline.Token);
                    await VerifyLanguageAsync(deadline.Token);
                    Check(TrueFluentPro.App.Services == null, "验收结束仍未启动生产主 DI");
                    code = 0;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    Write("FAIL=" + ex);
                    foreach (var window in Owned.Where(w => w.IsVisible).ToArray())
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

    private static async Task VerifyUsageAsync(CancellationToken token)
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
        window.Close();
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

    private static async Task VerifyLanguageAsync(CancellationToken token)
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
        var size = window.ClientSize;
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
        Check(reopened.CanResize == false && size.Width >= reopened.MinWidth && size.Height >= reopened.MinHeight,
            "语言窗生产 CanResize=false，按真实 SizeToContent/MinHeight 验布局，不伪造用户缩放");
        CheckLayout(reopened, "language-reopened");
        Capture(reopened, "language-reopened");
        var reload = reopened.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "重新加载"));
        await ClickAsync(reopened, reload, token);
        await LanguageReadyAsync(reopened, token);
        Check(restored.SelectedSource?.Code == "zh-CN" && restored.SelectedTarget?.Code == "ja-JP"
            && restored.EnableTranslation, "重新加载按钮真实 Click 保持保存值");
        reopened.Close();
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
            await RenderAsync(window, token);
            Check(input.Handled && clicks == 1, "路由 Enter 产生且冒泡一次真实 Click：" + (button.Name ?? button.Content));
        }
        finally { window.RemoveHandler(Button.ClickEvent, Observe); }
    }

    private static async Task SelectAsync(Window window, ComboBox box, string code, CancellationToken token)
    {
        var items = box.ItemsSource!.Cast<LanguageOption>().ToArray();
        var index = Array.FindIndex(items, x => x.Code == code);
        Check(index >= 0 && box.IsEffectivelyEnabled, "选择目标存在且控件可用：" + code);
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

    private static void RequireWindow(Window window)
    {
        Check(window.IsVisible && window.TryGetPlatformHandle()?.Handle != IntPtr.Zero
            && window.TryGetPlatformHandle() != null && window.ClientSize.Width > 0 && window.ClientSize.Height > 0,
            "真实 Show 的生产 Win32/Avalonia 窗口：" + window.Title);
        Write($"WINDOW title={window.Title};hwnd={window.TryGetPlatformHandle()!.Handle};client={window.ClientSize};RenderScaling={window.RenderScaling}");
        CheckLayout(window, "initial-" + window.GetType().Name);
    }

    private static void CheckLayout(Window window, string stage)
    {
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
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string))
            Check(VisibleFraction(button, window) > .99, "关键按钮边界在客户区/滚动视口内：" + button.Content);
        if (window is VoiceImeLanguageWindow)
            foreach (var name in new[] { "RecognitionLanguage", "TargetLanguage", "EnableTranslation", "SaveStatus", "SaveLanguage" })
                Check(VisibleFraction(Find<Control>(window, name), window) > .99, "语言控件/状态完整可见：" + name);
        Write($"LAYOUT stage={stage};client={window.ClientSize};scale={window.RenderScaling};visibleTexts={texts.Length};warnings={LayoutWarnings.Count}");
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
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
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
        // Same resources and ordering as the lightweight production path, without its OnFrameworkInitializationCompleted.
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var source = new TrueFluentPro.App();
        source.Initialize(); // Only production compiled App.axaml; no MainWindow/DI/config/DB initialization.
        var resources = source.Resources;
        source.Resources = new ResourceDictionary();
        Resources = resources;
        foreach (var style in source.Styles.ToArray())
        {
            source.Styles.Remove(style);
            Styles.Add(style);
        }
    }
}