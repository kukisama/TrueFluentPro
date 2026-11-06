using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro;
using TrueFluentPro.Controls;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;
using TrueFluentPro.ViewModels.Settings;
using TrueFluentPro.Views;
using TrueFluentPro.Views.Settings;
using FaIcon = Projektanker.Icons.Avalonia.Icon;

internal static class SongScrollPolishRegression
{
    private static readonly (string Tag, Type Type)[] Pages =
    {
        ("mediav2", typeof(SongScrollMediaCenterV2View)), ("media", typeof(SongScrollMediaStudioView)),
        ("audiolab", typeof(SongScrollAudioLabView)), ("taskmonitor", typeof(SongScrollTaskQueueMonitorView)),
        ("settings", typeof(SongScrollSettingsView))
    };

    internal static async Task VerifyAsync()
    {
        Require(OperatingSystem.IsWindows(), "platform", "Windows Avalonia window", Environment.OSVersion);
        var repo = Directory.GetCurrentDirectory();
        Require(File.Exists(System.IO.Path.Combine(repo, "TrueFluentPro.csproj")), "cwd", "repository root", repo);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var root = System.IO.Path.Combine(repo, "artifacts", "song-scroll-polish",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var app = Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        using var fixture = new SongScrollPolishFixture();
        MainWindow? window = null;
        var group = "setup";
        var passed = 0;
        void Pass(string text) => Console.WriteLine($"PASS {++passed}/7 {group}: {text}");
        Console.WriteLine("ARTIFACTS=" + root);
        Console.WriteLine("ASSEMBLY=" + typeof(MainWindow).Assembly.Location);
        Console.WriteLine("SCOPE=RegressionApplication themes + MainWindow without MainVM; five production views; public in-memory fixtures; no BindViewModel/Initialize, services, user config/DB, audio load, cloud or process control");
        Console.WriteLine("INPUT=Flyout.ShowAt / ComboBox.IsDropDownOpen / routed canvas pointer only; NOT physical click/drag. No foreground/cursor precondition is silently skipped.");
        Console.WriteLine("NOT_COVERED=real business/service integration, search execution/provider menu, configuration save, endpoint credentials, audio playback/decoding, physical input and real multi-monitor/high-DPI. Technical-input probe is synthetic in the real settings host, not the credentials section.");
        Console.WriteLine("VISUAL_REVIEW=PNG is RenderTargetBitmap of the actual laid-out window; saving is NOT a visual pass. 主代理须人工检查书缘旧纸签残影、折口及纹理接缝。");
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Dark; // 默认紫色不能借全局浅色主题隐藏。
            var appearance = System.IO.Path.Combine(root, "never-saved-appearance.json");
            window = new MainWindow(new ShellAppearancePreferencesService(appearance))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Require(window.DataContext == null, "MainWindow.DataContext", null, window.DataContext);
            var screen = window.Screens.All.FirstOrDefault(s => s.WorkingArea.Width / s.Scaling >= window.MinWidth + 32
                && s.WorkingArea.Height / s.Scaling >= window.MinHeight + 180)
                ?? throw Failure("screen", "work area accommodates normal and short production window", window.Screens.All.Count);
            window.Width = Math.Min(1256, screen.WorkingArea.Width / screen.Scaling - 32);
            window.Height = Math.Min(831, screen.WorkingArea.Height / screen.Scaling - 48);
            window.Position = new PixelPoint(screen.WorkingArea.X + 12, screen.WorkingArea.Y + 12);
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            window.Show();
            await Pump(window, token); // 收齐 MainWindow.OnOpened 默认路由，之后不再被覆盖。
            Console.WriteLine($"SCREEN={screen.Bounds};work={screen.WorkingArea};scaling={window.RenderScaling}");

            var pages = new Dictionary<string, Control>();
            TemplateProbe? modelParts = null;
            try
            {
                foreach (var spec in Pages)
                {
                    var page = Navigate(window, spec.Tag);
                    Require(page.GetType() == spec.Type, "route:" + spec.Tag, spec.Type.Name, page.GetType().Name);
                    page.DataContext = fixture;
                    if (spec.Tag == "mediav2") modelParts = new TemplateProbe(Find<ComboBox>(page, "ScrollMediaImageModels"));
                    await Pump(window, token);
                    Require(page.IsEffectivelyVisible && window.DataContext == null, "safe visible route:" + spec.Tag,
                        "visible page / null MainVM", $"visible={page.IsEffectivelyVisible};context={window.DataContext}");
                    pages.Add(spec.Tag, page);
                }

                group = "1-size-popup";
                var media = (SongScrollMediaCenterV2View)Navigate(window, "mediav2");
                await Pump(window, token);
                await VerifySizePopup(window, media, fixture, token);
                Pass("real expanded Popup, canvas 240×240 screen-DIP, complete 1968×1408 + narrow-ratio text/info; routed pointer snaps and writes back to the same fixture");

                group = "2-model-prefix";
                await VerifyModelPrefix(window, media, fixture, modelParts!, token);
                Pass("two actual same-name ModelOption instances: selected display and both expanded dropdown rows retain distinct endpoint prefixes");

                group = "3-studio-popups-and-toggle";
                var studio = (SongScrollMediaStudioView)Navigate(window, "media");
                await Pump(window, token);
                await VerifyStudioPopups(window, studio, fixture.CurrentSession, token);
                Pass("model/more/ratio actually expanded, bounded screen-DIP, 16/14 font hierarchy, teal selected item + real checked ToggleButton template background");

                group = "4-search-notification";
                await VerifySearch(window, studio, fixture.CurrentSession, token);
                Pass("INotifyPropertyChanged false→true→false drives globe, visible label/dot, enabled class and actual painted border/background; no search call");

                group = "5-settings";
                var settings = (SongScrollSettingsView)Navigate(window, "settings");
                await Pump(window, token);
                await VerifySettings(window, settings, token);
                Pass("16 directory entries; no extra 配置目录 row; original TextSection + real fresh TextSectionVM, Kai role hierarchy, checked paint, monospace exception; viewing custom 37 remains unchanged");

                group = "6-audio-player";
                var audio = (SongScrollAudioLabView)Navigate(window, "audiolab");
                await Pump(window, token);
                await VerifyPlayer(window, audio, fixture.Playback, token);
                Pass("measured one-row player; no time-input/jump/stop UI; actual Playback bindings, progress and speed notification, old public commands/SeekToTime retained; no audio loaded");

                group = "7-book-and-top-coordinate-guard";
                foreach (var spec in Pages)
                {
                    Navigate(window, spec.Tag);
                    await Pump(window, token);
                    CheckTop(((ISongScrollPage)pages[spec.Tag]).Shell, "ShellWindowButtons", "ShellRightMotto", "ShellScene");
                }
                media = (SongScrollMediaCenterV2View)Navigate(window, "mediav2");
                await Pump(window, token);
                var normalWidth = window.Width;
                var normalHeight = window.Height;
                foreach (var state in new[] { "normal", "maximized", "short" })
                {
                    window.WindowState = state == "maximized" ? WindowState.Maximized : WindowState.Normal;
                    await UntilState(window, state == "maximized" ? WindowState.Maximized : WindowState.Normal,
                        state == "maximized" ? new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling)
                            : new Size(normalWidth, normalHeight), token);
                    if (state == "short")
                    {
                        window.Width = normalWidth;
                        window.Height = window.MinHeight;
                        await UntilState(window, WindowState.Normal, new Size(normalWidth, window.MinHeight), token);
                    }
                    media.Shell.SetLeftRatio(state == "normal" ? .34 : state == "maximized" ? .50 : .25);
                    await Pump(window, token);
                    Require(state != "short" || window.ClientSize.Height < normalHeight - 80,
                        "short client", $"height < {normalHeight - 80}", window.ClientSize);
                    var surface = Find<SongScrollBookSurface>(media.Shell, "ShellBookSurface");
                    var scene = Find<Canvas>(media.Shell, "ShellScene");
                    Require(surface.IsEffectivelyVisible && surface.Bounds.Height > 300, "surface:" + state,
                        "visible, positive rendered height", surface.Bounds);
                    Near(surface.SceneHeight, scene.Bounds.Height, "surface follows scene:" + state);
                    Near(surface.SpinePosition, 343 + 2178 * media.Shell.LeftRatio, "surface spine:" + state);
                    CheckTop(media.Shell, "ShellWindowButtons", "ShellRightMotto", "ShellScene");
                    Capture(window, root, "book-" + state);
                }
                window.Width = normalWidth;
                window.Height = normalHeight;
                var live = Navigate(window, "live"); // null source: 不绑定 ILiveTranslationPresentationSource。
                await Pump(window, token);
                Require(live is SongScrollTranslationView && live.DataContext == null, "live comparison",
                    "SongScrollTranslationView / null source", $"{live.GetType().Name}/{live.DataContext}");
                CheckTop(live, "ScrollWindowButtons", "ScrollRightMotto", "ScrollScene");
                Pass("three real rendered window PNGs normal/maximized/short (.34/.50/.25); five shells + live preserve Canvas.Top 158/172 and measured scene coordinates; manual ghosting review pending");
                Require(!File.Exists(appearance), "appearance isolation", "no saved preference file", File.Exists(appearance));
                Require(passed == 7, "group count", 7, passed);
            }
            finally { modelParts?.Dispose(); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL group={group};passed={passed}/7;{ex}");
            throw;
        }
        finally
        {
            try
            {
                window?.Close();
                Require(window == null || !window.IsVisible, "cleanup", "fixture window closed", window?.IsVisible);
            }
            finally { app.RequestedThemeVariant = originalTheme; }
        }
        Console.WriteLine("SONG_SCROLL_POLISH_EXIT=0;groups=7;VISUAL_REVIEW=PENDING");
    }

    private static async Task VerifySizePopup(MainWindow window, Control media, SongScrollPolishFixture fixture, CancellationToken token)
    {
        var button = Find<Button>(media, "ScrollMediaSizeButton");
        var flyout = button.Flyout as SongScrollPopupFlyout
            ?? throw Failure("size flyout type", nameof(SongScrollPopupFlyout), button.Flyout?.GetType().Name);
        var selector = Find<ImageSizeCanvasSelector>((Control)flyout.Content!, "ScrollMediaSizeSelector");
        using var parts = new TemplateProbe(selector);
        var presenter = await Open(window, button, token);
        try
        {
            PopupBounds(presenter, 400, 600, "size popup");
            var canvas = parts.Get<Panel>("PART_CanvasArea");
            var rect = parts.Get<Border>("PART_SizeRect");
            _ = parts.Get<Border>("PART_DragHandle");
            var dimensions = parts.Get<TextBlock>("PART_DimensionLabel");
            var info = parts.Get<TextBlock>("PART_InfoText");
            foreach (var size in new[] { "1968x1408", "512x1536" })
            {
                fixture.WorkspaceSession.ImageSize = size;
                await Pump(Root(presenter), token);
                Require(selector.SelectedSize == size && ReferenceEquals(selector.DataContext, fixture), "size fixture→control",
                    size + " / same root context", selector.SelectedSize + " / " + selector.DataContext?.GetType().Name);
                Near(canvas.Bounds.Width, 240, "canvas local width");
                Near(canvas.Bounds.Height, 240, "canvas local height");
                Near(ScreenRect(canvas).Width, 240, "canvas screen-DIP width", 1.5);
                Near(ScreenRect(canvas).Height, 240, "canvas screen-DIP height", 1.5);
                Near(info.Bounds.Width, 264, "info layout width");
                Near(ScreenRect(info).Width, 264, "info screen-DIP width", 1.5);
                Require(dimensions.Text == size.Replace("x", " × "), "full dimensions", size.Replace("x", " × "), dimensions.Text);
                Require(info.Text == selector.SizeInfoText && !string.IsNullOrWhiteSpace(info.Text), "info binding", selector.SizeInfoText, info.Text);
                CompleteText(dimensions, presenter);
                CompleteText(info, presenter);
                Require(!dimensions.GetVisualAncestors().Contains(rect), "dimension parent", "outside narrow PART_SizeRect", dimensions.Parent?.GetType().Name);
                Near(dimensions.FontSize, 16, "dimension font");
                Near(info.FontSize, 14, "info font");
                PopupBounds(presenter, 400, 600, size);
            }

            // 真实控件路由，不调用私有换算函数；不声称物理鼠标。原始 1229×1547 应 snap 为 1232×1552。
            var top = Root(canvas);
            var point = canvas.TranslatePoint(new Point(1229d / 16, 1547d / 16), top)
                ?? throw Failure("canvas transform", "root coordinate", null);
            using var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
            canvas.RaiseEvent(new PointerPressedEventArgs(canvas, pointer, top, point, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, pointer, top, point, 1,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await Pump(top, token);
            Require(selector.SelectedSize == "1232x1552" && fixture.WorkspaceSession.ImageSize == "1232x1552",
                "routed pointer→16px grid→TwoWay", "control=fixture=1232x1552", $"control={selector.SelectedSize};fixture={fixture.WorkspaceSession.ImageSize}");
            Require(ImageSizeCalculator.TryParse(selector.SelectedSize, out var w, out var h)
                && w % 16 == 0 && h % 16 == 0 && ImageSizeCalculator.Validate(w, h) == null,
                "grid validity", "legal multiples of 16", selector.SelectedSize);
            fixture.WorkspaceSession.ImageSize = "1968x1408";
            await Pump(top, token);
            Require(selector.SelectedSize == "1968x1408", "same-control reverse update", "1968x1408", selector.SelectedSize);
            CompleteText(dimensions, presenter);
        }
        finally { flyout.Hide(); await Pump(window, token); }
    }

    private static async Task VerifyModelPrefix(MainWindow window, Control page, SongScrollPolishFixture fixture,
        TemplateProbe parts, CancellationToken token)
    {
        var combo = Find<ComboBox>(page, "ScrollMediaImageModels");
        Require(fixture.AvailableImageModels[0].ModelDisplayName == fixture.AvailableImageModels[1].ModelDisplayName
            && fixture.AvailableImageModels[0].EndpointName != fixture.AvailableImageModels[1].EndpointName,
            "model fixture", "same model name / different endpoint", string.Join(";", fixture.AvailableImageModels.Select(m => m.ToString())));
        foreach (var model in fixture.AvailableImageModels)
        {
            fixture.SelectedImageModel = model;
            await Pump(window, token);
            Require(ReferenceEquals(combo.SelectedItem, model), "selected model binding", model, combo.SelectedItem);
            var label = Visuals(combo).OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == model.ToString())
                ?? throw Failure("selected prefix text", model.ToString(), string.Join("|", Visuals(combo).OfType<TextBlock>().Select(t => t.Text)));
            CompleteText(label, window);
        }
        var popup = parts.Get<Popup>("PART_Popup");
        combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
        try
        {
            await Pump(window, token);
            Require(combo.IsDropDownOpen && popup.IsOpen && popup.Child != null, "model dropdown", "actual expanded Popup", popup.IsOpen);
            await Pump(Root(popup.Child!), token);
            foreach (var model in fixture.AvailableImageModels)
            {
                var item = combo.ContainerFromItem(model) as ComboBoxItem
                    ?? throw Failure("realized model row", model.ToString(), null);
                var label = One(Visuals(item).OfType<TextBlock>().Where(t => t.Text == model.ToString()), "dropdown prefix:" + model);
                CompleteText(label, popup.Child!);
            }
            combo.SetCurrentValue(ComboBox.SelectedItemProperty, fixture.AvailableImageModels[0]);
            await Pump(window, token);
            Require(ReferenceEquals(fixture.SelectedImageModel, fixture.AvailableImageModels[0]), "model control→fixture",
                fixture.AvailableImageModels[0], fixture.SelectedImageModel);
        }
        finally { combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false); await Pump(window, token); }
    }

    private static async Task VerifyStudioPopups(MainWindow window, Control studio, SongScrollPolishSessionFixture session, CancellationToken token)
    {
        Require(ReferenceEquals(Find<CachedContentControl>(studio, "SessionContentHost").Content, session),
            "studio content fixture", "same public session fixture", Find<CachedContentControl>(studio, "SessionContentHost").Content);
        var buttons = Visuals(studio).OfType<Button>().Where(b => b.IsEffectivelyVisible
            && b.Flyout is SongScrollPopupFlyout).ToArray();
        Require(buttons.Length == 3, "studio flyout count", "model/more/ratio = 3", buttons.Length);
        foreach (var button in buttons)
        {
            var flyout = (SongScrollPopupFlyout)button.Flyout!;
            var presenter = await Open(window, button, token);
            try
            {
                PopupBounds(presenter, 520, 560, "studio:" + ((Control)flyout.Content!).Name);
                var labels = Visuals(presenter).OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToArray();
                Require(labels.Length > 0, "popup text realized", ">0", labels.Length);
                foreach (var label in labels)
                {
                    var expected = label.Classes.Contains("scroll-popup-muted") ? 14 : 16;
                    Near(label.FontSize, expected, "popup font:" + label.Text);
                    Require(IsKai(label.FontFamily), "popup font family", "Kai", label.FontFamily);
                    Near(ScreenRect(label).Height, label.Bounds.Height, "popup text screen-DIP height", 1.5);
                    CompleteText(label, presenter);
                }
                if (flyout.Content is ListBox list)
                {
                    Require(list.SelectedIndex >= 0, "popup selection", ">=0", list.SelectedIndex);
                    var selected = list.ContainerFromIndex(list.SelectedIndex) as ListBoxItem
                        ?? throw Failure("selected popup container", "realized ListBoxItem", null);
                    Require(selected.IsSelected, "selected state", true, selected.IsSelected);
                    var paint = TemplateChild<ContentPresenter>(selected, "PART_ContentPresenter");
                    TealSelection(paint.Background, "selected popup actual paint");
                    Near(paint.FontSize, 16, "selected item inherited font");
                }
            }
            finally { flyout.Hide(); await Pump(window, token); }
        }
        var toggle = Find<ToggleButton>(studio, "ScrollStudioReasoningToggle");
        var surface = TemplateChild<ContentPresenter>(toggle, "PART_ContentPresenter");
        session.EnableReasoning = false;
        await Pump(window, token);
        var off = BrushColor(surface.Background);
        session.EnableReasoning = true;
        await Pump(window, token);
        Require(toggle.IsChecked == true, "reasoning binding", true, toggle.IsChecked);
        TealSelection(surface.Background, "real checked toggle template background");
        Require(BrushColor(surface.Background) != off, "toggle paint changes", "checked != unchecked", surface.Background);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        await Pump(window, token);
        Require(!session.EnableReasoning && toggle.IsChecked == false, "toggle TwoWay", "fixture=false / IsChecked=false", session.EnableReasoning);
    }

    private static async Task VerifySearch(MainWindow window, Control studio, SongScrollPolishSessionFixture session, CancellationToken token)
    {
        var button = Find<Button>(studio, "ScrollStudioWebSearchButton");
        var dot = Find<Ellipse>(studio, "ScrollStudioWebSearchEnabledDot");
        // ScrollButton 模板中 PART_ContentPresenter 是透明的，真正绘制边框/底色的是 Surface。
        var paint = TemplateChild<Border>(button, "Surface");
        var icon = One(Visuals(button).OfType<FaIcon>(), "search globe");
        var off = default(Color);
        foreach (var enabled in new[] { false, true, false })
        {
            session.EnableWebSearch = enabled; // 不直接添加/移除 enabled class。
            await Pump(window, token);
            Require(button.Classes.Contains("enabled") == enabled && dot.IsEffectivelyVisible == enabled,
                "search notification state", $"class/dot={enabled}", $"class={button.Classes.Contains("enabled")};dot={dot.IsEffectivelyVisible}");
            Require(icon.IsEffectivelyVisible && icon.Bounds.Width > 0 && icon.Value == "fa-solid fa-globe", "search icon",
                "visible globe (not link-chain)", $"{icon.Value}/{icon.Bounds}");
            var labels = Visuals(button).OfType<TextBlock>().Where(t => t.IsEffectivelyVisible
                && (t.Text?.StartsWith("联网搜索", StringComparison.Ordinal) ?? false)).ToArray();
            var expected = enabled ? "联网搜索 · 已开" : "联网搜索";
            Require(labels.Length == 1 && labels[0].Text == expected, "search visible label", expected, string.Join("|", labels.Select(t => t.Text)));
            CompleteText(labels[0], window);
            Require(paint.BorderThickness.Left > 0 && paint.Bounds.Width > 0, "painted search border", "visible nonzero template border", paint.BorderThickness);
            Require(BrushColor(paint.BorderBrush) == Color.Parse(enabled ? "#4D7979" : "#CBBDA4"), "search border color",
                enabled ? "#4D7979" : "#CBBDA4", paint.BorderBrush);
            var color = BrushColor(paint.Background);
            if (enabled)
            {
                TealSelection(paint.Background, "search enabled paint");
                Require(color != off, "search background transition", "on != off", color);
                Require(BrushColor(dot.Fill) == Color.Parse("#4D7979"), "enabled dot color", "#4D7979", dot.Fill);
            }
            else
            {
                var expectedOff = button.IsPointerOver ? "#E8E9DB" : "#55FFFCF5";
                Require(color == Color.Parse(expectedOff), "search disabled/reset paint", expectedOff, color);
                off = color;
            }
        }
        Require(session.SearchNotificationCount == 2, "search notification receipts", 2, session.SearchNotificationCount);
    }

    private static async Task VerifySettings(MainWindow window, SongScrollSettingsView settings, CancellationToken token)
    {
        var navigation = Find<ListBox>(settings, "ScrollSectionNavigation");
        var entries = navigation.Items.OfType<ListBoxItem>().ToArray();
        Require(entries.Length == 16 && entries.Select(i => i.Tag).Distinct().Count() == 16,
            "settings directory", "16 unique entries", entries.Length);
        Require(!Visuals(settings).OfType<TextBlock>().Any(t => (t.Text ?? "").Contains("配置目录", StringComparison.Ordinal)),
            "extra location row", "no 配置目录 text", string.Join("|", Visuals(settings).OfType<TextBlock>().Select(t => t.Text).Where(t => t?.Contains("配置目录") == true)));
        var vm = new TextSectionVM { DefaultFontSize = 37, ExportSrt = false };
        var changes = 0;
        vm.Changed += () => changes++; // 没有 SettingsVM 或 ConfigSaved/Save 订阅。
        var text = new TextSection { DataContext = vm };
        var host = Find<ContentControl>(settings, "ScrollSelectedSectionHost");
        navigation.SelectedItem = One(entries.Where(i => Equals(i.Tag, "Section_Text")), "text directory entry");
        // ShowSection 有 MainVM 闸门；安全调用真实表现准备钩子，再将原子表单置入真实宿主。
        Invoke(settings, "PrepareSection", "Section_Text", text);
        host.Content = text;
        Invoke(settings, "OnSectionShown", "Section_Text");
        await Pump(window, token);
        var sizes = Find<ComboBox>(text, "DefaultFontSizeComboBox");
        Require(vm.DefaultFontSize == 37 && sizes.SelectedItem is ComboBoxItem { Tag: "37" } && changes == 0,
            "view custom font without mutation", "VM=37 / selected=37 / Changed=0", $"VM={vm.DefaultFontSize};selected={(sizes.SelectedItem as ComboBoxItem)?.Tag};Changed={changes}");
        foreach (var (value, role, size) in new[]
        { ("字幕与文本", "title", 17d), ("字幕输出", "heading", 15d), ("默认字号", "body", 14d), ("字幕与录音同目录保存，可同时勾选", "caption", 13d) })
        {
            var label = One(Visuals(text).OfType<TextBlock>().Where(t => t.Text == value), "settings label:" + value);
            Require(label.Classes.Contains("scroll-form-" + role), "settings role:" + value, role, string.Join(",", label.Classes));
            Near(label.FontSize, size, "settings font:" + value);
            Require(IsKai(label.FontFamily), "settings Kai:" + value, "Kai", label.FontFamily);
        }
        var check = One(Visuals(text).OfType<CheckBox>().Where(c => Equals(c.Content, "生成 SRT 字幕")), "SRT checkbox");
        vm.ExportSrt = true;
        await Pump(window, token);
        Require(check.IsChecked == true, "settings checked binding", true, check.IsChecked);
        Require(BrushColor(TemplateChild<Border>(check, "NormalRectangle").Background) == Color.Parse("#4D7979"),
            "settings actual checked fill", "#4D7979", TemplateChild<Border>(check, "NormalRectangle").Background);
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        await Pump(window, token);
        Require(!vm.ExportSrt, "settings checkbox TwoWay", false, vm.ExportSrt);

        // TextSection 无技术输入；只增加明确标识的无业务 TextBox 探针，不加载凭据分区。
        var technical = new TextBox { Name = "ModelIdTextBox", Text = "gpt-image-2", Watermark = "https://example.invalid/" };
        var wrapper = new StackPanel();
        host.Content = null;
        wrapper.Children.Add(text);
        wrapper.Children.Add(technical);
        host.Content = wrapper;
        await Pump(window, token);
        Require(technical.Classes.Contains("scroll-form-code")
            && technical.FontFamily.ToString().Contains("Mono", StringComparison.OrdinalIgnoreCase),
            "technical exception", "scroll-form-code / Mono", $"{string.Join(",", technical.Classes)}/{technical.FontFamily}");
        Near(technical.FontSize, 14, "technical input font size");
        Require(vm.DefaultFontSize == 37 && sizes.SelectedItem is ComboBoxItem { Tag: "37" },
            "reattach preserves custom font", "37 / 37", $"{vm.DefaultFontSize}/{(sizes.SelectedItem as ComboBoxItem)?.Tag}");
    }

    private static async Task VerifyPlayer(MainWindow window, Control audio, AudioLabPlaybackViewModel playback, CancellationToken token)
    {
        var player = Find<Border>(audio, "ScrollAudioPlayer");
        var row = Find<Grid>(audio, "ScrollAudioPlayerRow");
        Near(row.Bounds.Height, 48, "player single row design height");
        Near(player.Bounds.Height, 70, "player total design height including padding/border");
        var scale = ScreenRect(row).Width / row.Bounds.Width;
        Near(ScreenRect(row).Height, 48 * scale, "player measured screen-DIP height", 1.5);
        Require(!Visuals(audio).OfType<Control>().Any(c => c.Name == "JumpTimeInput"), "removed time input", "absent", "JumpTimeInput present");
        var buttons = row.Children.OfType<Button>().ToArray(); // 不把 Slider 模板内部按钮算成播放器操作。
        Require(buttons.Length == 4 && !buttons.Any(b => ReferenceEquals(b.Command, playback.StopCommand)
            || (b.Content?.ToString()?.Contains("跳转") ?? false) || (b.Content?.ToString()?.Contains("停止") ?? false)),
            "removed jump/stop buttons", "4 buttons: play,previous,next,speed; no stop/jump", string.Join("|", buttons.Select(b => b.Content?.ToString())));
        foreach (var command in new[] { playback.TogglePlayPauseCommand, playback.PreviousSegmentCommand, playback.NextSegmentCommand })
            Require(buttons.Any(b => ReferenceEquals(b.Command, command)), "Playback command binding", command.GetType().Name, "missing command reference");
        var progress = One(Visuals(player).OfType<Slider>(), "player progress slider");
        Require(BindingOperations.GetBindingExpressionBase(progress, Slider.ValueProperty) != null, "progress binding expression", "present", null);
        playback.Progress = .375; // no reader: SeekToProgress returns without I/O。
        await Pump(window, token);
        Near(progress.Value, .375, "progress VM→slider");
        progress.SetCurrentValue(Slider.ValueProperty, .625);
        await Pump(window, token);
        Near(playback.Progress, .625, "progress slider→VM");
        var speed = One(buttons.Where(b => b.Flyout is MenuFlyout), "speed button");
        playback.PlaybackSpeed = 1.25;
        await Pump(window, token);
        Require(Equals(speed.Content, playback.PlaybackSpeedText), "speed notification", playback.PlaybackSpeedText, speed.Content);
        var speedMenu = (MenuFlyout)speed.Flyout!;
        speedMenu.ShowAt(speed);
        try
        {
            await Pump(window, token);
            var popup = ObservePopup(speedMenu);
            Require(speedMenu.IsOpen && popup.IsOpen && popup.Child is MenuFlyoutPresenter, "speed menu",
                "expanded real MenuFlyoutPresenter", popup.Child?.GetType().Name);
            await Pump(Root(popup.Child!), token);
            Require(speedMenu.Items.OfType<MenuItem>().All(i => ReferenceEquals(i.Command, playback.SetPlaybackSpeedCommand))
                && speedMenu.Items.Count == AudioLabPlaybackViewModel.SpeedOptions.Length,
                "speed command menu", "all original speed commands/options", speedMenu.Items.Count);
        }
        finally { speedMenu.Hide(); await Pump(window, token); }
        Require(playback.PlayCommand != null && playback.PauseCommand != null && playback.StopCommand != null
            && typeof(AudioLabPlaybackViewModel).GetMethod(nameof(AudioLabPlaybackViewModel.SeekToTime), new[] { typeof(TimeSpan) }) != null,
            "legacy VM public entry points", "Play/Pause/Stop + SeekToTime retained", typeof(AudioLabPlaybackViewModel).FullName);
        Require(!playback.IsStopEnabled && !playback.IsPlaying, "no audio loaded", "not ready / not playing", $"ready={playback.IsStopEnabled};playing={playback.IsPlaying}");
        Console.WriteLine($"PLAYER_MEASURED rowDesign={row.Bounds};outerDesign={player.Bounds};screenDIP={ScreenRect(player)};scale={scale}");
    }

    private static async Task<FlyoutPresenter> Open(MainWindow window, Button button, CancellationToken token)
    {
        button.BringIntoView();
        await Pump(window, token);
        Require(button.IsEffectivelyVisible && button.Bounds.Width > 0, "flyout anchor", "visible laid-out button", button.Bounds);
        var flyout = button.Flyout as PopupFlyoutBase ?? throw Failure("flyout", "PopupFlyoutBase", button.Flyout);
        flyout.ShowAt(button); // 不触发业务 Click，不执行命令，不 claim 物理点击。
        try
        {
            await Pump(window, token);
            // Avalonia 11.3.11 PopupFlyoutBase.Popup 是 protected；只读观察真实 Popup，不造替身或未初始化对象。
            var popup = ObservePopup(flyout);
            Require(flyout.IsOpen && popup.IsOpen && popup.Child is FlyoutPresenter, "expanded flyout",
                "IsOpen + actual FlyoutPresenter", $"flyout={flyout.IsOpen};popup={popup.IsOpen};child={popup.Child?.GetType().Name}");
            var presenter = (FlyoutPresenter)popup.Child!;
            await Pump(Root(presenter), token);
            Require(presenter.IsEffectivelyVisible && presenter.Bounds.Width > 0 && presenter.Bounds.Height > 0,
                "realized presenter", "visible positive bounds", presenter.Bounds);
            return presenter;
        }
        catch { flyout.Hide(); throw; }
    }

    private static Popup ObservePopup(PopupFlyoutBase flyout)
        => (Popup?)typeof(PopupFlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(flyout)
            ?? throw Failure("native popup observation", "PopupFlyoutBase.Popup", null);

    private static void PopupBounds(Control presenter, double width, double height, string stage)
    {
        var rect = ScreenRect(presenter);
        Require(rect.Width > 0 && rect.Height > 0 && rect.Width <= width + 1.5 && rect.Height <= height + 1.5,
            stage + " bounds", $"0 < screen-DIP <= {width}×{height}", rect);
        Console.WriteLine($"POPUP_MEASURED {stage};local={presenter.Bounds};screenDIP={rect}");
    }

    private static void CompleteText(TextBlock label, Control boundary)
    {
        var layout = label.TextLayout;
        Require(label.IsEffectivelyVisible && label.Bounds.Width > 0 && label.Bounds.Height > 0
            && label.TextTrimming == TextTrimming.None && layout.TextLines.All(l => !l.HasOverflowed)
            && layout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
            "complete glyph layout:" + label.Text, "visible, uncollapsed, no overflow, layout inside text bounds",
            $"visible={label.IsEffectivelyVisible};bounds={label.Bounds};layout={layout.WidthIncludingTrailingWhitespace}×{layout.Height};lines={layout.TextLines.Count}");
        var rect = ScreenRect(label);
        Within(ScreenRect(boundary), rect, "text inside visible boundary:" + label.Text);
        foreach (var ancestor in label.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor.ClipToBounds) Within(ScreenRect(ancestor), rect, "ancestor clip:" + ancestor.GetType().Name + "/" + label.Text);
            if (ReferenceEquals(ancestor, boundary)) break;
        }
    }

    private static void CheckTop(Control page, string buttonsName, string mottoName, string sceneName)
    {
        var buttons = Find<Grid>(page, buttonsName);
        var motto = Find<TextBlock>(page, mottoName);
        var scene = Find<Canvas>(page, sceneName);
        Near(Canvas.GetTop(buttons), 158, "guard8 buttons Canvas.Top");
        Near(Canvas.GetTop(motto), 172, "guard8 motto Canvas.Top");
        Near(buttons.TranslatePoint(default, scene)!.Value.Y, 158, "guard8 actual buttons scene Y", .6);
        Near(motto.TranslatePoint(default, scene)!.Value.Y, 172, "guard8 actual motto scene Y", .6);
        Require(buttons.IsEffectivelyVisible && motto.IsEffectivelyVisible, "guard8 visible", "buttons + motto", $"{buttons.IsEffectivelyVisible}/{motto.IsEffectivelyVisible}");
    }

    private static void Capture(MainWindow window, string root, string name)
    {
        var path = System.IO.Path.Combine(root, name + ".png");
        Require(!File.Exists(path), "fresh screenshot", "new path", path);
        var scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window); // 只能渲染生产 surface 的实际结果，不读取/重画旧 book-left 源图作证明。
        bitmap.Save(path);
        Console.WriteLine($"PNG_REVIEW_PENDING={path};client={window.ClientSize};pixels={bitmap.PixelSize};state={window.WindowState}");
    }

    private static async Task Pump(TopLevel root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        root.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(root) ?? throw Failure("composition", "real compositor", null);
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync()
            .Rendered.WaitAsync(token), DispatcherPriority.Background);
    }

    private static async Task UntilState(Window window, WindowState state, Size client, CancellationToken token)
    {
        var end = DateTime.UtcNow.AddSeconds(4);
        do
        {
            await Pump(window, token);
            if (window.WindowState == state && Math.Abs(window.ClientSize.Width - client.Width) <= 2
                && Math.Abs(window.ClientSize.Height - client.Height) <= 2) return;
            await Task.Delay(20, token);
        } while (DateTime.UtcNow < end);
        throw Failure("window state + actual client", $"{state}/{client}", $"{window.WindowState}/{window.ClientSize}");
    }

    private static Control Navigate(MainWindow window, string tag)
    {
        Invoke(window, "ShowPage", tag);
        Require(window.DataContext == null, "no MainVM after navigation", null, window.DataContext);
        return (Control)(typeof(MainWindow).GetField("_currentPage", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw Failure("route:" + tag, "current production page", null));
    }

    private static void Invoke(object owner, string method, params object[] args)
        => (owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw Failure("production hook", method, "missing")).Invoke(owner, args);

    // 必须来自真实 TemplateApplied namescope；不把模板定义里的名当作已展开/已布局。
    private sealed class TemplateProbe : IDisposable
    {
        private readonly TemplatedControl _control;
        private INameScope? _scope;
        public TemplateProbe(TemplatedControl control) { _control = control; control.TemplateApplied += Applied; }
        private void Applied(object? sender, TemplateAppliedEventArgs e) => _scope = e.NameScope;
        public T Get<T>(string name) where T : class => _scope?.Find<T>(name)
            ?? throw Failure(_control.GetType().Name + " template part", name, _scope == null ? "TemplateApplied not observed" : "missing part");
        public void Dispose() => _control.TemplateApplied -= Applied;
    }

    private static T TemplateChild<T>(TemplatedControl control, string name) where T : Control
        => Visuals(control).OfType<T>().FirstOrDefault(c => c.Name == name && ReferenceEquals(c.TemplatedParent, control))
            ?? throw Failure(control.GetType().Name + " actual template", name, "missing realized part");
    private static IEnumerable<Visual> Visuals(Visual root) => root.GetVisualDescendants().Prepend(root);
    private static T One<T>(IEnumerable<T> source, string stage)
    {
        var items = source.ToArray();
        Require(items.Length == 1, stage, "exactly one realized item", items.Length);
        return items[0];
    }
    private static T Find<T>(Control root, string name) where T : Control => root.FindNameScope()?.Find<T>(name)
        ?? Visuals(root).OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw Failure(root.GetType().Name + " node", name, "missing");
    private static TopLevel Root(Control control) => TopLevel.GetTopLevel(control)
        ?? throw Failure("visual root:" + control.Name, "attached TopLevel", null);
    private static Rect ScreenRect(Control control)
    {
        var top = Root(control);
        var a = control.PointToScreen(default);
        var b = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        return new Rect(a.X / top.RenderScaling, a.Y / top.RenderScaling,
            (b.X - a.X) / top.RenderScaling, (b.Y - a.Y) / top.RenderScaling);
    }
    private static void Within(Rect outer, Rect inner, string stage)
        => Require(inner.Width > 0 && inner.Height > 0 && inner.Left >= outer.Left - 1.5 && inner.Top >= outer.Top - 1.5
            && inner.Right <= outer.Right + 1.5 && inner.Bottom <= outer.Bottom + 1.5, stage, "inside " + outer, inner);
    private static bool IsKai(FontFamily font) => font.ToString().Contains("Kai", StringComparison.OrdinalIgnoreCase)
        || font.ToString().Contains("楷", StringComparison.Ordinal);
    private static Color BrushColor(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color
        : throw Failure("painted brush", "solid color", brush?.GetType().Name);
    private static void TealSelection(IBrush? brush, string stage)
    {
        var color = BrushColor(brush);
        Require(color == Color.Parse("#DCE7DE") || color == Color.Parse("#CBDDD3"), stage,
            "#DCE7DE or hover #CBDDD3 (not default purple)", color);
    }
    private static void Near(double actual, double expected, string stage, double tolerance = .1)
        => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, stage, expected + " ± " + tolerance, actual);
    private static void Require(bool condition, string stage, object? expected, object? actual)
    { if (!condition) throw Failure(stage, expected, actual); }
    private static InvalidOperationException Failure(string stage, object? expected, object? actual)
        => new($"{stage}: expected=[{expected ?? "<null>"}]; actual=[{actual ?? "<null>"}]");
}

// 仅供反射绑定的公开轻量对象。不构造 MediaSession/MediaCenter/MainWindowViewModel，
// 不调用 Initialize/BindViewModel、不使用 App.Services/PathManager/ConfigurationService。
public abstract class SongScrollPolishObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    protected void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SongScrollPolishFixture : SongScrollPolishObservable, IDisposable
{
    public SongScrollPolishFixture()
    {
        AvailableImageModels = Options("gpt-image-2");
        _selectedImageModel = AvailableImageModels[0];
        CurrentSession = new SongScrollPolishSessionFixture(Options("gpt-4.1"));
        Playback = new AudioLabPlaybackViewModel(() => _segments, _ => { }, () => null);
    }
    public static ModelOption[] Options(string model) => new[] { "夹具甲", "夹具乙" }.Select((endpoint, index) => new ModelOption
    {
        EndpointName = endpoint, ModelDisplayName = model, EndpointType = EndpointApiType.OpenAiCompatible,
        Reference = new ModelReference { EndpointId = "polish-fixture-" + index, ModelId = model }
    }).ToArray();
    private readonly ObservableCollection<TranscriptSegment> _segments = new();
    public AudioLabPlaybackViewModel Playback { get; }
    public AudioLabPlaybackViewModel PodcastPlayback => Playback;
    public SongScrollPolishSessionFixture CurrentSession { get; }
    public SongScrollPolishWorkspaceFixture WorkspaceSession { get; } = new();
    public ModelOption[] AvailableImageModels { get; }
    public ModelOption[] AvailableVideoModels { get; } = Array.Empty<ModelOption>();
    private ModelOption _selectedImageModel;
    public ModelOption SelectedImageModel { get => _selectedImageModel; set => Set(ref _selectedImageModel, value); }
    public bool HasCurrentSession => true;
    public bool IsImageKind { get; set; } = true;
    public bool IsVideoKind { get; set; }
    public bool IsFilePanelOpen => true;
    public string CurrentFilePath => "";
    public string CurrentFileName => "隔离空音频";
    public string BreadcrumbText => "听析中心 · 安全夹具";
    public string StatusMessage => "未加载音频";
    public bool IsSummaryTabVisible => true;
    public AudioLabTabKind SelectedTab => AudioLabTabKind.Summary;
    public object[] AudioFiles { get; } = Array.Empty<object>();
    public object[] CustomVisibleStages { get; } = Array.Empty<object>();
    public object[] Sessions { get; } = Array.Empty<object>();
    public object[] Buckets { get; } = Array.Empty<object>();
    public object[] CurrentBucketTasks { get; } = Array.Empty<object>();
    public object[] WorkspaceTabs { get; } = Array.Empty<object>();
    public void Dispose() => Playback.Dispose();
}

public sealed class SongScrollPolishWorkspaceFixture : SongScrollPolishObservable
{
    private string _imageSize = "1968x1408";
    public string ImageSize { get => _imageSize; set => Set(ref _imageSize, value); }
    public string ImageQuality { get; set; } = "medium";
    public bool IsCanvasSelectorAvailable => true;
    public string[] ImageSizeOptions => new[] { "1968x1408", "512x1536", "1232x1552" };
    public string[] ImageQualityOptions => new[] { "medium" };
    public string[] ImageFormatOptions => new[] { "png" };
    public string ImageFormat { get; set; } = "png";
    public int[] ImageCountOptions => new[] { 1 };
    public int ImageCount { get; set; } = 1;
}

public sealed class SongScrollPolishSessionFixture : SongScrollPolishObservable
{
    public SongScrollPolishSessionFixture(ModelOption[] models) { AvailableTextModels = models; _selectedConversationModel = models[0]; }
    public string SessionName => "安全夹具 · 弹层与搜索";
    public string PromptText { get; set; } = "不发送、不调用服务";
    public ChatInputCursor InputCursor { get; } = new();
    public object[] Messages { get; } = Array.Empty<object>();
    public object[] PendingAttachments { get; } = Array.Empty<object>();
    public ModelOption[] AvailableTextModels { get; }
    private ModelOption _selectedConversationModel;
    public ModelOption SelectedConversationModel
    {
        get => _selectedConversationModel;
        set { if (Set(ref _selectedConversationModel, value)) Notify(nameof(CurrentModelDisplayName)); }
    }
    public string CurrentModelDisplayName => SelectedConversationModel.ToString();
    public AspectRatioPreset[] AspectRatioPresets => AspectRatioPreset.DefaultPresets.Take(3).ToArray();
    private AspectRatioPreset _selectedAspectRatio = AspectRatioPreset.DefaultPresets[0];
    public AspectRatioPreset SelectedAspectRatio
    {
        get => _selectedAspectRatio;
        set { if (Set(ref _selectedAspectRatio, value)) Notify(nameof(SelectedRatioDisplayText)); }
    }
    public string SelectedRatioDisplayText => SelectedAspectRatio.Ratio;
    private bool _enableReasoning, _enableWebSearch;
    public bool EnableReasoning { get => _enableReasoning; set => Set(ref _enableReasoning, value); }
    public bool EnableWebSearch
    {
        get => _enableWebSearch;
        set { if (Set(ref _enableWebSearch, value)) SearchNotificationCount++; }
    }
    public int SearchNotificationCount { get; private set; }
    public string WebSearchButtonToolTip => "隔离状态展示，不执行搜索";
    public int MaxConversationTurns { get; set; } = 12;
    public bool EnableRecentImageCarry { get; set; } = true;
    public ICommand? OptimizePromptCommand => null;
    public string StatusText => "安全夹具，仅测呈现";
}