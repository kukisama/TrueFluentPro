using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;

internal static class SongScrollWorkspaceRegression
{
    // 书体设计坐标的验收契约，不从被测 LeftRatio 反推“期望值”。
    private const double PaperLeft = 343, PaperWidth = 2178;
    private static readonly (string Tag, Type Type, double Default)[] Pages =
    {
        ("media", typeof(SongScrollMediaStudioView), .30),
        ("mediav2", typeof(SongScrollMediaCenterV2View), .34),
        ("audiolab", typeof(SongScrollAudioLabView), .30),
        ("taskmonitor", typeof(SongScrollTaskQueueMonitorView), .25),
        ("settings", typeof(SongScrollSettingsView), .25)
    };
    private static readonly ShellAppearancePreference[] LegacyThemes =
    { ShellAppearancePreference.Classic, ShellAppearancePreference.SongStudy, ShellAppearancePreference.InkStudy };

    internal static async Task VerifyAsync(bool interactive = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("长卷工作区专项需要 Windows 窗口环境；严格输入模式还需要可交互桌面。");
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var root = Path.Combine(RepositoryRoot(), "artifacts", "song-scroll-workspace-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Check(!Directory.Exists(root), "拒绝复用历史验收目录。");
        Directory.CreateDirectory(root);
        var report = new List<string>();
        void Log(string value) { report.Add(value); Console.WriteLine(value); }
        var app = Application.Current!;
        var oldTheme = app.RequestedThemeVariant;
        MainWindow? window = null;
        var group = "初始化";
        var passed = false;
        try
        {
            Log("ARTIFACTS=" + root);
            Log("ASSEMBLY=" + typeof(MainWindow).Assembly.Location);
            Log("MODE=" + (interactive ? "STRICT_INTERACTIVE" : "LAYOUT_ONLY"));
            Log("SCOPE=production MainWindow/XAML + controlled in-memory page contexts; "
                + (interactive ? "HWND/SendInput/BitBlt" : "real window/layout + RenderTargetBitmap; no foreground/input helpers")
                + "; no MainVM, service graph, user config/DB, audio/model or cloud calls");
            if (!interactive)
                Log("SKIP_PHYSICAL_INPUT=显式布局模式：不获取前台、不注入系统mouse/keyboard、不真实点击，不用RaiseEvent/OnClick替代；按钮命令/书签路由/拖放捕获与释放/双击复位/键盘与焦点竞争均未验证，SKIP不是PASS");
            Log("NOT_COVERED=真实共享业务VM跨主题引用、已BindViewModel的媒体资产导航键竞争、设置16分区实际表单/自动保存；这些需真实业务初始化，本专项不绕过构造函数伪造VM");
            Log("VISUAL_REVIEW=" + (interactive ? "PNG仅为安全fixture原生截图" : "PNG是RenderTargetBitmap绘制实际fixture控件的渲染截图，不是物理桌面截图，不验证DWM/遮挡/原生命中")
                + "，书缘/折口纹理及前三页设计稿符合度待人工确认；surface同源和布局断言不等于像素视觉验收");
            app.RequestedThemeVariant = ThemeVariant.Dark;
            window = new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root, "appearance.json")))
            { WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = interactive };
            Check(window.DataContext == null, "主窗意外初始化业务，拒绝继续。");
            var screen = window.Screens.All.OrderByDescending(s => s.WorkingArea.Width / s.Scaling)
                .FirstOrDefault(s => s.Scaling > 0 && s.WorkingArea.Width / s.Scaling >= window.MinWidth + 48
                    && s.WorkingArea.Height / s.Scaling >= window.MinHeight + 64)
                ?? throw new InvalidOperationException("没有可容纳生产最小窗体的屏幕；不缩小生产限制冒充验收。");
            window.Width = Math.Max(window.MinWidth, Math.Min(1256, screen.WorkingArea.Width / screen.Scaling - 48));
            window.Height = Math.Max(window.MinHeight, Math.Min(831, screen.WorkingArea.Height / screen.Scaling - 64));
            window.Position = new PixelPoint(screen.WorkingArea.X + 16, screen.WorkingArea.Y + 16);
            window.Show();
            await Pump(window, token);
            Log($"SCREEN={screen.Bounds};work={screen.WorkingArea};scaling={window.RenderScaling};native-DPI=actual monitor only");

            // 先留旧主题字体基线；不构造或绑定任何业务 VM。
            var fonts = new Dictionary<ShellAppearancePreference, string>();
            var legacyPages = new Dictionary<string, Control>();
            foreach (var theme in LegacyThemes)
            {
                window.ApplyShellAppearance(theme);
                foreach (var spec in Pages)
                {
                    var page = await Navigate(window, spec.Tag, token);
                    if (!legacyPages.ContainsKey(spec.Tag)) legacyPages.Add(spec.Tag, page);
                    Check(ReferenceEquals(legacyPages[spec.Tag], page), "旧主题页缓存改变。");
                }
                fonts.Add(theme, LegacyFonts(window));
            }
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            var pages = new Dictionary<string, Control>();
            var contexts = new Dictionary<string, SongScrollWorkspaceFixture>();
            foreach (var spec in Pages)
            {
                group = "page:" + spec.Tag;
                var page = await Navigate(window, spec.Tag, token);
                Check(page.GetType() == spec.Type && page is ISongScrollPage
                    && !ReferenceEquals(page, legacyPages[spec.Tag]), $"{spec.Tag} 不是独立长卷类型/内容树。");
                pages.Add(spec.Tag, page);
                var fixture = new SongScrollWorkspaceFixture();
                contexts.Add(spec.Tag, fixture);
                page.DataContext = fixture;
                await Pump(window, token);
                var shell = ((ISongScrollPage)page).Shell;
                Check(shell.PageKey == spec.Tag && Near(shell.DefaultLeftRatio, spec.Default)
                    && Near(shell.LeftRatio, spec.Default), $"{spec.Tag} 默认左右比例错误。");
                CheckContext(page, fixture);
                CheckLayout(window, shell, spec.Default, Log);
                if (interactive) await Foreground(window, shell, token);
                Capture(window, root, spec.Tag + "-default-normal", interactive, Log);
                var defaultLeft = Bounds(Find<ContentControl>(shell, "ShellLeftPage"), window).Width;
                foreach (var ratio in new[] { .25, .35, .50 })
                {
                    shell.SetLeftRatio(ratio);
                    await Pump(window, token);
                    CheckLayout(window, shell, ratio, Log);
                    if (Near(ratio, .50))
                        Check(Bounds(Find<ContentControl>(shell, "ShellLeftPage"), window).Width > defaultLeft + 100,
                            "设定比例后实际左页没有变宽。");
                    if (Near(ratio, .50) && spec.Tag is "taskmonitor" or "settings")
                        Capture(window, root, spec.Tag + "-ratio-50-normal", interactive, Log);
                }
                shell.SetLeftRatio(spec.Default);
                window.WindowState = WindowState.Maximized;
                await WindowChromeRegression.ScrollStateAsync(window, WindowState.Maximized, token);
                await Pump(window, token);
                CheckLayout(window, shell, spec.Default, Log);
                if (spec.Tag == "mediav2") Capture(window, root, "mediav2-default-maximized", interactive, Log);
                window.WindowState = WindowState.Normal;
                await WindowChromeRegression.ScrollStateAsync(window, WindowState.Normal, token);
                await Pump(window, token);
                CheckLayout(window, shell, spec.Default, Log);
                Check(fixture.Receipt.ExecutionCount == 0, "布局/绑定擅自执行业务命令。");
                if (interactive)
                {
                    await Foreground(window, shell, token);
                    // 点击生产按钮，命令解析自原始绑定；回执仅写入内存计数器。
                    if (spec.Tag != "settings")
                    {
                        var button = page.GetVisualDescendants().OfType<Button>()
                            .First(b => ReferenceEquals(b.Command, fixture.Receipt));
                        await WindowChromeRegression.ScrollClickAsync(window, button, token);
                        await Pump(window, token);
                        Check(fixture.Receipt.ExecutionCount == 1, "真实点击没有且仅有一次到达受控命令。");
                    }
                    else
                    {
                        var list = Find<ListBox>(page, "ScrollSectionNavigation");
                        var item = list.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, "Section_Storage"));
                        await WindowChromeRegression.ScrollClickAsync(window, item, token);
                        Check(ReferenceEquals(list.SelectedItem, item), "配置目录真实点击无选择回执。");
                    }
                    var next = Pages[(Array.FindIndex(Pages, p => p.Tag == spec.Tag) + 1) % Pages.Length];
                    var bookmark = shell.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, next.Tag));
                    await WindowChromeRegression.ScrollClickAsync(window, bookmark, token);
                    await Pump(window, token);
                    Check(CurrentPage(window).GetType() == next.Type && CurrentPage(window).IsEffectivelyVisible,
                        "书签真实Click未经过主窗路由到目标长卷页：" + next.Tag);
                    Log($"PASS {group}: 独立类型/上下文继承/默认与三档真实布局/普通-最大化-还原/命中点击回执/书签路由到{next.Tag}");
                }
                else
                {
                    Log($"PASS_LAYOUT {group}: 独立类型/上下文继承/默认与.25/.35/.50真实布局/普通-最大化-还原几何");
                }
            }

            if (interactive)
            {
                group = "spine-pointer";
                var media = (SongScrollMediaCenterV2View)await Navigate(window, "mediav2", token);
                await VerifyPointer(window, media.Shell, token, Log);
                Log("PASS spine-pointer: 原生按下/移动/捕获/释放、释放后移动不改宽、双击复位并重新测量布局");

                group = "spine-keyboard";
                await VerifyKeyboard(window, media, token, Log);
                Log("PASS spine-keyboard: 媒体页书脊原生方向键/Shift/Home/End/Enter调整实际页宽，输入框方向键不调整书脊（业务VM未绑定）");
            }

            group = "settings-directory";
            var settings = (SongScrollSettingsView)await Navigate(window, "settings", token);
            settings.DataContext = contexts["settings"];
            await Pump(window, token);
            CheckSettingsDirectory(settings);
            Log("PASS settings-directory: 16项唯一目录、标题及生产分区工厂/VM映射键齐全；任务/设置功能位于书脊正确两侧");

            group = "legacy-roundtrip";
            foreach (var theme in LegacyThemes)
            {
                window.ApplyShellAppearance(theme);
                foreach (var spec in Pages)
                {
                    var page = await Navigate(window, spec.Tag, token);
                    Check(ReferenceEquals(legacyPages[spec.Tag], page) && page is not ISongScrollPage,
                        $"{theme}/{spec.Tag} 没有恢复原页面缓存。");
                    Check(Find<Panel>(window, "PageHost").Children.Contains(page)
                        && page.IsEffectivelyVisible, "旧导航没有恢复到 PageHost。");
                }
                Check(LegacyFonts(window) == fonts[theme], $"{theme} 导航/标题字体或字号被长卷污染。");
                if (interactive)
                {
                    await WindowChromeRegression.ScrollForegroundAsync(window, Find<Border>(window, "ShellTitleDragArea"), token);
                    await WindowChromeRegression.ScrollClickAsync(window, Find<Button>(window, "MediaV2NavButton"), token);
                    await Pump(window, token);
                    Check(ReferenceEquals(CurrentPage(window), legacyPages["mediav2"]), "旧导航实际点击没有到达旧媒体页。");
                }
                window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
                foreach (var spec in Pages)
                {
                    var page = await Navigate(window, spec.Tag, token);
                    Check(ReferenceEquals(page, pages[spec.Tag]), "主题往返重建长卷页面。");
                    // 无 MainVM 时 Settings 的宿主协调器会清空上下文，不伪称真实设置VM已共享。
                    if (spec.Tag != "settings") CheckContext(page, contexts[spec.Tag]);
                }
            }
            Check(app.RequestedThemeVariant == ThemeVariant.Dark && window.DataContext == null,
                "外观往返改写全局明暗或意外引入主业务。");
            Check(!File.Exists(Path.Combine(root, "appearance.json")), "仅应用外观却写入了偏好文件。");
            Log("PASS legacy-roundtrip: 三旧主题字体/导航与五页缓存往返；仅四页受控上下文引用保持，不代表真实业务VM引用验证");

            group = "live-shared-surface";
            await VerifyLiveSurface(window, root, interactive, token, Log);
            Log("PASS live-shared-surface: 只测新共用书体/.25/.50与页边联动，不运行原有live业务大专项");
            token.ThrowIfCancellationRequested();
            passed = true;
        }
        catch (Exception ex) { Log($"FAIL group={group}: {ex}"); throw; }
        finally
        {
            try
            {
                window?.Close();
                Check(window == null || !window.IsVisible, "清理后fixture窗口仍可见。");
            }
            catch (Exception ex)
            {
                passed = false;
                Log("FAIL cleanup: " + ex);
                throw;
            }
            finally
            {
                app.RequestedThemeVariant = oldTheme;
                watch.Stop();
                Log($"ELAPSED_MS={watch.Elapsed.TotalMilliseconds:F0}");
                Log("PHYSICAL_INPUT=" + (!interactive ? "SKIP" : passed ? "PASS" : "NOT_COMPLETED"));
                Log((interactive ? "SONG_SCROLL_WORKSPACE_EXIT=" : "SONG_SCROLL_WORKSPACE_LAYOUT_EXIT=") + (passed ? 0 : 1));
                if (!passed) Log("NOT_RUN=失败点之后的检查未完成；不重试、不以已有PASS代替整体通过");
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
            }
        }
    }

    private static void CheckLayout(MainWindow window, SongScrollShell shell, double ratio, Action<string> log)
    {
        var stage = Find<Canvas>(shell, "ShellStage");
        var scene = Find<Canvas>(shell, "ShellScene");
        var surface = Find<SongScrollBookSurface>(shell, "ShellBookSurface");
        var left = Find<ContentControl>(shell, "ShellLeftPage");
        var right = Find<ContentControl>(shell, "ShellRightPage");
        var handle = Find<Border>(shell, "ShellSpineHandle");
        var a = Bounds(stage, window);
        var l = Bounds(left, window);
        var r = Bounds(right, window);
        var scale = a.Width / 2560;
        var spine = PaperLeft + PaperWidth * ratio;
        var seam = Bounds(surface, window).Left + spine * scale;
        log(FormattableString.Invariant($"LAYOUT_CHECK page={shell.PageKey};state={window.WindowState};expectedRatio={ratio:F6};shellRatio={shell.LeftRatio:F6};expectedSpine={spine:F6};shellSpine={shell.SpinePosition:F6};surfaceSpine={surface.SpinePosition:F6};surfaceVisible={surface.IsEffectivelyVisible};surfaceCount={shell.GetVisualDescendants().OfType<SongScrollBookSurface>().Count()};stage={a};client={window.ClientSize}"));
        Check(Math.Abs(a.Left) <= 1 && Math.Abs(a.Top) <= 1
            && Math.Abs(a.Right - window.ClientSize.Width) <= 1
            && Math.Abs(a.Bottom - window.ClientSize.Height) <= 1, "舞台没有实际铺满客户区。");
        Check(Near(shell.LeftRatio, ratio) && Near(shell.SpinePosition, spine)
            && Near(surface.SpinePosition, spine) && surface.IsEffectivelyVisible
            && shell.GetVisualDescendants().OfType<SongScrollBookSurface>().Count() == 1,
            "纸页/上下折口未使用唯一同源书体或比例未生效。");
        Check(Near(surface.SceneHeight, scene.Bounds.Height) && Near(surface.Bounds.Height, scene.Bounds.Height)
            && Math.Abs(Bounds(surface, window).Top + 64 * scale) <= 1,
            "书体没有跟随舞台高度/上裁口。");
        Check(l.Width > 100 && r.Width > 100 && l.Height > 100 && r.Height > 100
            && Math.Abs(l.Left - a.Left - 406 * scale) <= 1
            && Math.Abs(l.Right - (seam - 24 * scale)) <= 1
            && Math.Abs(r.Left - (seam + 47 * scale)) <= 1
            && Math.Abs(r.Right - a.Left - 2463 * scale) <= 1
            && Math.Abs(l.Top - r.Top) <= 1 && Math.Abs(l.Bottom - r.Bottom) <= 1
            && l.Right < seam && r.Left > seam && l.Bottom <= a.Bottom + 1,
            "实际左右页宽度/功能分界错误、内容跨缝或越出舞台。");
        Check(Math.Abs(Bounds(handle, window).Center.X - seam) <= 1, "真实把手不在书体中缝。");
        Check(shell.LeftPage != null && shell.RightPage != null
            && Within(l, Bounds(shell.LeftPage, window)) && Within(r, Bounds(shell.RightPage, window)),
            "业务内容根节点跨过所属纸页，不能只靠裁剪掩盖分界。");
        // 第4/5页的功能界限必须是纸页边界，不是右页里另造一个双栏。
        if (shell.PageKey == "taskmonitor") CheckSide(Find<DataGrid>(shell, "TaskGrid"), shell.RightPage!, r, window);
        if (shell.PageKey == "settings")
        {
            CheckSide(Find<ListBox>(shell, "ScrollSectionNavigation"), shell.LeftPage!, l, window);
            CheckSide(Find<ScrollViewer>(shell, "ScrollSettingsScroller"), shell.RightPage!, r, window);
        }
        log(FormattableString.Invariant($"GEOMETRY page={shell.PageKey};state={window.WindowState};ratio={ratio:F4};stage={a};left={l};right={r};surfaceSpine={surface.SpinePosition:F3};seamClient={seam:F3};sceneHeight={surface.SceneHeight:F3}"));
    }

    private static async Task VerifyPointer(MainWindow window, SongScrollShell shell, CancellationToken token, Action<string> log)
    {
        shell.SetLeftRatio(.35);
        await Pump(window, token);
        await Foreground(window, shell, token);
        var handle = Find<Border>(shell, "ShellSpineHandle");
        var downs = 0; var moves = 0; var ups = 0; var doubles = 0; var capturedOnDown = false;
        IPointer? pointer = null;
        EventHandler<PointerPressedEventArgs> down = (_, e) =>
        {
            downs++; pointer = e.Pointer;
            capturedOnDown |= ReferenceEquals(e.Pointer.Captured, handle);
            if (e.ClickCount == 2) doubles++;
        };
        EventHandler<PointerEventArgs> move = (_, e) => { if (e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) moves++; };
        EventHandler<PointerReleasedEventArgs> up = (_, _) => ups++;
        handle.AddHandler(InputElement.PointerPressedEvent, down, RoutingStrategies.Bubble, true);
        handle.AddHandler(InputElement.PointerMovedEvent, move, RoutingStrategies.Bubble, true);
        handle.AddHandler(InputElement.PointerReleasedEvent, up, RoutingStrategies.Bubble, true);
        try
        {
            WindowChromeRegression.ScrollHover(window, handle); // 同时核验前台 HWND 与实际命中
            var start = handle.PointToScreen(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2));
            var scale = Bounds(Find<Canvas>(shell, "ShellStage"), window).Width / 2560 * window.RenderScaling;
            var end = new PixelPoint(start.X + (int)Math.Round(.10 * PaperWidth * scale), start.Y);
            await FloatingSubtitleRegression.DragAsync(start, end);
            await Pump(window, token);
            Check(downs == 1 && ups == 1 && moves > 0 && capturedOnDown && pointer?.Captured == null
                && !handle.Classes.Contains("dragging") && Math.Abs(shell.LeftRatio - .45) < .004,
                $"原生拖放缺少捕获/释放/移动回执：down={downs},move={moves},up={ups},ratio={shell.LeftRatio}。");
            log($"POINTER down={downs};moveWithLeft={moves};up={ups};capturedOnDown={capturedOnDown};released={pointer?.Captured == null};ratio={shell.LeftRatio:F4}");
            CheckLayout(window, shell, shell.LeftRatio, log);
            var released = shell.LeftRatio;
            WindowChromeRegression.ScrollHover(window, Find<Button>(shell, "ShellMaximize"));
            await Pump(window, token);
            Check(Near(shell.LeftRatio, released) && pointer?.Captured == null, "释放后移动鼠标仍拖动中缝。");
            shell.SetLeftRatio(.5);
            await Pump(window, token);
            await WindowChromeRegression.ScrollDoubleClickAsync(window, handle, token);
            await Pump(window, token);
            Check(doubles == 1 && Near(shell.LeftRatio, shell.DefaultLeftRatio) && pointer?.Captured == null,
                "真实双击未复位/仍保持捕获。");
            CheckLayout(window, shell, shell.DefaultLeftRatio, log);
        }
        finally
        {
            handle.RemoveHandler(InputElement.PointerPressedEvent, down);
            handle.RemoveHandler(InputElement.PointerMovedEvent, move);
            handle.RemoveHandler(InputElement.PointerReleasedEvent, up);
        }
    }

    private static async Task VerifyKeyboard(MainWindow window, SongScrollMediaCenterV2View page, CancellationToken token, Action<string> log)
    {
        var shell = page.Shell;
        shell.SetLeftRatio(.35);
        await Pump(window, token);
        await Foreground(window, shell, token);
        var handle = Find<Border>(shell, "ShellSpineHandle");
        // .35 与刚才双击复位的 .34 中心已分离；物理点击后检查真正焦点归属。
        await WindowChromeRegression.ScrollClickAsync(window, handle, token);
        Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), handle), "书脊实际点击未取得键盘焦点。");
        var keys = 0;
        EventHandler<KeyEventArgs> observe = (_, e) =>
        {
            if (e.Handled) keys++;
            log(FormattableString.Invariant($"SPINE_KEYDOWN key={e.Key};modifiers={e.KeyModifiers};handled={e.Handled};actualRatio={shell.LeftRatio:F6}"));
        };
        handle.AddHandler(InputElement.KeyDownEvent, observe, RoutingStrategies.Bubble, true);
        try
        {
            foreach (var (key, modifiers, expected) in new (ushort, ushort[], double)[]
            {
                (0x27, Array.Empty<ushort>(), .36), (0x25, new ushort[] { 0x10 }, .31),
                (0x24, Array.Empty<ushort>(), .25), (0x23, Array.Empty<ushort>(), .55),
                (0x0D, Array.Empty<ushort>(), shell.DefaultLeftRatio)
            })
            {
                var before = keys;
                await NativeKey(window, modifiers, key, token, log);
                await Pump(window, token);
                log(FormattableString.Invariant($"KEY vk=0x{key:X2};modifierVks={string.Join(",", modifiers.Select(v => $"0x{v:X2}"))};handledReceipt={keys};expectedRatio={expected:F6};actualRatio={shell.LeftRatio:F6}"));
                Check(keys == before + 1, "原生键盘未到达书脊或事件被上层吞掉。");
                CheckLayout(window, shell, expected, log);
            }
        }
        finally { handle.RemoveHandler(InputElement.KeyDownEvent, observe); }
        var text = Find<TextBox>(page, "PromptTextBox");
        await WindowChromeRegression.ScrollClickAsync(window, text, token);
        await NativeKey(window, Array.Empty<ushort>(), 0x23, token, log); // End
        var caret = text.CaretIndex;
        var ratio = shell.LeftRatio;
        Check(caret > 0 && ReferenceEquals(window.FocusManager?.GetFocusedElement(), text), "安全草稿输入框未实际取得焦点。");
        await NativeKey(window, Array.Empty<ushort>(), 0x25, token, log);
        Check(text.CaretIndex == caret - 1 && Near(shell.LeftRatio, ratio), "输入框方向键被书脊抢走或光标未移动。");
        CheckLayout(window, shell, ratio, log);
    }

    private static void CheckSettingsDirectory(SongScrollSettingsView page)
    {
        var expected = new[] { "Endpoints", "ModelSelection", "Storage", "Insight", "WebSearch", "Review", "ImageGen", "VideoGen",
            "Audio", "AudioLab", "Recognition", "VoiceIme", "Text", "Transfer", "About", "Cloud" }.Select(s => "Section_" + s).ToArray();
        var items = Find<ListBox>(page, "ScrollSectionNavigation").Items.OfType<ListBoxItem>().ToArray();
        Check(items.Length == 16 && items.Select(i => i.Tag as string).SequenceEqual(expected)
            && items.All(i => i.Content is Control content && content.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Classes.Contains("section-name") && !string.IsNullOrWhiteSpace(t.Text))), "设置目录缺项、重复、顺序或标签错误。");
        // 仅看工厂/映射字典的键，不调用工厂、不加载敏感分区或构造 SettingsVM。
        foreach (var name in new[] { "SectionFactories", "SectionDataContextResolvers" })
        {
            var dictionary = typeof(SettingsView).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                as System.Collections.IDictionary ?? throw new MissingFieldException(typeof(SettingsView).Name, name);
            Check(expected.All(dictionary.Contains), "目录与生产分区工厂/VM映射脱节：" + name);
        }
    }

    private static async Task VerifyLiveSurface(MainWindow window, string root, bool interactive, CancellationToken token, Action<string> log)
    {
        var page = (SongScrollTranslationView)await Navigate(window, "live", token);
        // live 正由并行生产实现提供公开接口；缺失就明确失败，不跳过或回退旧固定中缝。
        var setter = page.GetType().GetMethod("SetSpineRatio", new[] { typeof(double) })
            ?? throw new MissingMethodException(page.GetType().Name, "SetSpineRatio(double) 尚未编入当前产物");
        var property = page.GetType().GetProperty("SpinePosition")
            ?? throw new MissingMemberException(page.GetType().Name, "SpinePosition");
        var surface = page.GetVisualDescendants().OfType<SongScrollBookSurface>().Single();
        var handle = Find<Border>(page, "ScrollSpineHandle");
        if (interactive)
            await WindowChromeRegression.ScrollForegroundAsync(window, Find<Border>(page, "ScrollTitleDrag"), token);
        foreach (var ratio in new[] { .25, .50 })
        {
            setter.Invoke(page, new object[] { ratio });
            await Pump(window, token);
            var spine = PaperLeft + PaperWidth * ratio;
            var stage = Bounds(Find<Canvas>(page, "ScrollStage"), window);
            var scale = stage.Width / 2560;
            var seam = Bounds(surface, window).Left + spine * scale;
            var left = Bounds(Find<Border>(page, "ScrollConversationPaper"), window);
            var right = Bounds(Find<Grid>(page, "ScrollRightPage"), window);
            Check(property.GetValue(page) is double value && Near(value, spine) && Near(surface.SpinePosition, spine)
                && surface.IsEffectivelyVisible && left.Width > 100 && right.Width > 100
                && Math.Abs(stage.Left) <= 1 && Math.Abs(stage.Top) <= 1
                && Math.Abs(stage.Right - window.ClientSize.Width) <= 1
                && Math.Abs(stage.Bottom - window.ClientSize.Height) <= 1
                && Near(surface.SceneHeight, Find<Canvas>(page, "ScrollScene").Bounds.Height)
                && Math.Abs(left.Left - stage.Left - 406 * scale) <= 1
                && Math.Abs(left.Right - (seam - 2 * scale)) <= 1
                && Math.Abs(right.Left - (seam + 47 * scale)) <= 1
                && Math.Abs(right.Right - stage.Left - 2463 * scale) <= 1
                && Math.Abs(Bounds(handle, window).Center.X - seam) <= 1,
                "live共用surface、左右功能分界和把手未联动。");
            log($"LIVE ratio={ratio};surfaceSpine={surface.SpinePosition};left={left};right={right};stage={stage}");
            Capture(window, root, ratio == .25 ? "live-ratio-25" : "live-ratio-50", interactive, log);
        }
    }

    private static void CheckContext(Control page, object fixture)
    {
        var shell = ((ISongScrollPage)page).Shell;
        Check(ReferenceEquals(page.DataContext, fixture) && ReferenceEquals(shell.DataContext, fixture)
            && ReferenceEquals(shell.LeftPage?.DataContext, fixture) && ReferenceEquals(shell.RightPage?.DataContext, fixture),
            "Shell/左右内容根丢失页面DataContext继承。");
    }
    private static void CheckSide(Control control, Control content, Rect bounds, MainWindow window)
        => Check(control.GetVisualAncestors().Contains(content) && Within(bounds, Bounds(control, window)),
            "功能节点不在所属纸页内：" + control.Name);
    private static string LegacyFonts(MainWindow window)
    {
        var nav = Find<Button>(window, "LiveNavButton").GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "实时翻译");
        var caption = Find<Border>(window, "ShellTitleBar").GetVisualDescendants().OfType<TextBlock>().First();
        return FormattableString.Invariant($"{nav.FontFamily}|{nav.FontSize}|{caption.FontFamily}|{caption.FontSize}");
    }
    private static Control CurrentPage(MainWindow window) => (Control)(typeof(MainWindow)
        .GetField("_currentPage", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new MissingFieldException(nameof(MainWindow), "_currentPage"));
    private static async Task<Control> Navigate(MainWindow window, string tag, CancellationToken token)
    {
        typeof(MainWindow).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tag });
        await Pump(window, token);
        Check(window.DataContext == null, "导航意外创建主业务。");
        var page = CurrentPage(window);
        Check(page.IsEffectivelyVisible, "路由没有显示目标页：" + tag);
        return page;
    }
    private static Task Foreground(MainWindow window, SongScrollShell shell, CancellationToken token)
        => WindowChromeRegression.ScrollForegroundAsync(window, Find<Border>(shell, "ShellTitleDrag"), token);
    private static async Task NativeKey(MainWindow window, ushort[] modifiers, ushort key, CancellationToken token, Action<string> log)
    {
        token.ThrowIfCancellationRequested();
        var focused = window.FocusManager?.GetFocusedElement() as Control
            ?? throw new InvalidOperationException("没有本fixture焦点，拒绝键盘输入。");
        Check(TopLevel.GetTopLevel(focused) == window, "焦点不属于本fixture。");
        WindowChromeRegression.ScrollHover(window, focused); // 复用前台HWND和命中安全闸门
        // 先验证全部映射，再送入真实输入；不反射改写旧字幕专项的私有结构。
        var modifierDowns = modifiers.Select(value => Keyboard(value, false)).ToArray();
        var down = Keyboard(key, false);
        var up = Keyboard(key, true);
        log($"KEY_INJECT vk=0x{key:X2};scan=0x{down.Data.Keyboard.Scan:X2};flags=0x{down.Data.Keyboard.Flags:X4};modifierScans={string.Join(",", modifierDowns.Select(input => $"0x{input.Data.Keyboard.Scan:X2}"))};inputSize={Marshal.SizeOf<NativeInput>()}");
        var pressedModifiers = 0;
        var keyHeld = false;
        try
        {
            foreach (var input in modifierDowns)
            {
                SendKeys(input);
                pressedModifiers++; // 只为 SendInput 确认插入的按下事件负责释放。
            }
            token.ThrowIfCancellationRequested();
            SendKeys(down);
            keyHeld = true;
            SendKeys(up);
            keyHeld = false;
            await Task.Delay(100, token); // 沿用旧 helper 的消息处理等待，修饰键保持到回执产生。
            await Pump(window, token);
        }
        finally
        {
            // 即使取消/回执失败也发送真实 KeyUp；不让 token 或前台检查跳过释放。
            var releases = modifiers.Take(pressedModifiers).Reverse().Select(value => Keyboard(value, true)).ToList();
            if (keyHeld) releases.Insert(0, up);
            if (releases.Count > 0)
            {
                SendKeys(releases.ToArray());
                log($"KEY_RELEASE modifierCount={pressedModifiers};keyWasHeld={keyHeld};scans={string.Join(",", releases.Select(input => $"0x{input.Data.Keyboard.Scan:X2}"))}");
            }
        }
        await Pump(window, token);
    }

    private static NativeInput Keyboard(ushort key, bool up)
    {
        // KEYBDINPUT: SCANCODE 用 wScan 定义物理键，wVk 被忽略；E0 用 EXTENDEDKEY 表达。
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput
        // Scan 1 Make 表：左右 Shift 分别 2A/36，右 Shift 不是扩展键；导航簇是 E0。
        // https://learn.microsoft.com/en-us/windows/win32/inputdev/about-keyboard-input#scan-codes
        var (scan, extended) = key switch
        {
            0x10 or 0xA0 => (0x2A, false), // generic Shift 明确选择左 Shift
            0xA1 => (0x36, false),        // 右 Shift
            0x25 => (0x4B, true),         // Left
            0x27 => (0x4D, true),         // Right
            0x24 => (0x47, true),         // Home
            0x23 => (0x4F, true),         // End
            0x0D => (0x1C, false),        // 主键盘 Enter，不是数字键盘 Enter
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "本专项未定义此键的物理扫描码。")
        };
        return new NativeInput
        {
            Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput
            {
                Key = 0, Scan = (ushort)scan, Flags = 0x0008u | (extended ? 0x0001u : 0u) | (up ? 0x0002u : 0u)
            } }
        };
    }
    private static void SendKeys(params NativeInput[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        var error = Marshal.GetLastWin32Error();
        Check(sent == inputs.Length, $"原生扫描码输入失败：SendInput={sent}/{inputs.Length}, Win32={error}。");
    }
    // INPUT 的联合体必须保留较大的 MOUSEINPUT 尺寸与指针对齐，即使这里只发送键盘。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static async Task Pump(MainWindow window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var composition = ElementComposition.GetElementVisual(window)
            ?? throw new InvalidOperationException("已显示fixture无合成器。");
        await Dispatcher.UIThread.AwaitWithPriority(composition.Compositor.RequestCompositionBatchCommitAsync()
            .Rendered.WaitAsync(token), DispatcherPriority.Background);
    }
    private static void Capture(MainWindow window, string root, string name, bool interactive, Action<string> log)
    {
        var path = Path.Combine(root, name + (interactive ? ".png" : "-rendered.png"));
        Check(!File.Exists(path), "不覆盖已有截图：" + path);
        if (interactive)
        {
            WindowChromeRegression.ScrollCapture(window, path);
            log("SNAPSHOT=" + path);
        }
        else
        {
            // 沿用 InkStudyRegression：绘制已布局的实际控件，不读取BitBlt桌面像素。
            var scale = window.RenderScaling;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(path);
            log($"RENDERED_SNAPSHOT={path};client={window.ClientSize};pixels={bitmap.PixelSize};source=RenderTargetBitmap.Render(fixture window);渲染截图，不是物理桌面截图");
        }
    }
    private static Rect Bounds(Visual visual, Visual ancestor)
        => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(ancestor)
            ?? throw new InvalidOperationException("节点不在同一视觉树。"));
    private static bool Within(Rect outer, Rect inner) => inner.Width > 0 && inner.Height > 0
        && inner.Left >= outer.Left - 1 && inner.Right <= outer.Right + 1
        && inner.Top >= outer.Top - 1 && inner.Bottom <= outer.Bottom + 1;
    private static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) < .01;
    private static T Find<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"{root.GetType().Name}缺少节点{name}。");
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "TrueFluentPro.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("找不到仓库根目录，拒绝保存到未知位置。");
    }
}

// 公开轻量对象供Avalonia反射绑定；没有服务、文件路径、模型或配置持久化。
// 各按钮仍使用生产XAML原绑定，不在测试中替换Command属性。
public sealed class SongScrollWorkspaceFixture
{
    public InkStudyFakeCommand Receipt { get; } = new();
    public InkStudyFakeCommand NewSessionCommand => Receipt;
    public InkStudyFakeCommand NewWorkspaceCommand => Receipt;
    public InkStudyFakeCommand RefreshAudioFilesCommand => Receipt;
    public InkStudyFakeCommand RefreshCommand => Receipt;
    public string DraftPromptText { get; set; } = "安全夹具草稿";
    public bool HasCurrentSession => false;
    public bool HasSelectedTask => false;
    public bool IsImageKind { get; set; } = true;
    public bool IsVideoKind { get; set; }
    public bool IsFilePanelOpen { get; set; } = true;
    public string CurrentFilePath => "";
    public object[] Sessions { get; } = Array.Empty<object>();
    public object[] AudioFiles { get; } = Array.Empty<object>();
    public object[] Buckets { get; } = Array.Empty<object>();
    public object[] CurrentBucketTasks { get; } = Array.Empty<object>();
    public object[] WorkspaceTabs { get; } = Array.Empty<object>();
}