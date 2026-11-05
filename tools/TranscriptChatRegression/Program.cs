using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using TrueFluentPro;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        IconProvider.Current.Register<FontAwesomeIconProvider>();
        AppBuilder.Configure<RegressionApplication>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        // Chrome 专项自己管理 45 秒 deadline，清理窗口/主题后才退出消息循环。
        using var stop = args.Contains("--window-chrome") || args.Contains("--window-chrome-visual")
            ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var exitCode = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (args.Contains("--window-chrome-visual")) await WindowChromeRegression.VerifyVisualAsync();
                else if (args.Contains("--window-chrome")) await WindowChromeRegression.VerifyAsync();
                else if (args.Contains("--ink-study")) await InkStudyRegression.VerifyAsync();
                else if (args.Contains("--appearance")) await ShellAppearanceRegression.VerifyAsync();
                else if (args.Contains("--floating-subtitle")) await FloatingSubtitleRegression.VerifyAsync();
                else await VerifyAsync();
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return exitCode;
    }

    private static async Task VerifyAsync()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "transcript-chat-regression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalSessionsPath = PathManager.Instance.SessionsPath;
        var originalServices = App.Services;
        PathManager.Instance.SetSessionsPath(root);
        using var db = new IsolatedDb(Path.Combine(root, "test.db"));
        var voiceFactory = new FakeVoiceInputFactory();
        using var services = new ServiceCollection()
            .AddSingleton<ISqliteDbService>(db)
            .AddSingleton(new SqliteFeatureSwitches { IsReady = true })
            .AddSingleton<IStoragePathResolver, StoragePathResolver>()
            .AddSingleton<ICreativeSessionRepository, CreativeSessionRepository>()
            .AddSingleton<ISessionMessageRepository, SessionMessageRepository>()
            .AddSingleton<ISessionContentRepository, SessionContentRepository>()
            .AddSingleton<FileIdCache>()
            .AddSingleton<IChatVoiceInputFactory>(voiceFactory)
            .BuildServiceProvider();
        var servicesProperty = typeof(App).GetProperty(nameof(App.Services))!;
        servicesProperty.SetValue(null, services);
        MainWindow? owner = null;
        try
        {
            var config = new AzureSpeechConfig();
            var studio = new MediaStudioView();
            studio.Initialize(config.AiConfig ?? new AiConfig(), config.MediaGenConfig, config.Endpoints,
                new NoModelResolver(), new NoTokenStore(), new ConfigurationService(), () => config);
            var vm = studio.ViewModel!;
            await vm.InitializationTask;

            owner = new MainWindow();
            var pages = (Dictionary<string, Control>)Field(typeof(MainWindow), "_pageCache").GetValue(owner)!;
            pages.Add(MainWindowViewModel.NavTagMedia, studio);
            Field(typeof(MainWindow), "_mediaStudioInitialized").SetValue(owner, true);
            owner.Show();

            var transcript = AudioLabExportService.BuildTranscriptMarkdown("测试会议", "测试会议.mp3",
                new List<TranscriptSegment>
                {
                    new() { StartTime = TimeSpan.FromSeconds(10), Speaker = "主持人", Text = "会议开始标记" },
                    new() { StartTime = TimeSpan.FromMinutes(40), Speaker = "嘉宾", Text = new string('字', 16000) + "会议结尾标记" }
                });
            var open = Method(typeof(MainWindow), "OpenTranscriptChatAsync");
            await (Task)open.Invoke(owner, new object[] { "测试会议.mp3", transcript, "[0:10] 主持人: 会议开始标记" })!;
            var session = vm.CurrentSession!;
            var windowField = Field(typeof(MainWindow), "_workshopChatWindow");
            var window = (Window)windowField.GetValue(owner)!;
            Check(window.IsVisible && ReferenceEquals(window.Content, studio), "独立窗口未显示聊天视图");
            Check(session.IsTextMode && session.PromptText.Contains("会议开始标记"), "未切文字聊天或未带入选中字幕");
            Check(session.TotalMessageCount == 1 && session.AllMessages[0].HasAttachments, "录音稿未以附件消息保存");
            var attachment = session.AllMessages[0].Attachments.Single();
            Check(File.ReadAllText(attachment.FilePath) == transcript && attachment.FileSize > 15000, "完整录音稿快照丢失");
            Check(attachment.FilePath.StartsWith(session.SessionDirectory, StringComparison.OrdinalIgnoreCase), "附件不在会话目录中");
            Console.WriteLine("PASS window, selected quote and full transcript snapshot");

            // 走生产 SQLite 写入/加载，而非仅验证 JSON 元数据。
            var bundle = services.GetRequiredService<ISessionMessageRepository>().GetSessionBundle(session.SessionId);
            Check(bundle.Messages.Count == 1 && bundle.Attachments.Values.Single().Single().AttachmentType == "transcript",
                "尚未提问的录音稿附件没有立即落库");
            var load = Method(typeof(MediaStudioViewModel), "LoadSessionData");
            var loaded = ((List<ChatMessageViewModel> Messages, List<MediaGenTask> Tasks))load.Invoke(vm,
                new object[] { session.SessionId, CancellationToken.None })!;
            var restored = new MediaSessionViewModel(session.SessionId, session.SessionName, session.SessionDirectory,
                new AiConfig(), new MediaGenConfig(), new(), new NoModelResolver(), new NoTokenStore(), null!, null!, () => { });
            restored.ReplaceAllMessages(loaded.Messages);
            Check(restored.Messages.Count == 1 && restored.AllMessages[0].HasAttachments, "单条附件历史恢复失败");
            Console.WriteLine("PASS immediate SQLite save and single-message history restore");

            var build = Method(typeof(MediaSessionViewModel), "BuildMultiTurnContent");
            static ChatMessageViewModel Message(string role, string text, bool loading = false)
                => new(new MediaChatMessage { Role = role, Text = text, ContentType = "text" }) { IsLoading = loading };
            restored.AppendMessage(Message("user", "第一个问题"));
            var placeholder = Message("assistant", "正在搜索网页，不应进入上下文", true);
            restored.AppendMessage(placeholder);
            var first = (string)build.Invoke(restored, new object[] { "第一个问题" })!;
            Check(first.Contains(transcript) && first.Contains("[00:40:00] 嘉宾"), "首轮缺少完整字幕或时间/说话人");
            Check(!first.Contains("正在搜索网页"), "加载占位文案污染上下文");
            placeholder.Text = "第一轮回答标记";
            placeholder.IsLoading = false;
            restored.AppendMessage(Message("user", "第二个问题"));
            restored.AppendMessage(Message("assistant", "", true));
            var followUp = (string)build.Invoke(restored, new object[] { "第二个问题" })!;
            Check(followUp.Contains(transcript) && followUp.Contains("第一个问题") && followUp.Contains("第一轮回答标记"), "追问丢失字幕或对话历史");
            restored.MaxConversationTurns = 1;
            var limited = (string)build.Invoke(restored, new object[] { "第二个问题" })!;
            Check(limited.Contains(transcript) && limited.Split("会议结尾标记").Length == 2, "历史轮数淘汰录音稿或重复注入附件");
            Console.WriteLine("PASS first turn, follow-up, loading-state exclusion and transcript beyond history limit");

            // 普通文字附件也需跟随相应的历史用户消息。
            var notePath = Path.Combine(root, "补充资料.txt");
            File.WriteAllText(notePath, "普通附件记忆标记");
            restored.AppendMessage(new ChatMessageViewModel(new MediaChatMessage
            {
                Text = "补充资料", ContentType = "text",
                Attachments = new() { new() { Type = "text", FileName = "补充资料.txt", FilePath = notePath } }
            }));
            restored.AppendMessage(Message("user", "第三个问题"));
            var withNote = (string)build.Invoke(restored, new object[] { "第三个问题" })!;
            Check(withNote.Contains("普通附件记忆标记"), "普通历史附件正文没有进入追问");

            await VerifyVoiceInputAsync(studio, vm, window, voiceFactory);
            await VerifyVoiceProtocolAsync();

            // 关闭独立窗、返回创作工坊、再次从字幕打开，保持同一个会话管理器。
            window.Close();
            Check(windowField.GetValue(owner) == null && studio.Parent == null, "关闭窗口未释放视图宿主");
            Method(typeof(MainWindow), "ShowPage").Invoke(owner, new object[] { MainWindowViewModel.NavTagMedia });
            Check(studio.Parent != null && studio.IsVisible, "关闭后无法返回创作工坊");
            await (Task)open.Invoke(owner, new object[] { "另一会议.mp3", "另一份录音稿", "" })!;
            var reopened = (Window)windowField.GetValue(owner)!;
            Check(!ReferenceEquals(reopened, window) && ReferenceEquals(reopened.Content, studio), "重新打开复用了已关闭窗口或丢失视图");
            Check(vm.Sessions.Contains(session) && vm.CurrentSession != session, "新提问覆盖了原会话历史");
            Method(typeof(MainWindow), "ShowPage").Invoke(owner, new object[] { MainWindowViewModel.NavTagMedia });
            Check(ReferenceEquals(reopened.Content, studio), "导航把已打开的聊天视图重复加入主窗体");
            reopened.Close();
            restored.ReplaceAllMessages(Array.Empty<ChatMessageViewModel>());
            Check(restored.Messages.Count == 0, "空会话恢复失败");
            Console.WriteLine("PASS close, return to workshop, reopen and preserve previous session");
            Console.WriteLine($"ARTIFACTS={root}");
        }
        finally
        {
            owner?.Close();
            servicesProperty.SetValue(null, originalServices);
            PathManager.Instance.SetSessionsPath(originalSessionsPath);
        }
    }

    private static MethodInfo Method(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifyVoiceInputAsync(MediaStudioView studio, MediaStudioViewModel vm,
        Window chatWindow, FakeVoiceInputFactory factory)
    {
        var target = vm.CurrentSession!;
        var messageCount = target.TotalMessageCount;
        target.PromptText = "明天开会。请大家准时参加。";
        await FlushAsync();
        var input = studio.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptTextBox" && t.IsEffectivelyVisible);
        SelectInput(input, 5, 5);
        await vm.VoiceInput.ToggleAsync();
        var first = factory.Last!;
        Check(vm.VoiceInput.IsRecording && target.IsVoiceInputActive && !target.GenerateCommand.CanExecute(null),
            "听写状态未生效或录音期间允许发送");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var button = studio.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("voice-input-btn") && b.IsEffectivelyVisible);
        Check(button.Classes.Contains("voice-active"), "麦克风按钮未绑定录音高亮");
        var surface = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "VoiceButtonSurface");
        Check(surface.Classes.Contains("working"), "录音背景动画未激活");
        Check(surface.Background is ISolidColorBrush recordingBrush && recordingBrush.Color.R > recordingBrush.Color.G + 40,
            "按钮实际绘制的底色不是录音红色");
        var recordingColor = ((ISolidColorBrush)surface.Background!).Color;
        var initialOpacity = surface.Opacity;
        await Task.Delay(350);
        Check(Math.Abs(surface.Opacity - initialOpacity) > 0.05, "按钮整体没有呼吸效果");
        var levelBar = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "VoiceLevelBar");
        var quietHeight = levelBar.Height;
        first.Level(0.9);
        await FlushAsync();
        await Task.Delay(120);
        Check(levelBar.IsEffectivelyVisible && levelBar.Height > quietHeight + 8, "说话电平没有改变按钮音柱高度");
        Check(!vm.VoiceInput.HasStatus, "正常录音仍显示底部状态文字");
        first.Emit("记得带", false);
        await FlushAsync();
        first.Emit("记得带电脑。", false);
        await FlushAsync();
        first.Emit("请记得带电脑。", true);
        await FlushAsync();
        Check(target.PromptText == "明天开会。请记得带电脑。请大家准时参加。", "中间插入/片段修订覆盖前后原文或重复追加");
        CheckRenderedCaret(input, "句中插入", 12);
        first.Emit("谢谢。", true);
        await FlushAsync();
        Check(target.PromptText == "明天开会。请记得带电脑。谢谢。请大家准时参加。", "连续第二句没有接在语音片段后面");
        CheckRenderedCaret(input, "连续第二句", 15);
        ClickInputStart(input);
        await FlushAsync();
        Check(vm.VoiceInput.IsRecording && input.CaretIndex == 0 && ReferenceEquals(factory.Last, first), "调整位置停止/重启了听写或被拉回末尾");
        first.Emit("先提醒：", true);
        await FlushAsync();
        Check(target.PromptText.StartsWith("先提醒：明天开会。"), "录音中移动锚点没有在新位置插入");
        CheckRenderedCaret(input, "录音中改锚点", 4);
        var activeAnchor = target.PromptText.IndexOf("请记得", StringComparison.Ordinal);
        SelectInput(input, activeAnchor, activeAnchor);
        first.Emit("新的提醒", false);
        await FlushAsync();
        ClickInputStart(input);
        await FlushAsync();
        first.Emit("新的提醒，别迟到。", true);
        await FlushAsync();
        Check(target.PromptText.Contains("明天开会。新的提醒，别迟到。请记得"), "未定稿句被搬走/重复或修改了相邻原文");
        CheckRenderedCaret(input, "当前句修订保留用户新位置", 0);
        first.Emit("另外：", true);
        await FlushAsync();
        Check(target.PromptText.StartsWith("另外：先提醒："), "下一句未使用录音中调整的新锚点");
        SelectInput(input, target.PromptText.IndexOf("谢谢。", StringComparison.Ordinal), target.PromptText.IndexOf("谢谢。", StringComparison.Ordinal));
        first.Emit("补", false);
        await FlushAsync();
        var replaceStart = target.PromptText.IndexOf("请大家准时参加。", StringComparison.Ordinal);
        SelectInput(input, replaceStart, replaceStart + "请大家准时参加。".Length);
        first.Emit("补充说明。", true);
        await FlushAsync();
        Check(input.SelectedText == "请大家准时参加。" && vm.VoiceInput.IsRecording,
            "当前句变长导致下一句选区偏移/丢失，或点击停止录音");
        first.Emit("请提前到场。", true);
        await FlushAsync();
        Check(!target.PromptText.Contains("请大家准时参加。") && target.PromptText.EndsWith("请提前到场。"), "选区替换没有保持普通编辑规则");
        Check(input.Text == target.PromptText, "实时识别没有显示在实际输入框");
        CheckRenderedCaret(input, "选区替换");
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home, KeyModifiers = KeyModifiers.Control });
        await FlushAsync();
        Check(input.CaretIndex == 0 && vm.VoiceInput.IsRecording, "听写中不能用键盘正常调整位置");
        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "【键盘插入】" });
        await FlushAsync();
        first.Emit("接着说。", true);
        await FlushAsync();
        Check(target.PromptText.StartsWith("【键盘插入】接着说。另外："), "键盘/语音交错插入串位置");
        CheckRenderedCaret(input, "键盘及语音交错", "【键盘插入】接着说。".Length);
        var savedPrompt = target.PromptText;
        var spoken = "嗯，看起来你有什么想说的吗？可以跟我们聊一聊。\n嗯，如果说你有什么更多的想法，你可以跟我们聊一下，我相信大家都会有一些愿意跟你聊的东西在一起。";
        target.PromptText = spoken[..^6];
        target.InputCursor.RequestPosition(target.PromptText.Length, target.PromptText.Length, target.PromptText.Length);
        await FlushAsync();
        target.PromptText = spoken;
        target.InputCursor.RequestPosition(spoken.Length, spoken.Length, spoken.Length);
        await FlushAsync();
        CheckRenderedCaret(input, "中文增量末尾");
        await Task.Delay(80);
        CheckRenderedCaret(input, "等待下一句");
        target.PromptText = spoken + " Hello 😊。";
        target.InputCursor.RequestPosition(target.PromptText.Length, target.PromptText.Length, target.PromptText.Length);
        await FlushAsync();
        CheckRenderedCaret(input, "中英混排追加");
        target.PromptText = spoken[..^3] + "修订。";
        target.InputCursor.RequestPosition(target.PromptText.Length, target.PromptText.Length, target.PromptText.Length);
        await FlushAsync();
        CheckRenderedCaret(input, "识别片段缩短修订");
        target.PromptText = string.Join(Environment.NewLine, Enumerable.Repeat(spoken, 5));
        target.InputCursor.RequestPosition(target.PromptText.Length, target.PromptText.Length, target.PromptText.Length);
        await FlushAsync();
        CheckRenderedCaret(input, "多行长文");
        target.PromptText = savedPrompt;
        await FlushAsync();
        SelectInput(input, target.PromptText.Length, target.PromptText.Length);
        first.Emit("临时识别", false);
        await FlushAsync();
        target.PromptText = target.PromptText.Replace("临时识别", "用户手动修改");
        first.Emit("服务端修订", true);
        await FlushAsync();
        Check(target.PromptText.EndsWith("用户手动修改") && !target.PromptText.Contains("服务端修订"), "覆盖了用户手动改写");
        first.FinalOnStop = "停止时最后一句。";
        first.StopGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeStop = target.PromptText;
        first.Emit("已排队但停止前尚未显示的文字", true);
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        var stopping = vm.VoiceInput.ToggleAsync();
        Check(stopping.IsCompletedSuccessfully && !vm.VoiceInput.IsActive && !target.IsVoiceInputActive,
            "点击停止没有立即结束听写状态");
        await stopping;
        await first.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!first.StopGate.Task.IsCompleted && target.GenerateCommand.CanExecute(null), "设备后台释放阻塞了输入/发送");
        await FlushAsync();
        Check(target.PromptText == beforeStop && !button.Classes.Contains("voice-active")
            && !surface.Classes.Contains("working"), "停止后继续追加了尾句/迟到文字，或按钮仍亮着");
        Check(surface.Background is ISolidColorBrush stoppedBrush && stoppedBrush.Color != recordingColor,
            "停止按钮颜色未恢复");
        Console.WriteLine($"VOICE_STOP_UI_MS={stopWatch.Elapsed.TotalMilliseconds:F1} (native cleanup deliberately held)");
        first.StopGate.TrySetResult(true);
        Check(target.TotalMessageCount == messageCount && target.GenerateCommand.CanExecute(null), "听写自动发送了消息");
        input.CaretIndex = 0;
        input.SelectionStart = 0;
        input.SelectionEnd = 2;
        Check(input.CaretIndex < input.Text!.Length && input.SelectionStart == 0 && input.SelectionEnd == 2,
            "停止后没有恢复正常选字编辑");
        var stoppedText = target.PromptText;
        first.Emit("迟到的结果", true);
        await FlushAsync();
        Check(target.PromptText == stoppedText, "停止后迟到结果污染输入框");

        await vm.VoiceInput.ToggleAsync();
        var switching = factory.Last!;
        switching.FinalOnStop = "切换前的尾句。";
        var beforeSwitch = target.PromptText;
        vm.CreateNewSession();
        await vm.VoiceInput.StopAsync();
        await FlushAsync();
        Check(vm.CurrentSession!.PromptText.Length == 0 && target.PromptText == beforeSwitch, "切换会话后仍写入尾句/串入语音文字");

        await vm.VoiceInput.ToggleAsync();
        factory.Last!.Fail("模拟连接中断，请重试。");
        await FlushAsync();
        await vm.VoiceInput.StopAsync();
        Check(!vm.VoiceInput.IsActive && vm.VoiceInput.StatusText.Contains("模拟连接中断"), "连接失败后仍显示录音中");

        factory.DelayNextStart = true;
        var starting = vm.VoiceInput.ToggleAsync();
        Check(vm.VoiceInput.IsActive && !vm.VoiceInput.IsRecording, "连接中状态错误");
        await vm.VoiceInput.ToggleAsync();
        await starting;
        Check(!vm.VoiceInput.IsActive, "连接中再次切换没有停止");

        var hotkey = (ChatVoiceInputHotkey?)Field(typeof(MediaStudioView), "_voiceHotkey").GetValue(studio);
        Check(hotkey?.IsRegistered == true, "F8 全局热键未注册：" + vm.VoiceInput.HotkeyHint);
        var otherWindow = new Window { Title = "全局热键测试（不包含聊天控件）", Width = 320, Height = 160 };
        var hotkeyMessages = 0;
        Win32Properties.CustomWndProcHookCallback diagnostic = (IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0312) hotkeyMessages++;
            return IntPtr.Zero;
        };
        Win32Properties.AddWndProcHookCallback(chatWindow, diagnostic);
        try
        {
            otherWindow.Show();
            otherWindow.Activate();
            await FlushAsync();
            var canInject = GetForegroundWindow() == otherWindow.TryGetPlatformHandle()?.Handle;
            if (!canInject)
            {
                Console.WriteLine("SKIP physical global F8: test window did not obtain foreground; verify native WM_HOTKEY delivery instead");
            }
            void ToggleHotkey()
            {
                if (canInject) PressF8();
                else SendMessage(chatWindow.TryGetPlatformHandle()!.Handle, 0x0312, new IntPtr(0x5F08), new IntPtr(0x77 << 16));
            }
            ToggleHotkey();
            await UntilAsync(() => vm.VoiceInput.IsRecording, "原生热键消息未启动听写");
            ToggleHotkey();
            await UntilAsync(() => !vm.VoiceInput.IsActive, "第二次原生热键消息未停止听写");
            Check(hotkeyMessages == 2, "原生热键消息重复或缺失");
        }
        finally
        {
            Win32Properties.RemoveWndProcHookCallback(chatWindow, diagnostic);
            otherWindow.Close();
        }
        await vm.VoiceInput.ToggleAsync();
        chatWindow.Close();
        await vm.VoiceInput.StopAsync();
        Check(!vm.VoiceInput.IsActive && !hotkey!.IsRegistered, "关闭聊天未停止听写/释放全局热键");
        Console.WriteLine("PASS anchored insertion/revision, free reposition without stopping, selection replacement, immediate stop and native WM_HOTKEY toggle");
    }

    private static async Task VerifyVoiceProtocolAsync()
    {
        var original = new AzureSpeechConfig { SourceLanguage = "en-US" };
        var factory = new ChatVoiceInputFactory(new SpeechResourceRuntimeResolver(new NoModelResolver()),
            new RealtimeConnectionSpecResolver(), new NoTokenStore());
        await using var voice = factory.Create(original);
        var type = voice.GetType();
        var snapshot = (AzureSpeechConfig)Field(type, "_config").GetValue(voice)!;
        Check(snapshot.SourceLanguage == "auto" && original.SourceLanguage == "en-US" && !ReferenceEquals(snapshot, original),
            "听写改变了全局语音语言配置");
        foreach (var route in new[] { RealtimeEndpointRouteKind.OpenAiGa, RealtimeEndpointRouteKind.AzureOpenAiGa, RealtimeEndpointRouteKind.AzureOpenAiPreview })
        {
            var payload = Method(type, "BuildSessionUpdate").Invoke(voice, new object[] { new RealtimeConnectionSpec { RouteKind = route } })!;
            var json = JsonSerializer.Serialize(payload);
            Check(json.Contains("\"create_response\":false") && !json.Contains("response.create") && !json.Contains("\"language\""),
                "听写会产生模型回复/译文或强制改变语言");
        }
        Field(type, "_incrementalDeltas").SetValue(voice, true);
        var finals = new List<string>();
        voice.TextRecognized += (text, final) => { if (final) finals.Add(text); };
        void Event(string json)
        {
            using var doc = JsonDocument.Parse(json);
            Method(type, "HandleRealtimeEvent").Invoke(voice, new object[] { doc.RootElement });
        }
        Event("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"a\"}");
        Event("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"b\"}");
        Event("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"b\",\"transcript\":\"second\"}");
        Event("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"a\",\"transcript\":\"first\"}");
        Event("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"a\",\"transcript\":\"duplicate\"}");
        Event("{\"type\":\"response.output_text.delta\",\"delta\":\"not dictation\"}");
        Check(finals.SequenceEqual(new[] { "first", "second" }), "乱序/重复/回复事件污染原文定稿");
        await voice.StopAsync();
        Event("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"after-stop\",\"transcript\":\"late\"}");
        Check(finals.Count == 2, "生产听写服务停止后仍发布识别文字");
        Console.WriteLine("PASS production dictation payloads, config isolation and ordered transcription-only events (no cloud request)");
    }

    private static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void SelectInput(TextBox input, int start, int end)
    {
        input.CaretIndex = end;
        input.SelectionStart = start;
        input.SelectionEnd = end;
    }

    private static void ClickInputStart(TextBox input)
    {
        var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter");
        var root = TopLevel.GetTopLevel(input)!;
        var point = presenter.TranslatePoint(new Point(0, 10), root)!.Value;
        using var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        input.Focus();
        input.RaiseEvent(new PointerPressedEventArgs(input, pointer, root, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        input.RaiseEvent(new PointerReleasedEventArgs(input, pointer, root, point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Check(input.CaretIndex == 0 && input.SelectionStart == 0 && input.SelectionEnd == 0, "实际鼠标路由没有移动到点击位置");
    }

    private static void CheckRenderedCaret(TextBox input, string stage, int? expectedIndex = null)
    {
        var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter");
        _ = presenter.TextLayout;
        var hit = (CharacterHit)Field(typeof(TextPresenter), "_lastCharacterHit").GetValue(presenter)!;
        var renderedIndex = hit.FirstCharacterIndex + hit.TrailingLength;
        var length = input.Text?.Length ?? 0;
        var expected = expectedIndex ?? length;
        Console.WriteLine($"CARET {stage}: text={length}, logical={input.CaretIndex}, presenter={presenter.CaretIndex}, rendered={renderedIndex}");
        Check(presenter.Text == input.Text && input.CaretIndex == expected && presenter.CaretIndex == expected
            && renderedIndex == expected && presenter.SelectionStart == expected && presenter.SelectionEnd == expected,
            $"{stage}：逻辑/绘制光标或选区未跟随语音插入位置");
        var caret = (Rect)Method(typeof(TextPresenter), "GetCursorRectangle").Invoke(presenter, null)!;
        var visibleEnd = presenter.TranslatePoint(caret.BottomRight, input);
        Check(visibleEnd is { } point && point.X >= -1 && point.X <= input.Bounds.Width + 1
            && point.Y >= -1 && point.Y <= input.Bounds.Height + 1,
            $"{stage}：实际末尾光标未滚入输入框可见范围");
    }
    private static async Task UntilAsync(Func<bool> ready, string error)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(20);
        Check(ready(), error);
    }
    private static void PressF8()
    {
        var events = new[]
        {
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = 0x77 } } },
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = 0x77, Flags = 2 } } }
        };
        var sent = SendInput((uint)events.Length, events, Marshal.SizeOf<NativeInput>());
        Console.WriteLine($"HOTKEY SendInput={sent}/2 error={Marshal.GetLastWin32Error()} size={Marshal.SizeOf<NativeInput>()}");
        Check(sent == 2, "系统拒绝键盘注入，无法执行实际热键测试");
    }
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
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
}

internal sealed class FakeVoiceInputFactory : IChatVoiceInputFactory
{
    public FakeVoiceInputSession? Last { get; private set; }
    public bool DelayNextStart;
    public IChatVoiceInputSession Create(AzureSpeechConfig config)
    {
        Last = new FakeVoiceInputSession { DelayStart = DelayNextStart };
        DelayNextStart = false;
        return Last;
    }
}

internal sealed class FakeVoiceInputSession : IChatVoiceInputSession
{
    public event Action<string, bool>? TextRecognized;
    public event Action<string>? Failed;
    public event Action<double>? AudioLevelChanged;
    public bool DelayStart;
    public string? FinalOnStop;
    public TaskCompletionSource<bool>? StopGate;
    public TaskCompletionSource<bool> StopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task StartAsync(CancellationToken cancellationToken = default)
        => DelayStart ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopEntered.TrySetResult(true);
        if (FinalOnStop != null)
        {
            Emit(FinalOnStop, true);
            FinalOnStop = null;
        }
        if (StopGate != null) await StopGate.Task;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Emit(string text, bool final) => TextRecognized?.Invoke(text, final);
    public void Fail(string message) => Failed?.Invoke(message);
    public void Level(double level) => AudioLevelChanged?.Invoke(level);
}

public sealed class RegressionApplication : Application
{
    public override void Initialize()
    {
        // 仅载入生产主题，不执行 App.OnFrameworkInitializationCompleted 的真实服务和后台队列。
        var theme = new App();
        theme.Initialize();
        var resources = theme.Resources;
        theme.Resources = new ResourceDictionary();
        Resources = resources;
        foreach (var style in theme.Styles.ToArray())
        {
            theme.Styles.Remove(style);
            Styles.Add(style);
        }
    }
}

internal sealed class NoModelResolver : IModelRuntimeResolver
{
    public bool TryResolve(AzureSpeechConfig config, ModelReference? reference, ModelCapability capability,
        out ModelRuntimeResolution? runtime, out string errorMessage)
    {
        runtime = null;
        errorMessage = "隔离测试不调用模型";
        return false;
    }
}

internal sealed class NoTokenStore : IAzureTokenProviderStore
{
    public AzureTokenProvider GetProvider(string? profileKey = null) => null!;
    public Task<AzureTokenProvider?> GetAuthenticatedProviderAsync(string? profileKey, string? tenantId,
        string? clientId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("隔离测试不得请求真实凭据");
}

internal sealed class IsolatedDb : ISqliteDbService
{
    public string DatabasePath { get; }
    public int SchemaVersion => 7;
    public IsolatedDb(string path)
    {
        DatabasePath = path;
        using var connection = CreateConnection();
        // 复用生产 schema，数据库文件严格限制在本次 artifacts 目录内。
        typeof(SqliteDbService).GetMethod("CreateAllTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { connection });
    }
    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }
    public void EnsureCreated() { }
    public string? GetMeta(string key) => null;
    public void SetMeta(string key, string value) => throw new NotSupportedException();
    public void Dispose() { }
}