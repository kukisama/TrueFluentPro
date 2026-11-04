using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using TrueFluentPro;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;

internal static class ShellAppearanceRegression
{
    private static readonly Dictionary<string, Color> StudyPalette = new()
    {
        ["PrimaryBrush"] = Color.Parse("#FFAD4436"),
        ["TextPrimaryBrush"] = Color.Parse("#FF302D27"),
        ["PageBackgroundBrush"] = Color.Parse("#FFF5F0E6"),
        ["SurfaceBrush"] = Color.Parse("#FFFAF6EE"),
        ["CardBorderBrush"] = Color.Parse("#FFDFD2B9")
    };

    internal static async Task VerifyAsync()
    {
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        var root = Path.Combine(RepositoryRoot(), "artifacts", "song-study-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Check(!Directory.Exists(root), "本次产物目录已存在，拒绝复用。");
        Directory.CreateDirectory(root);
        var report = new List<string>();
        void Log(string value) { report.Add(value); Console.WriteLine(value); }
        var app = Application.Current ?? throw new InvalidOperationException("回归应用尚未初始化。");
        var originalTheme = app.RequestedThemeVariant;
        var appStyles = app.Styles.ToArray();
        var appResources = app.Resources;
        MainWindow? window = null;
        var monitoring = true;
        var passed = false;
        Log($"ARTIFACTS={root}");
        Log("SCOPE=production MainWindow/LiveTranslationView/editors + controlled INotifyPropertyChanged fixture; no MainVM/config/registry/audio/cloud/AppData");
        Log("INPUT=control activation (RaiseClick), not physical mouse/keyboard; PNG=production UI render, not generated-image/assets review");
        Log("PUMP=UI Background -> UpdateLayout -> CompositionBatch.Rendered -> Background; no fixed delay or hit-result retry");
        try
        {
            var preferencePath = Path.Combine(root, "appearance.json");
            var sentinelPath = Path.Combine(root, "isolation-marker.txt");
            File.WriteAllText(sentinelPath, "appearance regression: preserve this isolated sibling");
            var sentinel = File.ReadAllBytes(sentinelPath);
            var store = VerifyPreferences(preferencePath);
            var savedBytes = File.ReadAllBytes(preferencePath);
            var savedTime = File.GetLastWriteTimeUtc(preferencePath);
            Log("PASS isolated preference file: missing/unknown -> Classic; SongStudy and Classic save/reload; single appearance field");

            app.RequestedThemeVariant = ThemeVariant.Dark;
            window = new MainWindow(store) { Width = 1440, Height = 860 };
            Check(window.DataContext == null, "主窗口不应构造业务 VM。");
            var host = Find<Panel>(window, "PageHost");
            window.Show();
            await UntilAsync(() => host.GetVisualChildren().OfType<LiveTranslationView>().Any(), window, token);
            var live = host.GetVisualChildren().OfType<LiveTranslationView>().Single();
            var fixture = new AppearanceLiveFixture();
            fixture.PublishSample(0);
            live.DataContext = fixture;
            await PumpAsync(window, token);

            // 只把无业务控件放入现有 cache/host，不导航到其他生产页面、不调用其初始化。
            var cache = (Dictionary<string, Control>)(typeof(MainWindow)
                .GetField("_pageCache", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(MainWindow), "_pageCache")).GetValue(window)!;
            var spy = new SpyControl { IsVisible = false, IsHitTestVisible = false };
            cache.Add("appearance-regression-spy", spy);
            host.Children.Add(spy);
            var cachedPages = cache.ToArray();
            var hostChildren = host.Children.ToArray();
            var windowStyles = window.Styles.ToArray();
            var windowContent = window.Content;
            var history = fixture.History;
            var firstHistory = history[0];
            var historyList = live.GetVisualDescendants().OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, history));
            var names = new[] { "UnifiedEditor", "OriginalEditor", "TranslatedEditor" };
            var wrappers = names.Select(name => Find<ConfigurableTextEditor>(live, name)).ToArray();
            var editors = wrappers.Select(editor => editor.GetVisualDescendants().OfType<TextEditor>().Single()).ToArray();
            var richEditors = wrappers.Select(editor => editor.Content).ToArray();
            var documents = editors.Select(editor => editor.Document).ToArray();
            var fontSizes = new[] { 30d, 32d, 34d };
            for (var i = 0; i < editors.Length; i++)
            {
                // 使用生产字号选择器，不覆盖文本绑定或改静态默认字号。
                wrappers[i].GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem = fontSizes[i].ToString("F0");
            }
            await PumpAsync(window, token);
            var backgrounds = editors.Select(editor => BrushColor(editor.Background)).ToArray();
            var foregrounds = editors.Select(editor => BrushColor(editor.Foreground)).ToArray();
            var minHeights = editors.Select(editor => editor.MinHeight).ToArray();
            var classicPalette = ReadPalette(window);
            CheckPalette(live, classicPalette);
            Check(classicPalette["PrimaryBrush"] == Color.Parse("#FF60A5FA"), "经典基线没有使用生产 Dark 色板。");
            CheckTheme(false);

            var detachCounts = new Dictionary<string, int>();
            void Observe(Visual visual, string name)
            {
                detachCounts.Add(name, 0);
                visual.DetachedFromVisualTree += (_, _) => { if (monitoring) detachCounts[name]++; };
                Log($"REF {name}={RuntimeHelpers.GetHashCode(visual)}");
            }
            Observe(host, "PageHost");
            Observe(live, "LiveTranslationView");
            for (var i = 0; i < names.Length; i++)
            {
                Observe(wrappers[i], names[i]);
                Observe(editors[i], names[i] + ".TextEditor");
            }
            Log($"REF History={RuntimeHelpers.GetHashCode(history)}, Spy={RuntimeHelpers.GetHashCode(spy)}");

            void CheckRetained()
            {
                Check(ReferenceEquals(window.Content, windowContent), "外观切换替换了 Window.Content。");
                Check(window.DataContext == null && ReferenceEquals(live.DataContext, fixture), "外观切换改变了页面夹具或引入主 VM。");
                Check(ReferenceEquals(Find<Panel>(window, "PageHost"), host)
                    && ReferenceEquals(host.GetVisualChildren().OfType<LiveTranslationView>().Single(), live), "PageHost 或实时页面被替换。");
                Check(host.Children.Count == hostChildren.Length
                    && host.Children.Zip(hostChildren).All(pair => ReferenceEquals(pair.First, pair.Second)), "外观切换移除、重加或替换了宿主子页面。");
                Check(cache.Count == cachedPages.Length && cachedPages.All(pair => cache.TryGetValue(pair.Key, out var page)
                    && ReferenceEquals(page, pair.Value)) && spy.DetachCount == 0 && ReferenceEquals(spy.Parent, host), "cache/Spy 被移除或替换。");
                Check(detachCounts.Values.All(count => count == 0), "外观切换导致页面/宿主/编辑器 Detach："
                    + string.Join(", ", detachCounts.Select(pair => $"{pair.Key}={pair.Value}")));
                Check(ReferenceEquals(fixture.History, history) && ReferenceEquals(history[0], firstHistory)
                    && ReferenceEquals(historyList.ItemsSource, history) && historyList.ItemCount == history.Count, "历史集合、原条目或真实 ItemsSource 丢失。");
                var expected = new[] { fixture.DisplayedText, fixture.CurrentOriginal, fixture.CurrentTranslated };
                for (var i = 0; i < names.Length; i++)
                {
                    Check(ReferenceEquals(Find<ConfigurableTextEditor>(live, names[i]), wrappers[i])
                        && ReferenceEquals(wrappers[i].Content, richEditors[i])
                        && ReferenceEquals(wrappers[i].GetVisualDescendants().OfType<TextEditor>().Single(), editors[i])
                        && ReferenceEquals(editors[i].Document, documents[i]), $"{names[i]} 或底层编辑器/文档被重建。");
                    Check(wrappers[i].Text == expected[i] && editors[i].Text == expected[i]
                        && Math.Abs(editors[i].FontSize - fontSizes[i]) < 0.01, $"{names[i]} 的真实绑定文本或用户字号丢失。");
                }
            }

            void CheckTheme(bool study)
            {
                Check(window.IsSongStudyAppearance == study
                    && window.CurrentShellAppearance == (study ? ShellAppearancePreference.SongStudy : ShellAppearancePreference.Classic)
                    && live.IsStudyAppearance == study && window.Classes.Contains("song-study") == study,
                    "窗口/页面外观状态不一致。");
                Check(window.ActualThemeVariant == (study ? ThemeVariant.Light : ThemeVariant.Dark)
                    && live.ActualThemeVariant == window.ActualThemeVariant
                    && window.RequestedThemeVariant == (study ? ThemeVariant.Light : ThemeVariant.Default), "窗口主题没有局部 Light/恢复继承 Dark。");
                Check(app.RequestedThemeVariant == ThemeVariant.Dark && app.ActualThemeVariant == ThemeVariant.Dark
                    && ReferenceEquals(app.Resources, appResources) && app.Styles.SequenceEqual(appStyles), "书斋外观污染了 Application 主题/资源/样式。");
                foreach (var (key, color) in classicPalette)
                    Check(app.TryFindResource(key, ThemeVariant.Dark, out var value) && BrushColor(value as IBrush) == color,
                        $"Application 作用域的 Dark 色板 {key} 被局部外观改写。");
            }

            // 两次往返分别覆盖最小窗口与设计尺寸，不扩展到无业务依据的压力/原生任务。
            foreach (var (size, round) in new[] { (new Size(1000, 550), 1), (new Size(1440, 860), 2) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                await PumpAsync(window, token);
                Check(Math.Abs(window.ClientSize.Width - size.Width) <= 1.1
                    && Math.Abs(window.ClientSize.Height - size.Height) <= 1.1, $"未获得请求的真实客户区 {size}，实际 {window.ClientSize}。");
                Check(editors[0].TextArea.Focus(), "受控正文编辑器无法取得 Avalonia 焦点。");
                var focus = window.FocusManager?.GetFocusedElement();
                Check(ReferenceEquals(focus, editors[0].TextArea), "没有实际编辑焦点，不能验收焦点保留。");
                window.ApplyShellAppearance(ShellAppearancePreference.SongStudy);
                await PumpAsync(window, token);
                CheckTheme(true);
                CheckPalette(window, StudyPalette);
                CheckPalette(live, StudyPalette);
                Check(window.Styles.SequenceEqual(windowStyles) && window.Resources.MergedDictionaries.Count == 1,
                    "书斋不应改变已预载样式，只合并一份局部色板。");
                Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), focus), "切入书斋丢失正文焦点。");
                CheckRetained(); // 先验证原正文未丢，再更新；不能靠新样本掩盖切换时的清空。
                fixture.PublishSample(round);
                history.Add(new TranslationItem { Timestamp = new DateTime(2026, 11, 4, 12, round, 0),
                    OriginalText = $"Controlled history {round}", TranslatedText = $"受控历史记录 {round}" });
                await PumpAsync(window, token);
                CheckRetained();
                Check(editors.All(editor => BrushColor(editor.Background).A == 0
                    && BrushColor(editor.Foreground) == StudyPalette["TextPrimaryBrush"]), "书斋编辑器没有采用透明纸面/局部正文色。");
                CheckLayout(window, live, $"study round={round}", Log);
                await ExerciseSettingsAsync(window, live, token, Log);
                CheckRetained();
                if (round == 2) SaveWindow(window, Path.Combine(root, "study.png"), token, Log);

                // 设置交互本来就会移动焦点；重新聚焦后单独验收返经典不丢焦点。
                Check(editors[0].TextArea.Focus(), "返回经典前正文无法重新聚焦。");
                focus = window.FocusManager?.GetFocusedElement();
                window.ApplyShellAppearance(ShellAppearancePreference.Classic);
                await PumpAsync(window, token);
                CheckTheme(false);
                CheckPalette(window, classicPalette);
                CheckPalette(live, classicPalette);
                Check(window.Styles.SequenceEqual(windowStyles) && window.Resources.MergedDictionaries.Count == 0,
                    "经典模式没有真正撤销书斋局部色板，或重新挂载了样式。");
                Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), focus), "返回经典丢失正文焦点。");
                CheckRetained();
                for (var i = 0; i < editors.Length; i++)
                    Check(BrushColor(editors[i].Background) == backgrounds[i] && BrushColor(editors[i].Foreground) == foregrounds[i]
                        && editors[i].MinHeight == minHeights[i], $"{names[i]} 经典编辑器颜色/最小高度未恢复。");
                CheckLayout(window, live, $"classic round={round}", Log);
                if (round == 2) SaveWindow(window, Path.Combine(root, "classic.png"), token, Log);
                Log($"PASS round={round}, client={window.ClientSize}: same page/host/cache/3 editors/documents/history, Detach=0, focus/font/text retained; scoped palette and Dark restored");
            }

            Check(File.ReadAllBytes(preferencePath).SequenceEqual(savedBytes) && File.GetLastWriteTimeUtc(preferencePath) == savedTime,
                "纯 UI ApplyShellAppearance 意外保存了偏好文件。");
            Log("PASS ApplyShellAppearance is UI-only: isolated file bytes/mtime unchanged");

            var themeButton = Find<Button>(window, "ThemeNavButton");
            async Task SelectAppearanceAsync(string header)
            {
                // Button.OnClick 是 IClickableControl.RaiseClick 的生产入口；不直接调用 SaveAndApply。
                themeButton.Flyout?.Hide();
                RaiseClick(themeButton);
                await PumpAsync(window, token);
                var menu = themeButton.Flyout as MenuFlyout ?? throw new InvalidOperationException("主题按钮没有打开生产 MenuFlyout。");
                Check(menu.IsOpen, "真实主题菜单未打开。");
                var item = menu.Items.OfType<MenuItem>().Single(value => value.Header?.ToString()?.Contains(header, StringComparison.Ordinal) == true);
                Check(item.IsEffectivelyVisible && item.IsEffectivelyEnabled && item.GetVisualRoot() != null, "主题选项未呈现或不可用。");
                // 与 MenuItem.RaiseClick 相同的路由，经过生产菜单注册的 SaveAndApplyAppearance。
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                menu.Hide();
                await PumpAsync(window, token);
            }
            await SelectAppearanceAsync("宋人书斋");
            CheckTheme(true);
            CheckRetained();
            Check(new ShellAppearancePreferencesService(preferencePath).Load() == ShellAppearancePreference.SongStudy,
                "书斋菜单没有保存到显式隔离文件。");
            CheckPalette(live, StudyPalette);
            Log("PASS production theme button -> open flyout -> SongStudy MenuItem -> SaveAndApply -> independent reload SongStudy");
            // 故意选浅色：无主 VM 时 UpdateThemeMode 不执行，原 Application Dark 必须保持。
            await SelectAppearanceAsync("经典 · 浅色");
            CheckTheme(false);
            CheckRetained();
            CheckPalette(window, classicPalette);
            CheckPalette(live, classicPalette);
            Check(new ShellAppearancePreferencesService(preferencePath).Load() == ShellAppearancePreference.Classic,
                "经典菜单没有保存到显式隔离文件。");
            Check(fixture.StartCommand.ExecutionCount == 0, "专项不得触发翻译启动命令。");
            Check(File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                "隔离保存损坏了邻接标记或残留本次临时文件。");
            Log("PASS Classic menu save/reload; null MainVM leaves Application Dark; start executions=0; isolated sibling intact, no tmp residue");
            token.ThrowIfCancellationRequested();
            passed = true;
        }
        catch (Exception ex)
        {
            Log($"FAIL {ex.GetType().Name}: {ex.Message}");
            if (window is { IsVisible: true })
            {
                try
                {
                    // 不泵消息、不改布局，保留失败现场；离屏渲染不等于 compositor 已提交。
                    SaveWindow(window, Path.Combine(root, "failure.png"), CancellationToken.None, Log);
                }
                catch (Exception snapshotError) { Log($"SNAPSHOT_ERROR {snapshotError.Message}"); }
            }
            throw;
        }
        finally
        {
            // 正常 Close 的 Detach 是清理，不计入切换期间的零 Detach 断言。
            monitoring = false;
            try
            {
                FindOptional<Button>(window, "ThemeNavButton")?.Flyout?.Hide();
                window?.Close();
                Check(window == null || !window.IsVisible, "测试窗口未关闭。");
            }
            catch (Exception ex)
            {
                passed = false;
                Log($"FAIL cleanup: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                app.RequestedThemeVariant = originalTheme;
                watch.Stop();
                Log($"ELAPSED_MS={watch.Elapsed.TotalMilliseconds:F0}; cleanup=window closed, application theme restored; controlled-state UI only");
                Log($"SHELL_APPEARANCE_EXIT={(passed ? 0 : 1)}");
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
            }
        }
    }

    private static ShellAppearancePreferencesService VerifyPreferences(string path)
    {
        Check(!File.Exists(path), "外观测试文件不应预先存在。");
        var store = new ShellAppearancePreferencesService(path);
        Check(store.Load() == ShellAppearancePreference.Classic && !File.Exists(path), "缺失文件没有无副作用回退 Classic。");
        File.WriteAllText(path, "{\"appearance\":\"UnknownAppearance\"}");
        Check(store.Load() == ShellAppearancePreference.Classic, "未知标识没有回退 Classic。");
        foreach (var appearance in new[] { ShellAppearancePreference.SongStudy, ShellAppearancePreference.Classic })
        {
            store.Save(appearance);
            Check(new ShellAppearancePreferencesService(path).Load() == appearance, "独立服务重载与保存值不一致。");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            Check(json.RootElement.EnumerateObject().Count() == 1
                && json.RootElement.GetProperty("appearance").GetString() == appearance.ToString(), "外观存储写入了其他配置字段。");
        }
        return store;
    }

    private static async Task ExerciseSettingsAsync(MainWindow window, LiveTranslationView live, CancellationToken token, Action<string> log)
    {
        var settings = Find<Expander>(live, "TranslationSettings");
        var scroll = Find<ScrollViewer>(live, "SettingsScroll");
        var toggle = Find<Button>(live, "TranslationToggleButton");
        var header = settings.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "ExpanderHeader");
        var initial = settings.IsExpanded;
        Check(!toggle.GetVisualAncestors().Contains(settings), "开始/停止按钮仍放在设置折叠内容中。");
        Check(scroll.GetVisualAncestors().Contains(settings) && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "设置没有使用生产 Expander 内的独立滚动区。");
        for (var i = 0; i < 2; i++)
        {
            Check(header.Focus(), "译事设置标题不能取得焦点。");
            var before = settings.IsExpanded;
            RaiseClick(header);
            await PumpAsync(window, token);
            Check(settings.IsExpanded != before && scroll.IsEffectivelyVisible == settings.IsExpanded, "设置标题激活没有真正折叠/展开内容。");
            CheckLayout(window, live, $"settings expanded={settings.IsExpanded}", log);
            Check(toggle.IsEffectivelyEnabled && toggle.Command != null, "折叠/展开设置使翻译按钮不可操作或丢失命令绑定。");
            if (settings.IsExpanded)
            {
                CheckVisible(window, scroll, "SettingsScroll", log);
                Check(scroll.Viewport.Height > 0 && scroll.Extent.Height > 0, "展开设置没有可用滚动视口。");
                var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                var offset = scroll.Offset;
                scroll.Offset = new Vector(offset.X, maximum);
                await PumpAsync(window, token);
                Check(scroll.Offset.Y >= 0 && scroll.Offset.Y <= maximum + 1
                    && (maximum <= 1 || scroll.Offset.Y > 0), "有溢出的设置无法在独立 ScrollViewer 内滚动。");
                log($"SCROLL viewport={scroll.Viewport}, extent={scroll.Extent}, offset={scroll.Offset}, overflow={maximum:F1}");
                scroll.Offset = offset;
            }
        }
        Check(settings.IsExpanded == initial, "设置两次激活未恢复初始折叠状态。");
        log("PASS production Expander header activation/collapse/expand and bounded SettingsScroll; translation toggle stays outside/visible, never clicked");
    }

    private static void CheckLayout(MainWindow window, LiveTranslationView live, string stage, Action<string> log)
    {
        log($"LAYOUT {stage}, clientDip={window.ClientSize}, scaling={window.RenderScaling:F2}");
        var transcript = Find<Border>(live, "TranscriptRegion");
        var side = Find<Border>(live, "SideRegion");
        var toggle = Find<Button>(live, "TranslationToggleButton");
        foreach (var control in new Control[] { transcript, side, toggle }) CheckVisible(window, control, control.Name!, log);
        var left = BoundsIn(transcript, window);
        var right = BoundsIn(side, window);
        Check(left.Right <= right.Left + 1, "正文与历史侧栏发生横向重叠。");
        var header = Find<Expander>(live, "TranslationSettings").GetVisualDescendants().OfType<ToggleButton>()
            .Single(button => button.Name == "ExpanderHeader");
        if (live.IsStudyAppearance) CheckVisible(window, header, "ExpanderHeader", log);
    }

    private static void CheckVisible(MainWindow window, Control control, string name, Action<string> log)
    {
        var bounds = BoundsIn(control, window);
        Check(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && Contains(new Rect(window.ClientSize), bounds), $"{name} 未显示或越过窗口客户区：{bounds}。");
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor == window) break;
            if (ancestor.ClipToBounds)
                Check(Contains(BoundsIn(ancestor, window), bounds), $"{name} 被祖先布局裁剪。");
        }
        var first = control.PointToScreen(default);
        var last = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        var clientFirst = window.PointToScreen(default);
        var clientLast = window.PointToScreen(new Point(window.ClientSize.Width, window.ClientSize.Height));
        Check(first.X >= clientFirst.X - 1 && first.Y >= clientFirst.Y - 1
            && last.X <= clientLast.X + 1 && last.Y <= clientLast.Y + 1, $"{name} 的屏幕坐标越过本窗口。");
        if (control is Button button)
        {
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            var hit = window.InputHitTest(center) as Visual;
            Check(button.IsEffectivelyEnabled && (ReferenceEquals(hit, button) || hit?.GetVisualAncestors().Contains(button) == true),
                $"{name} 虽有边界但不可用或被其他控件遮挡。");
        }
        log($"BOUNDS {name}: dip={bounds}, screenPx={first}..{last}, clientPx={clientFirst}..{clientLast}");
    }

    private static Dictionary<string, Color> ReadPalette(Control scope) => StudyPalette.Keys.ToDictionary(key => key, key =>
    {
        Check(scope.TryFindResource(key, scope.ActualThemeVariant, out var value), $"{scope.GetType().Name} 作用域找不到 {key}。");
        return BrushColor(value as IBrush);
    });

    private static void CheckPalette(Control scope, IReadOnlyDictionary<string, Color> expected)
    {
        var actual = ReadPalette(scope);
        foreach (var (key, color) in expected)
            Check(actual[key] == color, $"{scope.GetType().Name} 作用域 {key} 未恢复/应用：{actual[key]} != {color}。");
    }

    private static Color BrushColor(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color
        : throw new InvalidOperationException("应为可观测的实色画刷。");

    // Avalonia 11.3.11 Button.cs: IClickableControl.RaiseClick -> virtual OnClick；
    // ToggleButton.OnClick 先 Toggle 再 base.OnClick，保留 Expander 模板的实际双向绑定。
    // https://github.com/AvaloniaUI/Avalonia/blob/11.3.11/src/Avalonia.Controls/Button.cs
    private static void RaiseClick(Button button)
    {
        Check(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "拒绝激活不可见/禁用的生产控件。");
        (typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(nameof(Button), "OnClick")).Invoke(button, null);
    }

    private static void SaveWindow(MainWindow window, string path, CancellationToken token, Action<string> log)
    {
        token.ThrowIfCancellationRequested();
        var scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
        log($"PASS production window render: {Path.GetFileName(path)}, dip={window.Bounds.Size}, pixels={bitmap.PixelSize}");
        token.ThrowIfCancellationRequested();
    }

    private static async Task PumpAsync(MainWindow window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        // Processed 仅表示已反序列化；Rendered 才完成服务端变换与命中读回更新。
        // Avalonia 11.3.11 Rendering/Composition/Transport/Batch.cs 的公开契约。
        var composition = ElementComposition.GetElementVisual(window)
            ?? throw new InvalidOperationException("真实测试窗口尚未连接合成器。");
        var batch = composition.Compositor.RequestCompositionBatchCommitAsync();
        await Dispatcher.UIThread.AwaitWithPriority(batch.Rendered.WaitAsync(token), DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
    }

    private static async Task UntilAsync(Func<bool> ready, MainWindow window, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            Check(watch.Elapsed < TimeSpan.FromSeconds(3), "OnOpened 没有创建生产实时翻译页面。");
            await PumpAsync(window, token);
        }
        await PumpAsync(window, token);
    }

    private static T Find<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException($"生产控件缺少 {name}。");
    private static T? FindOptional<T>(Control? root, string name) where T : Control => root?.FindControl<T>(name);
    private static Rect BoundsIn(Visual visual, Visual ancestor) => new(visual.TranslatePoint(default, ancestor)
        ?? throw new InvalidOperationException("控件不在同一生产视觉树。"), visual.Bounds.Size);
    private static bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 1 && inner.Top >= outer.Top - 1
        && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "TrueFluentPro.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("找不到仓库根，无法限定专项产物目录。");
    }

    private sealed class SpyControl : Border
    {
        public int DetachCount { get; private set; }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachCount++;
            base.OnDetachedFromVisualTree(e);
        }
    }
}

// 仅提供生产反射绑定所需的受控展示状态，不构造 MainWindowViewModel 或任何服务。
public sealed class AppearanceLiveFixture : INotifyPropertyChanged
{
    private string _original = "", _translated = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentOriginal { get => _original; set { _original = value; Notify(); } }
    public string CurrentTranslated { get => _translated; set { _translated = value; Notify(); Notify(nameof(DisplayedText)); } }
    public string DisplayedText { get => CurrentTranslated; set => CurrentTranslated = value; }
    public bool IsTranslatedView => true;
    public bool IsOriginalView => false;
    public bool IsSingleView => true;
    public bool IsBilingualView => false;
    public ObservableCollection<TranslationItem> History { get; } = new()
    {
        new TranslationItem { Timestamp = new DateTime(2026, 11, 4, 12, 0, 0),
            OriginalText = "Controlled UI history, no audio input.", TranslatedText = "受控历史：外观改变，内容与页面保持。" }
    };
    public bool HasAnyHistory => History.Count > 0;
    public bool IsHistoryEmpty => !HasAnyHistory;
    public bool IsLiveDisplayEmpty => string.IsNullOrEmpty(DisplayedText);
    public int LiveSidePanelTabIndex { get; set; }
    public object LiveInsightTabContent { get; } = new TextBlock { Text = "受控洞察占位，不调用服务。" };
    public string DisplayPlaceholder => "受控正文";
    public bool IsTranslating => false;
    public bool ShowActiveSpeakerTimeline => false;
    public object? ActiveSpeakerTimeline => null;
    public object? AudioDevices => null;
    public AppearanceConfigFixture ConfigVM { get; } = new();
    public string TranslationToggleButtonText => "开始翻译";
    public IBrush TranslationToggleButtonBackground => Brushes.ForestGreen;
    public IBrush TranslationToggleButtonForeground => Brushes.White;
    public AppearanceForbiddenStartCommand StartCommand { get; } = new();
    public ICommand ToggleTranslationCommand => StartCommand;
    public ICommand? ClearHistoryCommand => null;
    public ICommand? OpenHistoryFolderCommand => null;
    public ICommand? ShowFloatingSubtitlesCommand => null;
    public ICommand? ShowFloatingMicSubtitleCommand => null;
    public ICommand? ShowFloatingLoopbackSubtitleCommand => null;
    public ICommand? RestoreFloatingSubtitleInteractionCommand => null;
    public ICommand? ShowFloatingInsightCommand => null;
    public bool IsAnySubtitleOpen => false;
    public bool IsFloatingInsightOpen => false;
    public string StatusMessage => "受控 UI 验收 · 未启动音频或云端";
    public string AudioPipelineTooltip => "未初始化音频管线";
    public string AudioPipelineStatusText => "未启动";

    public void PublishSample(int round)
    {
        CurrentOriginal = $"Controlled revision {round}: the same page and editors remain attached.";
        CurrentTranslated = $"受控正文第 {round} 次更新：听万籁，译四方。\n切换书斋与经典，文字、字号和历史仍然保留。";
        Notify(nameof(IsLiveDisplayEmpty));
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AppearanceConfigFixture
{
    public string[] SpeechResourceNames { get; } = new[] { "隔离展示资源（不联网）" };
    public int ActiveSpeechResourceIndex { get; set; }
    public int SourceLanguageIndex { get; set; } = 1;
    public int TargetLanguageIndex { get; set; } = 1;
    public bool IsConfigurationEnabled => true;
    public IBrush SubscriptionLampFill => Brushes.Gray;
    public IBrush SubscriptionLampStroke => Brushes.Gray;
    public double SubscriptionLampOpacity => 1;
    public string SubscriptionValidationStatusMessage => "隔离展示，不验证订阅";
}

public sealed class AppearanceForbiddenStartCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public int ExecutionCount { get; private set; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        ExecutionCount++;
        throw new InvalidOperationException("外观专项禁止启动翻译。");
    }
}