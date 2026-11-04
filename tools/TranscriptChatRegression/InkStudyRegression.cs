using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.ViewModels;
using TrueFluentPro.ViewModels.Presentation;
using TrueFluentPro.Views;

internal static class InkStudyRegression
{
    private static readonly string[] PaletteKeys =
        { "PrimaryBrush", "TextPrimaryBrush", "PageBackgroundBrush", "SurfaceBrush", "CardBorderBrush" };

    internal static async Task VerifyAsync()
    {
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        var root = Path.Combine(RepositoryRoot(), "artifacts", "ink-study-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Check(!Directory.Exists(root), "拒绝复用已有验收目录。");
        Directory.CreateDirectory(root);
        var report = new List<string>();
        void Log(string value) { report.Add(value); Console.WriteLine(value); }
        var app = Application.Current ?? throw new InvalidOperationException("回归应用尚未初始化。");
        var originalTheme = app.RequestedThemeVariant;
        var appResources = app.Resources;
        var appStyles = app.Styles.ToArray();
        var fake = new InkStudyFakeSession();
        MainWindow? window = null;
        InkStudyTranslationView? ink = null;
        var passed = false;
        Log($"ARTIFACTS={root}");
        Log("SCOPE=production MainWindow + independent InkStudyTranslationView/adapter + public fake; no MainVM, real config/services/AppData/audio/cloud");
        Log("INPUT=Button.OnClick/MenuItem.ClickEvent control activation, not physical input; screenshots=production window only, no asset review/OCR/contact sheet");
        Log("PUMP=Dispatcher Background -> UpdateLayout -> CompositionBatch.Rendered via AwaitWithPriority; budget=40s, entry=45s; no Sleep/Delay/RAF");
        try
        {
            var preferencePath = Path.Combine(root, "appearance.json");
            var store = new ShellAppearancePreferencesService(preferencePath);
            // 不预存 InkStudy：等 OnOpened 创建原实时页后再切换，构造时序另由生产集成负责。
            app.RequestedThemeVariant = ThemeVariant.Dark;
            window = new MainWindow(store) { Width = 1366, Height = 768 };
            Check(window.DataContext == null, "不得构造或绑定真实主 VM。");
            var pageHost = Find<Panel>(window, "PageHost");
            window.Show();
            await WaitForLiveAsync(window, pageHost, token);
            var legacy = pageHost.Children.OfType<LiveTranslationView>().Single();
            // 原页只检查实例/cache/资源，不换成新接口 fake，也不修改旧 AppearanceLiveFixture。
            var legacyContext = legacy.DataContext;
            var legacyResources = legacy.Resources;
            var legacyStyles = legacy.Styles.ToArray();
            var content = window.Content;
            var windowResources = window.Resources;
            var windowStyles = window.Styles.ToArray();
            var classicMerged = window.Resources.MergedDictionaries.ToArray();
            var classicPalette = ReadPalette(window);
            var legacyPalette = ReadPalette(legacy);
            var cache = (Dictionary<string, Control>)(typeof(MainWindow)
                .GetField("_pageCache", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(MainWindow), "_pageCache")).GetValue(window)!;
            Check(cache.Count == 1 && cache.Values.Single() == legacy, "起始缓存应只有实时翻译页。");

            void CheckRetained()
            {
                Check(window.DataContext == null && ReferenceEquals(window.Content, content)
                    && ReferenceEquals(window.Resources, windowResources) && window.Styles.SequenceEqual(windowStyles),
                    "主题切换替换了主窗内容/资源/样式，或引入真实主 VM。");
                Check(ReferenceEquals(Find<Panel>(window, "PageHost"), pageHost)
                    && pageHost.Children.OfType<LiveTranslationView>().Single() == legacy
                    && cache.Count == 2 && cache.Values.Contains(legacy) && cache.Values.Contains(ink!),
                    "旧页/新页未独立缓存，或导航创建了其他业务页面。");
                Check(ReferenceEquals(legacy.DataContext, legacyContext) && ReferenceEquals(legacy.Resources, legacyResources)
                    && legacy.Styles.SequenceEqual(legacyStyles), "切换污染了原实时页的绑定/资源/样式。");
                Check(app.RequestedThemeVariant == ThemeVariant.Dark && app.ActualThemeVariant == ThemeVariant.Dark
                    && ReferenceEquals(app.Resources, appResources) && app.Styles.SequenceEqual(appStyles),
                    "新主题污染了 Application 的 Dark/资源/样式。");
                foreach (var (key, color) in classicPalette)
                    Check(app.TryFindResource(key, ThemeVariant.Dark, out var value) && ColorOf(value as IBrush) == color,
                        $"应用级 Dark 色板 {key} 被改写。");
                fake.CheckLegacyState();
            }

            window.ApplyShellAppearance(ShellAppearancePreference.InkStudy);
            await PumpAsync(window, token);
            var inkHost = Find<Panel>(window, "InkStudyHost");
            ink = inkHost.Children.OfType<InkStudyTranslationView>().Single();
            ink.BindSession(fake);
            await PumpAsync(window, token);
            CheckInkActive(window, ink);
            CheckRetained();
            Check(ink is not null && !ReferenceEquals(ink, legacy) && ink.Presentation is not null
                && !ReferenceEquals(ink.Presentation, fake) && ReferenceEquals(ink.Presentation.Session, fake)
                && ReferenceEquals(ink.DataContext, ink.Presentation), "新 View/adapter 未独立，或 Session 不是同一个 fake。");
            var pageChildren = ink.GetVisualDescendants().ToArray();
            Check(!pageChildren.Any(value => value is LiveTranslationView or ConfigurableTextEditor or TabControl),
                "独立新页仍包含旧翻译页、编辑器或折叠为页签的右侧分区。");
            var liveNav = pageChildren.OfType<Button>().Single(button => button.Tag?.ToString() == "live");
            Check(liveNav.GetVisualAncestors().Contains(ink) && !Find<Control>(window, "LegacyWorkspace").IsVisible,
                "新导航不属于独立 View，或旧导航仍叠在页面上。");
            RaiseClick(liveNav); // 只走 live，不激活其他真业务页。
            await PumpAsync(window, token);
            Check(ReferenceEquals(inkHost.Children.OfType<InkStudyTranslationView>().Single(), ink), "live 导航重建了新页面。");
            // 无主 VM 的主窗在导航时会 BindSession(null)；随后只重绑受控源。
            ink.BindSession(fake);
            await PumpAsync(window, token);
            CheckRetained();
            Log("PASS independent View/adapter/Session identity; own live navigation; legacy page/cache/resources retained, no other page activated");

            foreach (var size in new[] { new Size(1366, 768), new Size(1440, 860), new Size(1000, 550) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                Find<ScrollViewer>(ink, "InkPageScroll").Offset = default;
                await PumpAsync(window, token);
                Check(Math.Abs(window.ClientSize.Width - size.Width) <= 1.1
                    && Math.Abs(window.ClientSize.Height - size.Height) <= 1.1,
                    $"真实客户区不是请求尺寸 {size}：{window.ClientSize}。");
                CheckBookLayout(window, ink, fake, size.Width != 1000, Log);
                CheckConversation(ink, fake, size.Width != 1000, Log);
                if (size.Width == 1366)
                    SaveWindow(window, Path.Combine(root, "ink-study-default.png"), token, Log);
                else if (size.Width == 1440)
                {
                    await CheckLocalControlsAsync(window, ink, fake, token, Log);
                    await CheckSessionBindingsAsync(window, ink, fake, token, Log);
                    var start = Find<Button>(ink, "InkStartButton");
                    CheckHit(window, start);
                    Check(ReferenceEquals(start.Command, fake.ToggleTranslationCommand), "主动作不是 fake 的绑定命令。");
                    RaiseClick(start);
                    await PumpAsync(window, token);
                    Check(fake.ToggleTranslationCommand.ExecutionCount == 1 && fake.BusinessExecutionCount == 1,
                        "主按钮没有仅执行一次 fake 计数器，或顺带执行了其他业务命令。");
                    Log("PASS main action visible/hit-testable -> same FakeSession.ToggleTranslationCommand, count=1; no audio/cloud");
                }
                else
                {
                    var outer = Find<ScrollViewer>(ink, "InkPageScroll");
                    var canvas = Find<Grid>(ink, "InkCanvas");
                    Check(canvas.MinHeight >= 704 && outer.Extent.Height > outer.Viewport.Height,
                        "最小窗口应保持完整分区并使用外层滚动，不能压扁/隐藏分区。");
                    outer.ScrollToEnd();
                    await PumpAsync(window, token);
                    CheckHit(window, Find<Button>(ink, "InkStartButton"));
                    SaveWindow(window, Path.Combine(root, "ink-study-minimum.png"), token, Log);
                    foreach (var name in new[] { "InkHistoryPanel", "InkInsightPanel", "InkSettingsPanel" })
                    {
                        var panel = Find<Control>(ink, name);
                        var bounds = BoundsIn(panel, outer);
                        var top = outer.Offset.Y + bounds.Top;
                        outer.Offset = new Vector(0, Math.Clamp(top - 8, 0, Math.Max(0, outer.Extent.Height - outer.Viewport.Height)));
                        await PumpAsync(window, token);
                        CheckVisible(window, panel);
                    }
                    Log("PASS minimum: all three sections reachable by outer scroll; ScrollToEnd exposes hit-testable primary action; no section hidden");
                }
                CheckRetained();
            }

            await CheckProjectionAsync(window, ink, fake, token, Log);
            Check(fake.BusinessExecutionCount == 1, "呈现更新/释放触碰了 fake 业务命令或生命周期。");

            // 只切 UI 不保存；路径始终显式，连默认外观路径也不读取。
            Check(!File.Exists(preferencePath), "ApplyShellAppearance/live 导航意外保存了偏好。");
            window.Width = 1440;
            window.Height = 860;
            Find<ScrollViewer>(ink, "InkPageScroll").Offset = default;
            await PumpAsync(window, token);
            await SelectThemeAsync(window, Find<Button>(ink, "InkThemeButton"), "原稿", token);
            CheckInkActive(window, ink);
            Check(new ShellAppearancePreferencesService(preferencePath).Load() == ShellAppearancePreference.InkStudy,
                "InkThemeButton.ThemeRequested 未经主窗菜单保存/重载 InkStudy。");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(preferencePath)))
                Check(json.RootElement.EnumerateObject().Count() == 1
                    && json.RootElement.GetProperty("appearance").GetString() == nameof(ShellAppearancePreference.InkStudy),
                    "隔离外观存储写入了业务字段。");
            ink.BindSession(fake);
            await PumpAsync(window, token);
            Check(ReferenceEquals(ink.Presentation!.Session, fake), "菜单返回原稿后 Session 绑定丢失。");
            Log("PASS InkThemeButton -> ThemeRequested -> MainWindow MenuFlyout -> InkStudy save/reload (isolated appearance.json only)");

            await SelectThemeAsync(window, Find<Button>(ink, "InkThemeButton"), "宋人书斋 · 明亮", token);
            Check(window.CurrentShellAppearance == ShellAppearancePreference.SongStudy && legacy.IsEffectivelyVisible
                && legacy.IsStudyAppearance && window.ActualThemeVariant == ThemeVariant.Light && !ink.IsEffectivelyVisible,
                "SongStudy 未返回原有 LiveTranslationView。");
            CheckRetained();
            Check(ReferenceEquals(cache.Values.OfType<LiveTranslationView>().Single(), legacy), "SongStudy 没有复用经典实时页。");
            Check(ReadPalette(window)["PrimaryBrush"] == Color.Parse("#FFAD4436"), "旧书斋局部色板没有生效。");
            await SelectThemeAsync(window, Find<Button>(window, "ThemeNavButton"), "经典 · 浅色", token);
            Check(window.CurrentShellAppearance == ShellAppearancePreference.Classic && legacy.IsEffectivelyVisible
                && !legacy.IsStudyAppearance && !ink.IsEffectivelyVisible
                && window.RequestedThemeVariant == ThemeVariant.Default && window.ActualThemeVariant == ThemeVariant.Dark,
                "返经典没有恢复旧实时页及继承 Dark（无主 VM 不改变应用明暗）。");
            Check(window.Resources.MergedDictionaries.SequenceEqual(classicMerged), "返经典未撤销局部色板。");
            CheckPalette(window, classicPalette);
            CheckPalette(legacy, legacyPalette);
            CheckRetained();
            Check(new ShellAppearancePreferencesService(preferencePath).Load() == ShellAppearancePreference.Classic,
                "经典菜单未保存到本次隔离文件。");

            // 再进入新页只验证缓存与同源重绑，不要求无主 VM 的空绑定保留旧 adapter。
            window.ApplyShellAppearance(ShellAppearancePreference.InkStudy);
            await PumpAsync(window, token);
            Check(ReferenceEquals(inkHost.Children.OfType<InkStudyTranslationView>().Single(), ink), "返回新主题没有复用独立页面缓存。");
            ink.BindSession(fake);
            await PumpAsync(window, token);
            CheckInkActive(window, ink);
            Check(ReferenceEquals(ink.Presentation!.Session, fake), "返回新主题不是原 fake 会话。");
            window.ApplyShellAppearance(ShellAppearancePreference.Classic);
            await PumpAsync(window, token);
            CheckRetained();
            CheckPalette(window, classicPalette);
            CheckPalette(legacy, legacyPalette);
            Check(window.Resources.MergedDictionaries.SequenceEqual(classicMerged) && fake.BusinessExecutionCount == 1,
                "往返污染了旧资源或触发 fake 业务计数。");
            Log("PASS InkStudy/SongStudy/Classic round trip: same cached legacy/new pages; styles/palette restored; Application stays Dark; fake old mode/font unchanged");
            token.ThrowIfCancellationRequested();
            passed = true;
        }
        catch (Exception ex)
        {
            Log($"FAIL {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            try
            {
                if (ink != null)
                    foreach (var button in ink.GetVisualDescendants().OfType<Button>()) button.Flyout?.Hide();
                window?.FindControl<Button>("ThemeNavButton")?.Flyout?.Hide();
                ink?.DisposePresentation();
                window?.Close();
                Check(window == null || !window.IsVisible, "测试窗口未关闭。");
                Check(fake.StopCount == 0 && fake.DisposeCount == 0
                    && fake.BusinessExecutionCount == fake.ToggleTranslationCommand.ExecutionCount,
                    "清理窗口/adapter 停止、释放或执行了 fake 其他业务。");
                fake.CheckLegacyState();
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
                Log($"INK_STUDY_EXIT={(passed ? 0 : 1)}");
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
            }
        }
    }

    private static async Task CheckProjectionAsync(MainWindow window, InkStudyTranslationView view,
        InkStudyFakeSession fake, CancellationToken token, Action<string> log)
    {
        var adapter = view.Presentation!;
        var frame = fake.LatestRealtimeFrame!;
        var active = adapter.Conversation.Single(card => card.SegmentKey == frame.SegmentKey);
        var beforeHistory = fake.History.ToArray();
        var business = fake.BusinessExecutionCount;
        fake.Publish(frame with { OriginalText = "Let us begin the discussion.", TranslatedText = "让我们开始今天的讨论。" });
        await PumpAsync(window, token);
        Check(adapter.Conversation.Count == 3 && ReferenceEquals(adapter.Conversation.Last(), active)
            && active.TranslatedText == fake.CurrentTranslated && active.IsLive, "partial 修订重建/追加了同一句卡片。");
        fake.Publish(fake.LatestRealtimeFrame! with { IsFinal = true });
        await PumpAsync(window, token);
        Check(ReferenceEquals(adapter.Conversation.Last(), active) && !active.IsLive, "final-before-history 重复或替换了卡片。");
        var final = fake.LatestRealtimeFrame!;
        var historyItem = new TranslationItem { Timestamp = final.Timestamp, OriginalText = final.OriginalText,
            TranslatedText = final.TranslatedText, Source = final.Source, SubtitleSource = final.Source,
            SubtitleSegmentKey = final.SegmentKey };
        fake.History.Insert(0, historyItem);
        await PumpAsync(window, token);
        Check(adapter.Conversation.Count == 3 && adapter.Conversation.Count(card => card.SegmentKey == final.SegmentKey) == 1
            && ReferenceEquals(adapter.Conversation.Last(), active) && adapter.RecentHistory.Contains(active),
            "final 交接业务 History 后没有去重/保留同一卡片。");
        Check(fake.History.Count == beforeHistory.Length + 1 && ReferenceEquals(fake.History[0], historyItem)
            && fake.History.Skip(1).SequenceEqual(beforeHistory), "adapter 排序或改写了业务历史集合。");
        fake.Publish(new LiveTranslationFrame("A voice without source metadata.", "这条发言没有来源元数据。",
            final.Timestamp.AddSeconds(5), VadGateController.ActiveSource.None, false, "ink-unknown"));
        await PumpAsync(window, token);
        var unknown = adapter.Conversation.Single(card => card.SegmentKey == "ink-unknown");
        var mic = adapter.Conversation.First(card => card.Source == VadGateController.ActiveSource.Mic);
        var loopback = adapter.Conversation.First(card => card.Source == VadGateController.ActiveSource.Loopback);
        Check(unknown.SpeakerLabel == "发言" && unknown.Source == VadGateController.ActiveSource.None
            && ColorOf(unknown.AccentBrush) != ColorOf(mic.AccentBrush)
            && ColorOf(unknown.AccentBrush) != ColorOf(loopback.AccentBrush)
            && ColorOf(unknown.BubbleBrush) != ColorOf(mic.BubbleBrush)
            && ColorOf(unknown.BubbleBrush) != ColorOf(loopback.BubbleBrush), "未知来源被猜成我/对方或使用分源颜色。");
        var unknownScroll = Find<ScrollViewer>(view, "InkConversationScroll");
        unknownScroll.ScrollToEnd();
        await PumpAsync(window, token);
        Check(unknownScroll.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => ReferenceEquals(text.DataContext, unknown) && text.Text == "发言" && text.IsEffectivelyVisible),
            "中性来源标签没有通过真实卡片模板呈现。");

        var snapshot = adapter.Conversation.Select(card => (Card: card, card.OriginalText, card.TranslatedText, card.IsLive)).ToArray();
        var recent = adapter.RecentHistory.ToArray();
        var changed = 0;
        adapter.Changed += (_, _) => changed++;
        view.DisposePresentation();
        Check(view.Presentation == null && view.DataContext == null, "释放新页 adapter 后仍保留绑定。");
        fake.Publish(final with { SegmentKey = "after-dispose", TranslatedText = "释放后不得写入旧呈现。" });
        fake.History.Add(new TranslationItem { Timestamp = final.Timestamp.AddMinutes(1), OriginalText = "After dispose",
            TranslatedText = "仅更新受控源", Source = VadGateController.ActiveSource.Loopback });
        await PumpAsync(window, token);
        Check(changed == 0 && adapter.Conversation.Count == snapshot.Length && adapter.RecentHistory.SequenceEqual(recent)
            && snapshot.Select((entry, index) => ReferenceEquals(adapter.Conversation[index], entry.Card)
                && entry.Card.OriginalText == entry.OriginalText && entry.Card.TranslatedText == entry.TranslatedText
                && entry.Card.IsLive == entry.IsLive).All(value => value)
            && fake.BusinessExecutionCount == business, "Dispose 后 adapter 仍更新，或释放触碰了 fake 业务。");
        fake.ResetSample();
        view.BindSession(fake);
        await PumpAsync(window, token);
        Check(!ReferenceEquals(view.Presentation, adapter) && ReferenceEquals(view.Presentation!.Session, fake),
            "重新绑定没有获得独立且同源的新 adapter。");
        log("PASS partial same card -> final -> History handoff deduplicated; UnknownSource neutral; disposed adapter frozen on frame/history updates, business counters unchanged");
    }

    private static async Task CheckLocalControlsAsync(MainWindow window, InkStudyTranslationView view,
        InkStudyFakeSession fake, CancellationToken token, Action<string> log)
    {
        var adapter = view.Presentation!;
        foreach (var (name, original, translated) in new[]
                 { ("InkOriginalButton", true, false), ("InkTranslatedButton", false, true), ("InkBilingualButton", true, true) })
        {
            var button = Find<ToggleButton>(view, name);
            RaiseClick(button);
            await PumpAsync(window, token);
            Check(button.IsChecked == true && adapter.ShowOriginalLine == original && adapter.ShowTranslatedLine == translated,
                $"{name} 未改变新页本地显示模式。");
            foreach (var card in adapter.Conversation)
            {
                var text = ConversationText(view, card);
                Check(text.Single(value => value.Text == card.OriginalText).IsEffectivelyVisible == original
                    && text.Single(value => value.Text == card.TranslatedText).IsEffectivelyVisible == translated,
                    "显示模式只改状态，实际原文/译文行未跟随。");
            }
            fake.CheckLegacyState();
        }
        var font = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => AutomationProperties.GetName(combo) == "本页对话字号");
        Check(font.SelectedItem is int selectedSize && selectedSize == 16 && adapter.ConversationFontSize == 16,
            "新页默认正文字号不是 16，或字号选择未绑定。");
        foreach (var size in new[] { 20, 16 })
        {
            font.SelectedItem = size;
            await PumpAsync(window, token);
            Check(adapter.ConversationFontSize == size && adapter.Conversation.All(card =>
                ConversationText(view, card).All(text => Math.Abs(text.FontSize - size) < 0.01)),
                "本地字号选择未作用于真实卡片文字。");
            fake.CheckLegacyState();
        }
        log("PASS actual local display toggles/16->20->16 font selector update card text; FakeSession legacy mode/default font=38 unchanged");
    }

    private static async Task CheckSessionBindingsAsync(MainWindow window, InkStudyTranslationView view,
        InkStudyFakeSession fake, CancellationToken token, Action<string> log)
    {
        var resource = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => ReferenceEquals(combo.ItemsSource, fake.ConfigVM.SpeechResourceNames));
        Check(resource.SelectedIndex == fake.ConfigVM.ActiveSpeechResourceIndex && resource.IsEffectivelyEnabled,
            "Session.ConfigVM 的受控语音资源没有真实绑定。");
        var insight = Find<ScrollViewer>(view, "InkInsightScroll");
        var markdown = insight.GetVisualDescendants().OfType<ChatMarkdownBlock>().Single();
        Check(markdown.Markdown == fake.AiInsight.InsightMarkdown && markdown.Children.Count > 1
            && markdown.GetVisualDescendants().OfType<SelectableTextBlock>().Any(),
            "洞察没有把受控长 Markdown 输入真实 ChatMarkdownBlock 并生成内容。");
        Check(double.IsFinite(markdown.MaxContentWidth) && markdown.MaxContentWidth > 0
            && markdown.MaxContentWidth <= insight.Viewport.Width + 1 && insight.Extent.Height > insight.Viewport.Height,
            "真实洞察没有有限排版宽度/可滚动的长结果。");
        insight.ScrollToEnd();
        await PumpAsync(window, token);
        Check(insight.Offset.Y > 0, "长洞察实际不能滚动。");
        insight.Offset = default;

        foreach (var name in new[] { "InkDevicesButton", "InkInsightOptionsButton" })
        {
            var button = Find<Button>(view, name);
            RaiseClick(button);
            await PumpAsync(window, token);
            var flyout = button.Flyout as Flyout ?? throw new InvalidOperationException($"缺少生产 {name} Flyout。");
            try
            {
                Check(flyout.IsOpen && flyout.Content is Control, $"{name} 没有实际打开。");
                var body = (Control)flyout.Content!;
                Check(ReferenceEquals(body.DataContext, view.Presentation), "弹层没有继承新 adapter。");
                if (name == "InkDevicesButton")
                {
                    var combos = body.GetVisualDescendants().OfType<ComboBox>().ToArray();
                    Check(combos.Any(combo => ReferenceEquals(combo.ItemsSource, fake.AudioDevices.AudioDevicesList)
                            && ReferenceEquals(combo.SelectedItem, fake.AudioDevices.SelectedAudioDevice))
                        && combos.Any(combo => ReferenceEquals(combo.ItemsSource, fake.AudioDevices.OutputDevicesList)
                            && ReferenceEquals(combo.SelectedItem, fake.AudioDevices.SelectedOutputDevice))
                        && body.GetVisualDescendants().OfType<Button>().Any(value => ReferenceEquals(value.Command, fake.AudioDevices.RefreshAudioDevicesCommand)),
                        "Session.AudioDevices 的设备列表/选择/刷新命令绑定不完整。");
                }
                else
                {
                    Check(body.GetVisualDescendants().OfType<ItemsControl>().Any(items => ReferenceEquals(items.ItemsSource, fake.AiInsight.InsightPresetButtons))
                        && body.GetVisualDescendants().OfType<Button>().Any(value => ReferenceEquals(value.Command, fake.AiInsight.SendInsightCommand))
                        && body.GetVisualDescendants().OfType<TextBox>().Any(text => text.Text == fake.AiInsight.InsightUserInput),
                        "Session.AiInsight 的预设/输入/命令没有真实绑定。");
                }
            }
            finally { flyout.Hide(); }
            await PumpAsync(window, token);
        }
        log("PASS real ChatMarkdownBlock long input/finite width/scroll; ConfigVM language/resource + AudioDevices + AiInsight popup bindings use public fakes only");
    }

    private static void CheckBookLayout(MainWindow window, InkStudyTranslationView view,
        InkStudyFakeSession fake, bool fullViewport, Action<string> log)
    {
        var frame = view.GetVisualDescendants().OfType<InkStudyBookFrame>().Single();
        var book = BoundsIn(frame, window);
        // 对应生产五列 20,*,28,*,20 和上下 20 留白，不以资产像素/OCR猜安全区。
        var rightSafe = new Rect(book.Left + book.Width / 2 + 14, book.Top + 20,
            book.Width / 2 - 34, book.Height - 40);
        var panels = new[] { "InkHistoryPanel", "InkInsightPanel", "InkSettingsPanel" }
            .Select(name => Find<Control>(view, name)).ToArray();
        var rectangles = panels.Select(panel => BoundsIn(panel, window)).ToArray();
        for (var index = 0; index < panels.Length; index++)
        {
            Check(panels[index].IsEffectivelyVisible && panels[index].Bounds.Width > 0 && panels[index].Bounds.Height > 0,
                $"{panels[index].Name} 被隐藏或压为零。");
            Check(Contains(rightSafe, rectangles[index]), $"{panels[index].Name} 越出右书页安全区：{rectangles[index]}，safe={rightSafe}。");
            for (var other = 0; other < index; other++)
                Check(!Overlaps(rectangles[index], rectangles[other]), "历史/洞察/设置区域发生矩形重叠。");
            if (fullViewport) CheckVisible(window, panels[index]);
        }
        var insight = Find<ScrollViewer>(view, "InkInsightScroll");
        Check(insight.Viewport.Height > 0 && (!fullViewport || insight.Viewport.Height >= 60),
            $"洞察真实视口不足：{insight.Viewport}。");
        if (fullViewport)
        {
            CheckHit(window, Find<Button>(view, "InkStartButton"));
            var source = Find<ComboBox>(view, "InkSourceLanguage");
            var target = Find<ComboBox>(view, "InkTargetLanguage");
            Check(source.SelectedIndex == fake.ConfigVM.SourceLanguageIndex && target.SelectedIndex == fake.ConfigVM.TargetLanguageIndex
                && source.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString()).SequenceEqual(fake.ConfigVM.SourceLanguages)
                && target.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString()).SequenceEqual(fake.ConfigVM.TargetLanguages),
                "Session.ConfigVM 的源/目标语言索引或生产选项顺序不符。");
            var sourceGroup = source.GetVisualAncestors().OfType<Grid>().First();
            var targetGroup = target.GetVisualAncestors().OfType<Grid>().First();
            Check(ReferenceEquals(sourceGroup.Parent, targetGroup.Parent)
                && !Overlaps(BoundsIn(sourceGroup, window), BoundsIn(targetGroup, window)), "源/目标语言不再成组或发生重叠。");
            foreach (var combo in new[] { source, target })
            {
                CheckVisible(window, combo);
                var presenter = combo.GetVisualDescendants().OfType<ContentPresenter>().Single(value => value.Name == "ContentPresenter");
                var text = presenter.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                Check(text != null && !string.IsNullOrWhiteSpace(text.Text)
                    && text.TextLayout.WidthIncludingTrailingWhitespace <= presenter.Bounds.Width + 1
                    && text.TextLayout.Height <= presenter.Bounds.Height + 1,
                    $"{combo.Name} 的当前语言文本被裁切或未绑定。");
                Check(Contains(BoundsIn(Find<Control>(view, "InkSettingsPanel"), window), BoundsIn(combo, window)),
                    "语言选择越出译事设置区。");
            }
            foreach (var group in new[] { sourceGroup, targetGroup })
            {
                var label = group.Children.OfType<TextBlock>().Single();
                CheckVisible(window, label);
                Check(label.TextLayout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1
                    && label.TextLayout.Height <= label.Bounds.Height + 1, "源/目标语言组的标签被裁切。");
            }
        }
        log($"PASS layout client={window.ClientSize}, safe={rightSafe}; history={rectangles[0]}, insight={rectangles[1]}, settings={rectangles[2]}, insightViewport={insight.Viewport}, allVisible={fullViewport}");
    }

    private static void CheckConversation(InkStudyTranslationView view, InkStudyFakeSession fake, bool requireVisibleCards, Action<string> log)
    {
        var adapter = view.Presentation!;
        Check(ReferenceEquals(adapter.Session, fake) && adapter.Conversation.Count == 3 && adapter.ConversationFontSize == 16,
            "示例没有形成两条历史和一条实时卡片，或默认字号不是 16。");
        var scroll = Find<ScrollViewer>(view, "InkConversationScroll");
        var viewport = BoundsIn(scroll, view);
        var visible = 0;
        foreach (var card in adapter.Conversation)
        {
            var texts = ConversationText(view, card);
            Check(texts.Length == 2 && texts.All(text => text.IsEffectivelyVisible && Math.Abs(text.FontSize - 16) < 0.01),
                "实际双语卡片正文未按 16 呈现（不能继承旧 38）。");
            var label = scroll.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => ReferenceEquals(text.DataContext, card) && text.Text == card.SpeakerLabel);
            Check(card.SpeakerLabel == (card.Source == VadGateController.ActiveSource.Mic ? "我" : "对方")
                && label.IsEffectivelyVisible, "我/Mic 与对方/Loopback 的来源标记不符。");
            if (texts.Any(text => Overlaps(BoundsIn(text, view), viewport))) visible++;
        }
        Check(adapter.Conversation.Any(card => card.Source == VadGateController.ActiveSource.Mic)
            && adapter.Conversation.Any(card => card.Source == VadGateController.ActiveSource.Loopback)
            && (!requireVisibleCards || visible >= 3), $"默认窗口可见来源卡不足三条：{visible}。");
        log($"PASS conversation: Mic/我 + Loopback/对方, 2 history + 1 realtime, font=16, visibleCards={visible}");
    }

    private static SelectableTextBlock[] ConversationText(InkStudyTranslationView view, InkTranslationCard card)
        => Find<ScrollViewer>(view, "InkConversationScroll").GetVisualDescendants().OfType<SelectableTextBlock>()
            .Where(text => ReferenceEquals(text.DataContext, card)).ToArray();

    private static async Task SelectThemeAsync(MainWindow window, Button button, string header, CancellationToken token)
    {
        CheckHit(window, button);
        RaiseClick(button);
        await PumpAsync(window, token);
        var menu = button.Flyout as MenuFlyout ?? throw new InvalidOperationException("主窗没有为主题请求挂 MenuFlyout。");
        try
        {
            Check(menu.IsOpen, "真实主题菜单没有打开。");
            var item = menu.Items.OfType<MenuItem>().Single(value => value.Header?.ToString()?.Contains(header, StringComparison.Ordinal) == true);
            Check(item.IsEffectivelyVisible && item.IsEffectivelyEnabled && item.GetVisualRoot() != null, "主题选项没有呈现或不可用。");
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally { menu.Hide(); }
        await PumpAsync(window, token);
    }

    private static void CheckInkActive(MainWindow window, InkStudyTranslationView view)
        => Check(window.CurrentShellAppearance == ShellAppearancePreference.InkStudy && window.IsInkStudyAppearance
            && !window.IsSongStudyAppearance && view.IsEffectivelyVisible
            && Find<Panel>(window, "InkStudyHost").IsEffectivelyVisible
            && window.ActualThemeVariant == ThemeVariant.Light && view.ActualThemeVariant == ThemeVariant.Light,
            "窗口/新宿主/独立页未进入局部 Light InkStudy。");

    private static void CheckVisible(MainWindow window, Control control)
    {
        var bounds = BoundsIn(control, window);
        Check(control.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0
            && Contains(new Rect(window.ClientSize), bounds), $"{control.Name ?? control.GetType().Name} 不在客户区可见范围：{bounds}。");
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ReferenceEquals(ancestor, window)) break;
            if (ancestor.ClipToBounds)
                Check(Contains(BoundsIn(ancestor, window), bounds), $"{control.Name} 被祖先裁切。");
        }
    }

    private static void CheckHit(MainWindow window, Button button)
    {
        CheckVisible(window, button);
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        var hit = window.InputHitTest(center) as Visual;
        Check(button.IsEffectivelyEnabled && (ReferenceEquals(hit, button) || hit?.GetVisualAncestors().Contains(button) == true),
            $"{button.Name} 的主动作/主题入口被遮挡或不可用。");
    }

    private static void RaiseClick(Button button)
    {
        Check(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "拒绝激活不可见/禁用控件。");
        (typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(nameof(Button), "OnClick")).Invoke(button, null);
    }

    private static async Task PumpAsync(MainWindow window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var composition = ElementComposition.GetElementVisual(window)
            ?? throw new InvalidOperationException("测试窗尚未连接合成器。");
        var batch = composition.Compositor.RequestCompositionBatchCommitAsync();
        await Dispatcher.UIThread.AwaitWithPriority(batch.Rendered.WaitAsync(token), DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
    }

    private static async Task WaitForLiveAsync(MainWindow window, Panel host, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!host.Children.OfType<LiveTranslationView>().Any())
        {
            Check(watch.Elapsed < TimeSpan.FromSeconds(3), "OnOpened 没有创建生产实时页。");
            await PumpAsync(window, token);
        }
        await PumpAsync(window, token);
    }

    private static void SaveWindow(MainWindow window, string path, CancellationToken token, Action<string> log)
    {
        token.ThrowIfCancellationRequested();
        var scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
        log($"PASS production window screenshot={Path.GetFileName(path)}, client={window.ClientSize}, pixels={bitmap.PixelSize}; visual comparison pending main agent");
        token.ThrowIfCancellationRequested();
    }

    private static Dictionary<string, Color> ReadPalette(Control scope) => PaletteKeys.ToDictionary(key => key, key =>
    {
        Check(scope.TryFindResource(key, scope.ActualThemeVariant, out var value), $"缺少资源 {key}。");
        return ColorOf(value as IBrush);
    });
    private static void CheckPalette(Control scope, IReadOnlyDictionary<string, Color> expected)
    {
        var actual = ReadPalette(scope);
        foreach (var (key, color) in expected) Check(actual[key] == color, $"旧页面/窗口色板未恢复：{key}。");
    }
    private static Color ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color
        : throw new InvalidOperationException("预期实色画刷。");
    private static T Find<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"缺少生产控件 {name}。");
    private static Rect BoundsIn(Visual visual, Visual ancestor) => new(visual.TranslatePoint(default, ancestor)
        ?? throw new InvalidOperationException("控件不在同一视觉树。"), visual.Bounds.Size);
    private static bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 1 && inner.Top >= outer.Top - 1
        && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;
    private static bool Overlaps(Rect left, Rect right) => Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left) > 1
        && Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top) > 1;
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TrueFluentPro.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到仓库根，不能限定本次产物路径。");
    }
}

// 必须公开：生产 XAML 使用反射绑定。只保存内存数据，绝不构造真实业务 VM/服务。
public abstract class InkStudyFakeState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class InkStudyFakeCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public int ExecutionCount { get; private set; }
    public object? LastParameter { get; private set; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { ExecutionCount++; LastParameter = parameter; }
}

public sealed class InkStudyFakeSession : InkStudyFakeState, ILiveTranslationPresentationSource, IDisposable
{
    public InkStudyFakeSession() => ResetSample();
    public string CurrentOriginal => LatestRealtimeFrame?.OriginalText ?? "";
    public string CurrentTranslated => LatestRealtimeFrame?.TranslatedText ?? "";
    public LiveTranslationFrame? LatestRealtimeFrame { get; private set; }
    public ObservableCollection<TranslationItem> History { get; } = new();
    public InkStudyFakeConfig ConfigVM { get; } = new();
    public InkStudyFakeAudioDevices AudioDevices { get; } = new();
    public InkStudyFakeInsight AiInsight { get; } = new();
    public InkStudyFakeCommand ToggleTranslationCommand { get; } = new();
    public InkStudyFakeCommand ClearHistoryCommand { get; } = new();
    public InkStudyFakeCommand OpenHistoryFolderCommand { get; } = new();
    public InkStudyFakeCommand ShowFloatingSubtitlesCommand { get; } = new();
    public InkStudyFakeCommand ShowFloatingMicSubtitleCommand { get; } = new();
    public InkStudyFakeCommand ShowFloatingLoopbackSubtitleCommand { get; } = new();
    public InkStudyFakeCommand RestoreFloatingSubtitleInteractionCommand { get; } = new();
    public InkStudyFakeCommand ShowFloatingInsightCommand { get; } = new();
    public bool IsAnySubtitleOpen => false;
    public bool IsFloatingInsightOpen => false;
    public bool IsTranslating => false;
    public string TranslationToggleButtonText => "开始翻译";
    public string StatusMessage => "准备就绪 · 受控呈现";
    public string AudioPipelineStatusText => "未初始化音频管线";
    public string AudioPipelineTooltip => "计数器命令，不访问音频或云端";
    // 模拟旧页状态；新页模式/字号不得回写它们。
    public EditorDisplayMode EditorDisplayMode { get; set; } = EditorDisplayMode.Translated;
    public bool IsOriginalView { get => EditorDisplayMode == EditorDisplayMode.Original; set { if (value) EditorDisplayMode = EditorDisplayMode.Original; } }
    public bool IsTranslatedView { get => EditorDisplayMode == EditorDisplayMode.Translated; set { if (value) EditorDisplayMode = EditorDisplayMode.Translated; } }
    public bool IsBilingualView { get => EditorDisplayMode == EditorDisplayMode.Bilingual; set { if (value) EditorDisplayMode = EditorDisplayMode.Bilingual; } }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int BusinessExecutionCount => StopCount + DisposeCount + new[] { ToggleTranslationCommand, ClearHistoryCommand,
        OpenHistoryFolderCommand, ShowFloatingSubtitlesCommand, ShowFloatingMicSubtitleCommand,
        ShowFloatingLoopbackSubtitleCommand, RestoreFloatingSubtitleInteractionCommand, ShowFloatingInsightCommand,
        AudioDevices.RefreshAudioDevicesCommand, AiInsight.SendInsightCommand, AiInsight.StopInsightCommand,
        AiInsight.ClearInsightCommand, AiInsight.SendPresetInsightCommand, AiInsight.ToggleAutoInsightCommand }
        .Sum(command => command.ExecutionCount);

    public void Publish(LiveTranslationFrame frame)
    {
        LatestRealtimeFrame = frame;
        Notify(nameof(LatestRealtimeFrame));
        Notify(nameof(CurrentOriginal));
        Notify(nameof(CurrentTranslated));
    }
    public void ResetSample()
    {
        History.Clear();
        var start = new DateTime(2026, 11, 4, 9, 0, 0);
        History.Add(new TranslationItem { Timestamp = start, OriginalText = "Good morning, everyone.",
            TranslatedText = "大家早上好。", Source = VadGateController.ActiveSource.Mic, SubtitleSegmentKey = "ink-mic-history" });
        History.Add(new TranslationItem { Timestamp = start.AddSeconds(10), OriginalText = "Good morning. Welcome!",
            TranslatedText = "早上好，欢迎大家！", Source = VadGateController.ActiveSource.Loopback, SubtitleSegmentKey = "ink-loopback-history" });
        Publish(new LiveTranslationFrame("Let us begin.", "让我们开始吧。", start.AddSeconds(20),
            VadGateController.ActiveSource.Mic, false, "ink-live"));
    }
    public void Stop() => StopCount++;
    public void Dispose() => DisposeCount++;
    public void CheckLegacyState()
    {
        if (EditorDisplayMode != EditorDisplayMode.Translated || ConfigVM.Config.DefaultFontSize != 38)
            throw new InvalidOperationException("新呈现修改了 fake 的旧显示模式或字号。");
    }
}

public sealed class InkStudyFakeLegacyConfig { public int DefaultFontSize { get; set; } = 38; }

public sealed class InkStudyFakeConfig : InkStudyFakeState
{
    public InkStudyFakeLegacyConfig Config { get; } = new();
    public string[] SpeechResourceNames { get; } = new[] { "书斋受控语音资源" };
    public string[] SourceLanguages { get; } = new[] { "自动识别", "英语", "中文", "日语", "韩语", "法语", "德语", "西班牙语" };
    public string[] TargetLanguages { get; } = new[] { "英语", "中文", "日语", "韩语", "法语", "德语", "西班牙语" };
    public int ActiveSpeechResourceIndex { get; set; }
    public int SourceLanguageIndex { get; set; } = 1;
    public int TargetLanguageIndex { get; set; } = 1;
    public bool IsConfigurationEnabled => true;
    public IBrush SubscriptionLampFill => Brushes.SeaGreen;
    public IBrush SubscriptionLampStroke => Brushes.SeaGreen;
    public double SubscriptionLampOpacity => 1;
    public string SubscriptionValidationStatusMessage => "受控展示，不验证订阅";
}

public sealed class InkStudyFakeAudioDevices : InkStudyFakeState
{
    public ObservableCollection<AudioDeviceInfo> AudioDevicesList { get; } = new()
        { new() { DeviceId = "fake-mic", DisplayName = "书斋麦克风（模拟）" } };
    public ObservableCollection<AudioDeviceInfo> OutputDevicesList { get; } = new()
        { new() { DeviceId = "fake-loopback", DisplayName = "系统回环（模拟）" } };
    public InkStudyFakeAudioDevices()
    {
        SelectedAudioDevice = AudioDevicesList[0];
        SelectedOutputDevice = OutputDevicesList[0];
    }
    public AudioDeviceInfo? SelectedAudioDevice { get; set; }
    public AudioDeviceInfo? SelectedOutputDevice { get; set; }
    public bool IsInputDeviceUiEnabled => true;
    public bool IsOutputDeviceUiEnabled => true;
    public bool IsAudioDeviceRefreshEnabled => true;
    public bool IsRecognitionLoopbackOnly { get; set; }
    public bool IsRecognitionLoopbackMix { get; set; } = true;
    public bool IsRecognitionMicOnly { get; set; }
    public bool IsRecordingLoopbackOnly { get; set; }
    public bool IsRecordingLoopbackMix { get; set; } = true;
    public bool IsRecordingMicOnly { get; set; }
    public ObservableCollection<double> AudioLevelHistory { get; } = new(Enumerable.Repeat(0d, 24));
    public InkStudyFakeCommand RefreshAudioDevicesCommand { get; } = new();
}

public sealed class InkStudyFakeInsight : InkStudyFakeState
{
    public string InsightMarkdown { get; set; } = """
        ## 今日交流 · 会意

        双方已经完成问候，接下来将围绕今天的议题展开讨论。**我方**先发起交流，**对方**积极回应，语气自然友好。

        ### 对话脉络

        - 我方以“大家早上好”开启会谈，表达了愿意交流的态度。
        - 对方回应问候并欢迎大家，建立了共同讨论的氛围。
        - 当前正在确认进入正文，适合先说明议程，再逐项交流。

        ### 后续关注

        请先核对双方使用的语言，注意保留专有名词的原文。遇到尚未说完的句子，可等待定稿再归纳，不把实时修订误认为新的结论。

        > 听其言，也观其意；以原文为据，以译文相通。

        讨论结束时，建议整理共同确认的事项、尚存分歧与后续安排。以上内容是本次受控界面样例，未调用任何模型或自动分析服务。
        """;
    public bool IsAiConfigured => true;
    public bool IsInsightEmpty => string.IsNullOrEmpty(InsightMarkdown);
    public bool IsInsightLoading => false;
    public InsightPresetButton[] InsightPresetButtons { get; } = new InsightPresetButton[]
        { new() { Name = "会谈摘要", Prompt = "概括本次讨论内容" }, new() { Name = "后续安排", Prompt = "整理双方确认的安排" } };
    public string InsightUserInput { get; set; } = "请总结双方的主要观点。";
    public bool IsAutoInsightEnabled => false;
    public int AutoInsightModeIndex { get; set; }
    public int AutoInsightIntervalSeconds { get; set; } = 30;
    public string AutoInsightPrompt { get; set; } = "总结当前对话，保留待确认事项。";
    public string AutoInsightToggleText => "启动自动洞察";
    public InkStudyFakeCommand SendInsightCommand { get; } = new();
    public InkStudyFakeCommand StopInsightCommand { get; } = new();
    public InkStudyFakeCommand ClearInsightCommand { get; } = new();
    public InkStudyFakeCommand SendPresetInsightCommand { get; } = new();
    public InkStudyFakeCommand ToggleAutoInsightCommand { get; } = new();
}