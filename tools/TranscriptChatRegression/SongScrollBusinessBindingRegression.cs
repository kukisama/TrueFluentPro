using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TrueFluentPro;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

internal static class SongScrollBusinessBindingRegression
{
    private const string Draft = "隔离草稿：切换外观后仍在。";
    private const string RevisedDraft = "隔离草稿：从长卷返回经典，再回长卷。";
    private const string UserText = "安全夹具问题：纸上有什么？";
    private const string AssistantText = "安全夹具回答：纸上有山河。";

    internal static async Task VerifyAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("业务绑定专项需要 Windows Avalonia 窗口及真实热键注册。验收不发送按键。");
        Check(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "TrueFluentPro.csproj")),
            "请从仓库根目录运行，拒绝将夹具保存到未知位置。");
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "song-scroll-workspace-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-business-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var report = new List<string>();
        void Log(string value) { report.Add(value); Console.WriteLine(value); }
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var originalSessions = PathManager.Instance.SessionsPath;
        var originalServices = App.Services;
        var servicesProperty = typeof(App).GetProperty(nameof(App.Services))!;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var sessionsRoot = Path.Combine(root, "sessions");
        var appearancePath = Path.Combine(root, "appearance.json");
        IsolatedDb? db = null;
        ServiceProvider? services = null;
        MainWindow? owner = null;
        MediaStudioViewModel? business = null;
        EventHandler? disposeObserver = null;
        var closeCalled = false;
        var passed = false;
        var group = "isolation-and-initialization";
        var hotkeys = new List<ChatVoiceInputHotkey>();
        try
        {
            Log("ARTIFACTS=" + root);
            Log("ASSEMBLY=" + typeof(MainWindow).Assembly.Location);
            Log("SCOPE=真实MediaStudioView.Initialize/共享VM/生产ShowPage/宿主显隐；隔离SQLite与sessions；45秒协作deadline；无MainVM、配置读写、云任务、音频或输入注入");
            Log("NOT_COVERED=物理F8/鼠标、真实听写/生成、字幕问答独立窗、其他业务页、真实高DPI及设计稿视觉一致性；PNG仅供人工目视，不代表物理桌面截图");
            PathManager.Instance.SetSessionsPath(sessionsRoot);
            Check(PathManager.Instance.SessionsPath == sessionsRoot, "会话隔离路径未生效，拒绝初始化。");
            db = new IsolatedDb(Path.Combine(root, "business.db"));
            var voiceFactory = new FakeVoiceInputFactory();
            services = new ServiceCollection()
                .AddSingleton<ISqliteDbService>(db)
                .AddSingleton(new SqliteFeatureSwitches { IsReady = true })
                .AddSingleton<IStoragePathResolver, StoragePathResolver>()
                .AddSingleton<ICreativeSessionRepository, CreativeSessionRepository>()
                .AddSingleton<ISessionMessageRepository, SessionMessageRepository>()
                .AddSingleton<ISessionContentRepository, SessionContentRepository>()
                .AddSingleton<FileIdCache>()
                .AddSingleton<IChatVoiceInputFactory>(voiceFactory)
                .BuildServiceProvider();
            servicesProperty.SetValue(null, services);
            Check(services.GetRequiredService<IStoragePathResolver>().WorkspaceRoot == sessionsRoot,
                "存储解析器没有捕获隔离会话路径。");

            owner = new MainWindow(new ShellAppearancePreferencesService(appearancePath))
            { Width = 1256, Height = 831, ShowActivated = false };
            Check(owner.DataContext == null, "主窗意外引入真实MainVM，拒绝继续。");
            owner.Show();
            await Pump(owner, token); // 先完成OnOpened的默认路由，不让它覆盖后面的media。
            Navigate(owner);
            await Pump(owner, token);
            var classic = CurrentPage(owner);
            Check(classic.GetType() == typeof(MediaStudioView), "经典media路由没有创建真实MediaStudioView。");
            var config = new AzureSpeechConfig { AiConfig = new AiConfig(), SessionDirectoryOverride = sessionsRoot };
            // ConfigurationService只有无参构造，构造仅记录路径；本专项不调用Load/Save或模型/搜索选择命令。
            classic.Initialize(config.AiConfig, config.MediaGenConfig, config.Endpoints,
                new NoModelResolver(), new NoTokenStore(), new ConfigurationService(), () => config);
            var vm = business = classic.ViewModel ?? throw new InvalidOperationException("真实工坊没有创建VM。");
            Field(typeof(MainWindow), "_mediaStudioBusiness").SetValue(owner, vm);
            Field(typeof(MainWindow), "_mediaStudioInitialized").SetValue(owner, true);
            // 初始化没有取消接口：先收齐本地加载回调，再检查deadline；不能遗留后台加载跨越服务恢复。
            await vm.InitializationTask;
            token.ThrowIfCancellationRequested();
            var session = vm.CurrentSession ?? throw new InvalidOperationException("隔离工坊初始化后没有会话。");
            Check(vm.Sessions.Count == 1 && session.IsContentLoaded && session.TotalMessageCount == 0
                && session.SessionDirectory.StartsWith(sessionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "工坊未从空隔离库创建唯一安全会话。");
            session.SessionName = "安全夹具 · 业务绑定";
            session.PromptText = Draft;
            var disposeReceipts = 0;
            // 无听写时，生产Dispose只Raise一次禁用通知；观察现有事件，不替换/包装VM.Dispose。
            disposeObserver = (_, _) => { if (!vm.VoiceInput.ToggleCommand.CanExecute(null)) disposeReceipts++; };
            vm.VoiceInput.ToggleCommand.CanExecuteChanged += disposeObserver;
            await Pump(owner, token);
            CheckBusiness(owner, classic, vm, session);
            var classicInput = CheckPrompt(classic, session, Draft);
            CheckHotkeys(classic, null, vm, hotkeys, "classic-initial", Log);

            group = "classic-to-scroll";
            owner.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(owner, token);
            var scroll = CurrentPage(owner) as SongScrollMediaStudioView
                ?? throw new InvalidOperationException("长卷media路由没有创建独立派生树。");
            Check(!ReferenceEquals(classic, scroll), "切外观复用了经典控件树。");
            CheckBusiness(owner, scroll, vm, session);
            Check(!ReferenceEquals(classicInput, CheckPrompt(scroll, session, Draft)), "两个呈现共用了输入控件。");
            CheckHotkeys(scroll, classic, vm, hotkeys, "scroll-first-host-refresh", Log);
            Log("PASS classic-to-scroll: 两棵真实树、同一VM/会话/草稿，宿主显隐后新页按F8归属策略就绪、旧页释放");

            group = "real-session-content";
            var question = new ChatMessageViewModel(new MediaChatMessage { Role = "user", Text = UserText, ContentType = "text" });
            var answer = new ChatMessageViewModel(new MediaChatMessage { Role = "assistant", Text = AssistantText, ContentType = "text" });
            session.AppendMessage(question);
            session.AppendMessage(answer);
            await Pump(owner, token);
            CheckConversation(scroll, session, question, answer);
            CheckPrompt(scroll, session, Draft);
            Capture(owner, Path.Combine(root, "business-binding-rendered.png"), Log);
            Log("PASS real-session-content: 非空态、真实消息列表/用户文字/Markdown字形节点/PromptTextBox及TextPresenter；只追加安全fixture，不执行Generate");

            group = "appearance-roundtrip";
            session.PromptText = RevisedDraft;
            await Pump(owner, token);
            CheckPrompt(scroll, session, RevisedDraft);
            owner.ApplyShellAppearance(ShellAppearancePreference.Classic);
            await Pump(owner, token);
            Check(ReferenceEquals(CurrentPage(owner), classic), "返回经典重建了缓存页。");
            CheckBusiness(owner, classic, vm, session);
            CheckPrompt(classic, session, RevisedDraft);
            CheckConversation(classic, session, question, answer);
            CheckHotkeys(classic, scroll, vm, hotkeys, "classic-return-host-refresh", Log);
            owner.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(owner, token);
            Check(ReferenceEquals(CurrentPage(owner), scroll), "返回长卷重建了缓存页。");
            CheckBusiness(owner, scroll, vm, session);
            CheckPrompt(scroll, session, RevisedDraft);
            CheckConversation(scroll, session, question, answer);
            CheckHotkeys(scroll, classic, vm, hotkeys, "scroll-return-host-refresh", Log);
            Check(disposeReceipts == 0 && voiceFactory.Last == null && !vm.VoiceInput.IsActive
                && !session.IsVoiceInputActive && !session.IsGenerating && !session.IsChatStreaming
                && session.TaskHistory.Count == 0 && vm.ActiveTaskCount == 0, "外观切换释放了共享业务或启动了音频/生成任务。");
            Check(!File.Exists(appearancePath), "仅应用外观却保存了偏好文件。");
            Log("PASS appearance-roundtrip: 经典→长卷→经典→长卷，同源草稿及两条消息实际呈现保持、可见页尊重F8归属策略、无语音工厂调用");

            group = "owner-close";
            token.ThrowIfCancellationRequested();
            closeCalled = true;
            owner.Close(); // 唯一业务释放入口；不另调classic.Cleanup/scroll.Cleanup/vm.Dispose。
            Check(!owner.IsVisible && classic.ViewModel == null && scroll.ViewModel == null
                && classic.DataContext == null && scroll.DataContext == null
                && classic.Parent == null && scroll.Parent == null
                && Field(typeof(MainWindow), "_mediaStudioBusiness").GetValue(owner) == null
                && hotkeys.All(h => !h.IsRegistered) && Hotkey(classic) == null && Hotkey(scroll) == null,
                "owner.Close未解绑两棵呈现/清空共享owner/释放全部已观察热键。");
            Check(disposeReceipts == 1 && !vm.VoiceInput.ToggleCommand.CanExecute(null) && voiceFactory.Last == null,
                $"共享释放通知应恰好一次，实际={disposeReceipts}；或清理触发了语音工厂。");
            Log("PASS owner-close: owner.Close单入口解绑两棵树并释放共享业务（禁用通知恰好1次），全部热键已释放");
            passed = true;
        }
        catch (Exception ex) { Log($"FAIL group={group}: {ex}"); throw; }
        finally
        {
            try
            {
                if (!closeCalled && owner != null) { closeCalled = true; owner.Close(); }
            }
            catch (Exception ex) { passed = false; Log("FAIL cleanup-owner: " + ex); }
            finally
            {
                if (business != null && disposeObserver != null)
                    business.VoiceInput.ToggleCommand.CanExecuteChanged -= disposeObserver;
                servicesProperty.SetValue(null, originalServices);
                PathManager.Instance.SetSessionsPath(originalSessions);
                Application.Current!.RequestedThemeVariant = originalTheme;
                try { services?.Dispose(); db?.Dispose(); }
                catch (Exception ex) { passed = false; Log("FAIL cleanup-services: " + ex); }
                var restored = ReferenceEquals(App.Services, originalServices)
                    && PathManager.Instance.SessionsPath == originalSessions;
                if (!restored) passed = false;
                Log("RESTORED_SERVICES_AND_SESSIONS=" + restored);
                watch.Stop();
                Log($"ELAPSED_MS={watch.Elapsed.TotalMilliseconds:F0}");
                Log("SONG_SCROLL_BUSINESS_BINDING_EXIT=" + (passed ? 0 : 1));
                if (!passed) Log("NOT_RUN=失败点之后未完成；不重试、不跳过断言、不用局部PASS冒充整体验收");
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
            }
        }
        Check(passed, "清理/恢复未全部通过，请查看本次result.txt。");
    }

    private static void CheckBusiness(MainWindow owner, MediaStudioView page, MediaStudioViewModel vm, MediaSessionViewModel session)
    {
        Check(owner.DataContext == null && ReferenceEquals(page, CurrentPage(owner)) && page.IsEffectivelyVisible
            && ReferenceEquals(page.ViewModel, vm) && ReferenceEquals(page.DataContext, vm)
            && ReferenceEquals(vm.CurrentSession, session) && vm.Sessions.Count == 1 && ReferenceEquals(vm.Sessions[0], session)
            && ReferenceEquals(Field(typeof(MainWindow), "_mediaStudioBusiness").GetValue(owner), vm), "当前页的真实共享VM/会话或主窗隔离失效。");
        var host = owner.FindControl<Panel>(page is ISongScrollPage ? "SongScrollHost" : "PageHost")!;
        Check(host.IsEffectivelyVisible && host.Children.Contains(page), "页面不在可见的生产宿主内。");
        if (page is ISongScrollPage scroll)
            Check(ReferenceEquals(scroll.Shell.DataContext, vm) && ReferenceEquals(scroll.Shell.LeftPage?.DataContext, vm)
                && ReferenceEquals(scroll.Shell.RightPage?.DataContext, vm), "长卷Shell/左右页丢失业务上下文继承。");
    }

    private static TextBox CheckPrompt(MediaStudioView page, MediaSessionViewModel session, string expected)
    {
        var host = Visible<CachedContentControl>(page, "SessionContentHost");
        var input = Visible<TextBox>(page, "PromptTextBox");
        var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter");
        Check(ReferenceEquals(host.Content, session) && ReferenceEquals(input.DataContext, session)
            && session.PromptText == expected && input.Text == expected && presenter.Text == expected
            && input.Bounds.Width > 0 && input.Bounds.Height > 0 && presenter.Bounds.Height > 0,
            page.GetType().Name + "：会话/草稿绑定或实际输入文字呈现丢失。");
        return input;
    }

    private static void CheckConversation(MediaStudioView page, MediaSessionViewModel session,
        ChatMessageViewModel question, ChatMessageViewModel answer)
    {
        var list = Visible<ListBox>(page, "MessageListBox");
        Check(ReferenceEquals(list.DataContext, session) && ReferenceEquals(list.ItemsSource, session.Messages)
            && list.ItemCount == 2 && session.TotalMessageCount == 2 && list.Bounds.Height > 0
            && ReferenceEquals(list.Items[0], question) && ReferenceEquals(list.Items[1], answer), "真实消息列表未绑定原会话的两条fixture消息。");
        Check(list.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.IsEffectivelyVisible
            && ReferenceEquals(t.DataContext, question) && t.Text == UserText && t.Bounds.Height > 0), "用户fixture文字没有实际呈现。");
        var markdown = list.GetVisualDescendants().OfType<ChatMarkdownBlock>().Single(m => m.IsEffectivelyVisible
            && ReferenceEquals(m.DataContext, answer) && m.Markdown == AssistantText);
        Check(markdown.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.IsEffectivelyVisible
            && t.Bounds.Height > 0 && t.Inlines?.OfType<Run>().Any(r => r.Text == AssistantText) == true),
            "助手fixture只有Markdown数据，没有实际文字节点。");
        Check(!page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "新建会话，开始落笔。"),
            "已有会话仍显示长卷空态。");
    }

    private static void CheckHotkeys(MediaStudioView active, MediaStudioView? hidden, MediaStudioViewModel vm,
        List<ChatVoiceInputHotkey> observed, string stage, Action<string> log)
    {
        var current = Hotkey(active);
        var oldRegistered = observed.Count(h => !ReferenceEquals(h, current) && h.IsRegistered);
        // 生产 TryRegister 明确规定系统输入法已登记时不抢占 F8；
        // 应验证这一既有契约，而不是把正确让出按键误报为呈现未就绪。
        var nativeImeOwnsHotkey = VoiceImeRegistrationService.HasNativeRegistration();
        log($"HOTKEY stage={stage};active={active.GetType().Name};registered={current?.IsRegistered == true};nativeImeOwner={nativeImeOwnsHotkey};retiredRegistered={oldRegistered};hint={vm.VoiceInput.HotkeyHint}");
        Check(active.IsEffectivelyVisible && current != null,
            stage + "：可见页未连接热键生命周期。");
        if (nativeImeOwnsHotkey)
            Check(!current!.IsRegistered && vm.VoiceInput.HotkeyHint.StartsWith("F8 由系统语音输入法处理", StringComparison.Ordinal),
                stage + "：没有按生产策略把F8留给已登记的系统输入法。");
        else
            Check(current!.IsRegistered,
                stage + "：没有系统输入法归属时，可见页F8应真实注册；生产提示=" + vm.VoiceInput.HotkeyHint);
        Check(oldRegistered == 0 && (hidden == null || (!hidden.IsEffectivelyVisible && Hotkey(hidden) == null)),
            stage + "：隐藏旧页仍持有热键或历史注册未释放。");
        if (!observed.Contains(current!)) observed.Add(current!);
    }

    private static async Task Pump(MainWindow owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        owner.UpdateLayout();
        var composition = ElementComposition.GetElementVisual(owner)
            ?? throw new InvalidOperationException("fixture没有合成器。");
        await Dispatcher.UIThread.AwaitWithPriority(composition.Compositor.RequestCompositionBatchCommitAsync()
            .Rendered.WaitAsync(token), DispatcherPriority.Background);
    }

    private static void Capture(MainWindow owner, string path, Action<string> log)
    {
        var scale = owner.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(owner.Bounds.Width * scale),
            (int)Math.Ceiling(owner.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(owner);
        bitmap.Save(path);
        log($"RENDERED_SNAPSHOT={path};pixels={bitmap.PixelSize};source=RenderTargetBitmap.Render(actual fixture window);非物理桌面截图，待主代理目视");
    }

    private static void Navigate(MainWindow owner) => typeof(MainWindow).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(owner, new object[] { MainWindowViewModel.NavTagMedia });
    private static MediaStudioView CurrentPage(MainWindow owner) => Field(typeof(MainWindow), "_currentPage").GetValue(owner) as MediaStudioView
        ?? throw new InvalidOperationException("当前路由不是工坊页。");
    private static ChatVoiceInputHotkey? Hotkey(MediaStudioView page) => Field(typeof(MediaStudioView), "_voiceHotkey").GetValue(page) as ChatVoiceInputHotkey;
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.Name, name);
    private static T Visible<T>(Control page, string name) where T : Control => page.GetVisualDescendants().OfType<T>()
        .Single(c => c.Name == name && c.IsEffectivelyVisible);
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
}