using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
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

// 只编译测试项目，加载既有生产 DLL。不得把事件回调反射调用当作订阅验证。
internal static class SongScrollRealtimeInteractionRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task VerifyAsync()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "transcript-chat-regression",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-realtime-interaction-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var report = new List<string>();
        var failures = new List<string>();
        var dispatcherErrors = new List<Exception>();
        var clock = Stopwatch.StartNew();
        var stage = "setup";
        var passed = false;
        void Log(string text) { var line = $"{clock.Elapsed.TotalMilliseconds:F1}ms {text}"; report.Add(line); Console.WriteLine(line); }
        void Expect(bool value, string text) { if (!value) { failures.Add(stage + ": " + text); Log("FAIL " + text); } }
        void Checkpoint()
        {
            if (dispatcherErrors.Count > 0) throw new AggregateException("生产 UI 回调异常", dispatcherErrors);
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
        // Post/async-void 异常必须记为失败；仅为落盘和清理测试窗而暂时接住，绝不当作通过。
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        { dispatcherErrors.Add(e.Exception); e.Handled = true; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        var appTheme = Application.Current!.RequestedThemeVariant;
        MainWindow? window = null;
        MainWindowViewModel? main = null;
        ConfigViewModel? configVm = null;
        var service = new EventTranslation(Log);
        var factory = new EventFactory(service, Log);
        var deviceRefreshes = 0;
        var memorySaves = 0;
        var clearNotifications = 0;
        var commandNotifications = 0;
        var frameNotifications = 0;
        var clicks = 0;
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            var assembly = typeof(MainWindowViewModel).Assembly;
            Log("ARTIFACTS=" + root);
            Log($"ASSEMBLY={assembly.Location};mtime={File.GetLastWriteTime(assembly.Location):O};MVID={assembly.ManifestModule.ModuleVersionId};SHA256={Hash(assembly.Location)}");
            var production = Path.Combine(Directory.GetCurrentDirectory(), "bin", "Debug", "net10.0", "TrueFluentPro.dll");
            Expect(Hash(production) == Hash(assembly.Location), "未加载当前 bin/Debug 生产 DLL 的同字节副本");
            Checkpoint();
            Log("SCOPE=真实 MainWindow/经典及长卷页面/Presentation/ConfigVM/AudioDevicesVM/AiInsightVM/RelayCommand；只用合成文字和内存工厂/服务，无录音、DB、凭据、用户配置或云请求。");
            Log("LIMIT=跳过 MainVM 构造（避免 LoadConfig/DI/DB/后台任务），只初始化下方显式字段；ConfigurationService 跳过 PathManager 构造并指向不存在的夹具文件；SettingsVM 仅为窗口 ConfigSaved 事件载体跳过构造，不测试设置或保存。");
            Log("LIMIT=实际 AudioDevicesVM 停止回调会只读枚举系统设备；logger 丢弃设备名称/ID，configSaver 仅内存计数；不创建采集器。输入为真实按钮的 routed Enter→OnClick→Command，不是物理键盘/鼠标验收。");

            var config = new AzureSpeechConfig
            {
                EnableRecording = false, AudioSourceMode = AudioSourceMode.DefaultMic,
                RecordingMode = RecordingMode.MicOnly, UseInputForRecognition = true, UseOutputForRecognition = false,
                RealtimeMaxLength = 400, MaxHistoryItems = 15, AiConfig = new AiConfig(),
                SessionDirectoryOverride = root, ActiveSpeechResourceId = "realtime-fixture",
                SpeechResources = new() { new SpeechResource { Id = "realtime-fixture", Name = "内存语音夹具（无凭据）",
                    ConnectorType = SpeechConnectorType.MicrosoftSpeech, IsEnabled = true, Capabilities = SpeechCapability.RealtimeSpeechToText } }
            };
            var configService = (ConfigurationService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigurationService));
            Set(configService, "_configFilePath", Path.Combine(root, "never-load-or-save.json"));
            Set(configService, "_backupConfigFilePath", Path.Combine(root, "never-load-or-save.json.bak"));
            main = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            var vm = main;
            Set(vm, "_config", config);
            Set(vm, "_configService", configService);
            Set(vm, "_realtimeTranslationServiceFactory", factory); // _translationService 必须仍为 null。
            Set(vm, "_currentOriginal", ""); Set(vm, "_currentTranslated", "");
            Set(vm, "_statusMessage", "隔离待开始"); Set(vm, "_audioDiagnosticStatus", "诊断: 未启动");
            Set(vm, "_audioPipelineLiveMetrics", ""); Set(vm, "_infoBarMessage", "");
            Set(vm, "_selectedNavTag", MainWindowViewModel.NavTagLive);
            Set(vm, "_floatingSubtitleKeys", new Dictionary<string, long>(StringComparer.Ordinal));
            Set(vm, "_floatingSubtitleFrames", new Dictionary<long, FloatingSubtitleUpdate>());
            Set(vm, "_floatingSourceFrames", new Dictionary<(long, VadGateController.ActiveSource), FloatingSubtitleUpdate>());
            Set(vm, "_floatingSubtitleKeyOrder", new Queue<(string?, long)>());
            vm.History = new ObservableCollection<TranslationItem>(); // 实际 History setter 挂 CollectionChanged。
            // 重建构造器里的四个命令，委托指向生产窄方法；没有伪造翻译状态/LatestRealtimeFrame。
            var start = Method(vm, "StartTranslation").CreateDelegate<Action>(vm);
            var stop = Method(vm, "StopTranslation").CreateDelegate<Action>(vm);
            SetCommand(vm, "StartTranslationCommand", new RelayCommand(_ => start(), _ => !vm.IsTranslating && config.IsValid()));
            SetCommand(vm, "StopTranslationCommand", new RelayCommand(_ => stop(), _ => vm.IsTranslating));
            SetCommand(vm, "ToggleTranslationCommand", new RelayCommand(_ => { if (vm.IsTranslating) stop(); else start(); }, _ => vm.IsTranslating || config.IsValid()));
            SetCommand(vm, "ClearHistoryCommand", new RelayCommand(_ => Method(vm, "ClearHistory").CreateDelegate<Action>(vm)(), _ => vm.History.Count > 0));
            foreach (var name in new[] { "OpenHistoryFolder", "ShowFloatingSubtitles", "ShowFloatingMicSubtitle", "ShowFloatingLoopbackSubtitle", "ShowFloatingInsight", "ToggleEditorType", "ShowMediaStudio", "CycleThemeMode" })
            {
                var action = Method(vm, name).CreateDelegate<Action>(vm);
                SetCommand(vm, name + "Command", new RelayCommand(_ => action()));
            }
            configVm = new ConfigViewModel(configService, new AzureSubscriptionValidator(), config,
                () => ((RelayCommand)vm.StartTranslationCommand).RaiseCanExecuteChanged(),
                _ => throw new InvalidOperationException("本专项禁止配置更新"), (_, _, _) => { }, () => false, () => window);
            configVm.UpdateSpeechResourceNames(); // 不 Load/SetConfig/订阅验证，不进行配置持久化。
            SetCommand(vm, "ConfigVM", configVm);
            var audio = new AudioDevicesViewModel(() => config, text => vm.StatusMessage = text, () => vm.IsTranslating,
                _ => throw new InvalidOperationException("本专项禁止音频配置更新"), () => false,
                _ => memorySaves++, (name, _) => { if (name == "DeviceRefreshSelect") deviceRefreshes++; }, () => window);
            SetCommand(vm, "AudioDevices", audio);
            // 模型解析明确返回未配置；凭据/云服务替身若被调用立即抛错并留痕。
            var forbidden = new List<string>();
            T Deny<T>() where T : class => RealtimeDenyProxy.Create<T>(name => { forbidden.Add(name); Log("FORBIDDEN=" + name); });
            var insight = new AiInsightViewModel(Deny<IAiInsightService>(), Deny<IAzureTokenProviderStore>(),
                new UnconfiguredModel(), () => config, () => vm.History, text => vm.StatusMessage = text, () => Task.CompletedTask);
            SetCommand(vm, "AiInsight", insight);
            SetCommand(vm, "Settings", RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel)));
            vm.ClearHistoryCommand.CanExecuteChanged += (_, _) => clearNotifications++;
            foreach (var command in new[] { vm.StartTranslationCommand, vm.StopTranslationCommand, vm.ToggleTranslationCommand })
                command.CanExecuteChanged += (_, _) => commandNotifications++;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.LatestRealtimeFrame)) frameNotifications++; };
            Expect(Field(vm, "_translationService").GetValue(vm) == null && factory.Creates == 0, "服务被提前注入，绕过工厂");
            Checkpoint();
            window = new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root, "appearance.json")))
            { Width = 1256, Height = 831, Title = "实时翻译链路 · 隔离回归", DataContext = vm };
            window.Show();
            await Pump(window, token);
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            var view = window.GetVisualDescendants().OfType<SongScrollTranslationView>().Single();
            var presentation = view.Presentation!;
            var button = view.FindControl<Button>("ScrollStart")!;
            button.AddHandler(Button.ClickEvent, (_, e) => { if (ReferenceEquals(e.Source, button)) clicks++; }, RoutingStrategies.Bubble, true);
            Expect(ReferenceEquals(presentation.Session, vm) && ReferenceEquals(button.Command, vm.SongScrollToggleCommand), "长卷按钮未挂生产 SongScrollToggleCommand/同一 MainVM");
            Expect(presentation.Conversation.Count == 0 && vm.History.Count == 0, "夹具初始非空");
            Checkpoint();

            async Task ClickAsync(string label, Button? target = null, bool deferCheckpoint = false)
            {
                stage = label;
                target ??= button;
                var command = ReferenceEquals(target, button) ? vm.SongScrollToggleCommand : vm.ToggleTranslationCommand;
                var before = clicks;
                Expect(target.IsEffectivelyVisible && target.IsEffectivelyEnabled && target.Command!.CanExecute(null), "真实按钮不可用");
                Expect(ReferenceEquals(target.Command, command), "真实按钮未绑定对应生产入口");
                if (!deferCheckpoint) Checkpoint();
                Expect(target.Focus(), "按钮未获得焦点");
                target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await Pump(window, token);
                Expect(clicks == before + 1, "OnClick 回执不是一次");
                Log($"BUTTON {label};target={target.Name};clicks={clicks};translating={vm.IsTranslating};connecting={vm.IsSongScrollConnecting};status={vm.StatusMessage};command={target.Command?.GetType().Name}");
                if (!deferCheckpoint) Checkpoint();
            }
            // UI 线程只等后台 Emit 返回（它只 Post、不等 UI），不 await 让出消息泵；有界等待避免夹具挂死。
            void QueueFromWorker(TranslationItem item, bool final)
            {
                Dispatcher.UIThread.VerifyAccess();
                Task.Run(() => service.Emit(item, final), token).WaitAsync(TimeSpan.FromSeconds(2), token).GetAwaiter().GetResult();
            }
            async Task RenderAsync(TranslationItem item, bool final, int count)
            {
                await Task.Run(() => service.Emit(item, final), token); // 真正通过已挂接的事件，从后台线程进入 UI Post。
                await Pump(window, token);
                Expect(vm.LatestRealtimeFrame is { } frame && frame.OriginalText == item.OriginalText
                    && frame.TranslatedText == item.TranslatedText && frame.IsFinal == final && frame.SegmentKey == item.SubtitleSegmentKey,
                    "事件未通过生产回调发布 LatestRealtimeFrame");
                Expect(presentation.Conversation.Count == count, "Conversation 丢失/重复句子");
                var card = presentation.Conversation.LastOrDefault();
                Expect(card != null && card.OriginalText == item.OriginalText && card.TranslatedText == item.TranslatedText, "实际投影正文不匹配");
                var reading = view.FindControl<ScrollViewer>("ScrollConversation")!;
                reading.ScrollToEnd();
                await Pump(window, token);
                foreach (var text in new[] { item.OriginalText, item.TranslatedText })
                {
                    var node = reading.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => ReferenceEquals(t.DataContext, card) && t.Text == text);
                    Expect(node is { IsEffectivelyVisible: true } && node.Bounds.Width > 0 && node.Bounds.Height > 0 && node.TextLayout.TextLines.Count > 0,
                        "真实 TextBlock 未呈现正文：" + text);
                    if (node != null)
                    {
                        var bounds = SongScrollTranslationView.TransformedBounds(node, reading);
                        Expect(bounds.Intersects(new Rect(reading.Bounds.Size)), "正文没有进入阅读视口");
                        Log($"TEXT final={final};text={node.Text};bounds={bounds};lines={node.TextLayout.TextLines.Count};type={node.GetType().Name}");
                    }
                }
                Log($"FRAME final={final};notifications={frameNotifications};history={vm.History.Count};conversation={presentation.Conversation.Count};clearNotifications={clearNotifications}");
                Checkpoint();
            }

            await ClickAsync("start");
            Expect(factory.Creates == 1 && service.Starts == 1 && vm.IsSongScrollConnecting && !configVm.IsConfigurationEnabled, "首次按钮没有经工厂进入连接态");
            Expect(service.HasProductionSubscribers(vm), "Attach 未挂六个真实生产回调");
            Checkpoint();
            service.CompleteStart();
            await Pump(window, token);
            Expect(vm.IsTranslating && !vm.IsSongScrollConnecting && !configVm.IsConfigurationEnabled && commandNotifications >= 3, "启动成功未完成 IsTranslating/AudioDevices/命令回调");
            Expect(view.FindControl<TextBlock>("ScrollStartLabel")!.Text == "停止翻译", "实际按钮标签未变为停止");
            Checkpoint();
            stage = "partial-final";
            var partial = Item("第一句正在翻译", "First partial.", "first", 0);
            await RenderAsync(partial, false, 1);
            Expect(vm.CurrentOriginal == partial.OriginalText && vm.CurrentTranslated == partial.TranslatedText && vm.History.Count == 0, "partial 没写主 VM，或提前写历史");
            var partialCard = presentation.Conversation.Single();
            var final = Item("第一句翻译完成", "First final.", "first", 0);
            await RenderAsync(final, true, 1);
            Expect(ReferenceEquals(presentation.Conversation.Single(), partialCard) && ReferenceEquals(vm.History.Single(), final)
                && clearNotifications == 1 && !insight.IsInsightLoading && !insight.IsAutoInsightEnabled, "final 合并/History/实际 AiInsight 回调未完成");
            service.EmitAuxiliary();
            await Pump(window, token);
            Expect(audio.AudioLevel == .625 && audio.AudioLevelHistory[^1] == .625 && vm.AudioDiagnosticStatus == "夹具诊断：无真实音频", "真实电平/诊断回调失败");
            Checkpoint();
            Log("PASS factory→Attach six delegates→background partial/final→LatestRealtimeFrame→Conversation→visible TextBlock; actual History/ClearHistoryCommand/AiInsight/AudioDevices callbacks completed");

            var frozenFrame = vm.LatestRealtimeFrame;
            var frozenOriginal = vm.CurrentOriginal;
            var frozenTranslated = vm.CurrentTranslated;
            var frameCount = frameNotifications;
            var clearCount = clearNotifications;
            var late = Item("停止后的迟到文字", "Late after stop.", "late", 1);
            // 不让 UI 泵先消费：排队后立即对真实按钮发 Enter，生产 stop 设置 ignore 后再消费。
            service.Emit(late, false); service.Emit(late, true);
            await ClickAsync("stop-reject-queued");
            Expect(!vm.IsTranslating && configVm.IsConfigurationEnabled && service.Stops == 1 && audio.AudioLevel == 0
                && audio.AudioLevelHistory.Count == 24 && audio.AudioLevelHistory.All(v => v == 0), "停止回调未完成");
            await Task.Run(() => { service.Emit(late, false); service.Emit(late, true); }, token);
            await Pump(window, token);
            Expect(ReferenceEquals(vm.LatestRealtimeFrame, frozenFrame) && vm.CurrentOriginal == frozenOriginal && vm.CurrentTranslated == frozenTranslated
                && vm.History.Count == 1 && presentation.Conversation.Count == 1 && frameNotifications == frameCount && clearNotifications == clearCount,
                "停止前排队/停止后迟到 partial 或 final 仍写入主 VM、历史或投影");
            Checkpoint();
            Log("PASS stop rejects queued and late partial/final; state/config/24 level samples reset; original frame/history retained");

            // 停止会话的结果在重启前已排队；不能在新 Start 清掉 ignore 后回填。
            QueueFromWorker(late, false); QueueFromWorker(late, true);
            await ClickAsync("restart");
            Expect(factory.Creates == 1 && factory.Resolves == 1 && service.Starts == 2 && service.HasProductionSubscribers(vm), "重启未复用服务或重复订阅");
            Expect(ReferenceEquals(vm.LatestRealtimeFrame, frozenFrame) && vm.CurrentOriginal == frozenOriginal && vm.CurrentTranslated == frozenTranslated
                && vm.History.Count == 1 && presentation.Conversation.Count == 1 && frameNotifications == frameCount && clearNotifications == clearCount,
                "重启后消费了停止会话原已排队的结果");
            Checkpoint();
            service.CompleteStart();
            await Pump(window, token);
            Expect(vm.IsTranslating && !vm.IsSongScrollConnecting, "停止后重启失败");
            stage = "restart-partial-final";
            await RenderAsync(Item("重启后的新句", "Restart partial.", "second", 2), false, 2);
            await RenderAsync(Item("重启后的终稿", "Restart final.", "second", 2), true, 2);
            Expect(vm.History.Select(i => i.SubtitleSegmentKey).SequenceEqual(new[] { "second", "first" }), "重启后历史次序错误");
            Checkpoint();
            Log("PASS restart rejects old queued partial/final across Start; same factory service, no duplicate delegates, new partial/final rendered");

            stage = "classic-roundtrip";
            window.ApplyShellAppearance(ShellAppearancePreference.Classic);
            await Pump(window, token);
            var classic = window.GetVisualDescendants().OfType<LiveTranslationView>().Single();
            Expect(ReferenceEquals(window.DataContext, vm) && ReferenceEquals(classic.DataContext, vm) && classic.IsEffectivelyVisible && vm.IsTranslating, "经典路由换掉主 VM 或停止业务");
            Expect(ReferenceEquals(classic.FindControl<Button>("TranslationToggleButton")!.Command, vm.ToggleTranslationCommand), "经典按钮不属于同一业务 VM");
            var classicItem = Item("经典主题仍接收同一事件", "Same VM while classic.", "third", 3);
            await Task.Run(() => service.Emit(classicItem, false), token);
            await Pump(window, token);
            Expect(classic.FindControl<ConfigurableTextEditor>("OriginalEditor")!.Text == classicItem.OriginalText
                && classic.FindControl<ConfigurableTextEditor>("TranslatedEditor")!.Text == classicItem.TranslatedText, "经典实际编辑器未接收主 VM 正文");
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            Expect(ReferenceEquals(window.GetVisualDescendants().OfType<SongScrollTranslationView>().Single(), view)
                && ReferenceEquals(view.Presentation, presentation) && ReferenceEquals(presentation.Session, vm) && vm.IsTranslating
                && service.Starts == 2 && service.Stops == 1 && factory.Creates == 1, "往返重建投影/主VM/服务或改变启停次数");
            await RenderAsync(Item("经典往返后的终稿", "Final after roundtrip.", "third", 3), true, 3);
            Log("PASS actual MainWindow Classic↔SongScroll route/cache; same MainVM/History/service/presentation; classic editors and returned scroll TextBlocks receive events");

            // 一组 A/B：通过真实旧按钮/新命令改变入口，绝不反射写 guard 或伪造翻译帧。
            Log("RESET_AB_SCOPE=同一当前 fake service/相同 partial-final payload；后台事件→生产回调 Post→消费前生产 ResetFloatingSubtitleSession→UI pump。");
            Log("RESET_AB_LIMIT=直接调用生产浮字幕 Reset，等价 UpdateTranslationConfigAsync 同连接器配置更新及 OnReconnectTriggered 的 reset 子路径；未实际执行配置更新/重连事件、厂商 Stop/Start 协议或云连接。正文/History/LatestFrame 不应依赖浮窗 epoch，clear/stop/restart 隔离仍须保留。");
            var resetSubtitle = Method(vm, "ResetFloatingSubtitleSession").CreateDelegate<Action>(vm);
            bool Guard() => (bool)Field(vm, "_songScrollFrameGuard").GetValue(vm)!; // 仅观测，由实际开始入口设置。
            long Epoch() => (long)Field(vm, "_floatingSubtitleEpoch").GetValue(vm)!;
            var resetPartial = Item("当前会话有效正文", "Valid active session text.", "active-reset", 5);
            var resetFinal = Item(resetPartial.OriginalText, resetPartial.TranslatedText, "active-reset", 5);
            async Task VerifyResetAsync(string arm, bool guarded, TranslationItem item, bool final)
            {
                stage = $"reset-{arm}-{(final ? "final" : "partial")}";
                var oldFrame = vm.LatestRealtimeFrame;
                var oldOriginal = vm.CurrentOriginal;
                var oldTranslated = vm.CurrentTranslated;
                var oldHistory = vm.History.ToArray();
                var oldFrames = frameNotifications;
                var oldClear = clearNotifications;
                var starts = service.Starts;
                var stops = service.Stops;
                var epoch = Epoch();
                Expect(vm.IsTranslating && !vm.IsSongScrollConnecting && Guard() == guarded && service.HasProductionSubscribers(vm)
                    && ReferenceEquals(Field(vm, "_translationService").GetValue(vm), service), "A/B 前提错误：非当前活跃服务或入口 guard 不符");
                QueueFromWorker(item, final);
                Expect(ReferenceEquals(vm.LatestRealtimeFrame, oldFrame) && frameNotifications == oldFrames
                    && vm.CurrentOriginal == oldOriginal && vm.CurrentTranslated == oldTranslated && vm.History.SequenceEqual(oldHistory), "Reset 前 UI 队列已被提前消费");
                resetSubtitle();
                Expect(Epoch() == epoch + 1 && ReferenceEquals(vm.LatestRealtimeFrame, oldFrame) && frameNotifications == oldFrames
                    && vm.CurrentOriginal == oldOriginal && vm.CurrentTranslated == oldTranslated && vm.History.SequenceEqual(oldHistory), "浮窗 Reset 自身改写正文/History/LatestFrame");
                Log($"RESET_AB_QUEUED arm={arm};final={final};frameguard={Guard()};epoch={epoch}->{Epoch()};currentService={ReferenceEquals(Field(vm, "_translationService").GetValue(vm), service)};uiConsumed=false;starts={starts};stops={stops}");
                await Pump(window, token);
                var bodyOk = vm.CurrentOriginal == item.OriginalText && vm.CurrentTranslated == item.TranslatedText;
                var historyOk = vm.History.SequenceEqual(final ? new[] { item }.Concat(oldHistory) : oldHistory);
                var frameOk = vm.LatestRealtimeFrame is { } frame && !ReferenceEquals(frame, oldFrame)
                    && frame.OriginalText == item.OriginalText && frame.TranslatedText == item.TranslatedText
                    && frame.SegmentKey == item.SubtitleSegmentKey && frame.IsFinal == final && frameNotifications == oldFrames + 1;
                var card = presentation.Conversation.LastOrDefault();
                var projectionOk = card != null && card.SegmentKey == item.SubtitleSegmentKey
                    && card.OriginalText == item.OriginalText && card.TranslatedText == item.TranslatedText;
                var textOk = guarded
                    ? view.FindControl<ScrollViewer>("ScrollConversation")!.GetVisualDescendants().OfType<TextBlock>()
                        .Count(t => ReferenceEquals(t.DataContext, card) && t.IsEffectivelyVisible && t.Bounds.Width > 0 && t.Bounds.Height > 0
                            && (t.Text == item.OriginalText || t.Text == item.TranslatedText)) >= 2
                    : classic.FindControl<ConfigurableTextEditor>("OriginalEditor")!.Text == item.OriginalText
                        && classic.FindControl<ConfigurableTextEditor>("TranslatedEditor")!.Text == item.TranslatedText;
                Log($"RESET_AB_RESULT arm={arm};final={final};body={bodyOk};history={historyOk};latestFrame={frameOk};projection={projectionOk};visibleText={textOk};frames={oldFrames}->{frameNotifications};historyCount={oldHistory.Length}->{vm.History.Count};original={vm.CurrentOriginal};translated={vm.CurrentTranslated};latestKey={vm.LatestRealtimeFrame?.SegmentKey ?? "null"}");
                Expect(bodyOk, "有效正文被浮窗 epoch Reset 丢弃");
                Expect(historyOk && clearNotifications == oldClear + (final ? 1 : 0), "有效 final 的 History/清除命令回调被浮窗 Reset 丢弃");
                Expect(frameOk, "有效 LatestRealtimeFrame 被浮窗 epoch Reset 丢弃");
                Expect(projectionOk && textOk, "有效正文未呈现在对应主题的实际控件");
                Expect(vm.IsTranslating && service.Starts == starts && service.Stops == stops && Guard() == guarded
                    && ReferenceEquals(Field(vm, "_translationService").GetValue(vm), service), "仅浮窗 Reset 意外切换服务/启停/guard");
                Expect(Field(vm, "_latestFloatingSubtitleUpdate").GetValue(vm) == null, "旧浮窗 epoch 的结果回填了浮窗快照");
                // 不在这里 Checkpoint：保留 partial/final 两个失败的完整证据，并执行最终停止/Detach。
            }
            await ClickAsync("reset-ab-stop-existing");
            window.ApplyShellAppearance(ShellAppearancePreference.Classic);
            await Pump(window, token);
            var classicButton = classic.FindControl<Button>("TranslationToggleButton")!;
            classicButton.AddHandler(Button.ClickEvent, (_, e) => { if (ReferenceEquals(e.Source, classicButton)) clicks++; }, RoutingStrategies.Bubble, true);
            await ClickAsync("reset-A-classic-start", classicButton);
            service.CompleteStart();
            await Pump(window, token);
            Expect(vm.IsTranslating && !Guard() && service.Starts == 3 && service.Stops == 2, "A 未通过旧开始按钮进入 frameguard=false");
            Checkpoint();
            await VerifyResetAsync("A-classic", false, resetPartial, false);
            await VerifyResetAsync("A-classic", false, resetFinal, true);
            Checkpoint();
            Log("PASS RESET_A classic real start→active emit→floating Reset→pump; body/History/LatestFrame/editors updated with frameguard=false");
            await ClickAsync("reset-A-classic-stop", classicButton);
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            await ClickAsync("reset-B-songscroll-start");
            service.CompleteStart();
            await Pump(window, token);
            Expect(vm.IsTranslating && Guard() && service.Starts == 4 && service.Stops == 3 && factory.Creates == 1, "B 未通过新开始命令进入 frameguard=true/同一服务");
            Checkpoint();

            stage = "clear-reject-queued";
            var queuedBeforeClear = Item("清除前已排队的旧结果", "Queued before clear.", "before-clear", 6);
            QueueFromWorker(queuedBeforeClear, false); QueueFromWorker(queuedBeforeClear, true);
            var beforeClearFrames = frameNotifications;
            var beforeClearNotifications = clearNotifications;
            Expect(vm.ClearHistoryCommand.CanExecute(null), "清除命令未启用");
            vm.ClearHistoryCommand.Execute(null);
            await Pump(window, token);
            Expect(vm.CurrentOriginal == "" && vm.CurrentTranslated == "" && vm.History.Count == 0 && vm.LatestRealtimeFrame == null
                && presentation.Conversation.Count == 0 && frameNotifications == beforeClearFrames + 1 && clearNotifications == beforeClearNotifications + 1,
                "clearHistory 期间原已排队的 partial/final 回填正文/History/LatestFrame/投影");
            Checkpoint();
            Log("PASS clearHistory rejects previously queued partial/final while same SongScroll session remains active");
            await RenderAsync(Item("清除后新事件正常显示", "New event after clear.", "after-clear", 7), false, 1);
            await RenderAsync(Item("清除后新终稿正常显示", "New final after clear.", "after-clear", 7), true, 1);
            await VerifyResetAsync("B-songscroll", true, resetPartial, false);
            await VerifyResetAsync("B-songscroll", true, resetFinal, true);
            var finalClearCount = clearNotifications;
            await ClickAsync("final-stop", deferCheckpoint: true);
            await UntilAsync(() => deviceRefreshes > 0, token);
            Expect(service.Stops == 4 && !vm.IsTranslating && clearNotifications == finalClearCount && forbidden.Count == 0, "最终停止/回调计数/禁用依赖门禁失败");
            Expect(!File.Exists(configService.GetConfigFilePath()) && !File.Exists(configService.GetConfigFilePath() + ".bak"), "意外保存配置");
            window.Close();
            Expect(service.SubscriberCount == 0, "关闭测试窗未走生产 Detach 解除六个事件");
            Log($"COUNTS creates={factory.Creates};resolves={factory.Resolves};starts={service.Starts};stops={service.Stops};buttonClicks={clicks};frames={frameNotifications};history={vm.History.Count};clear={clearNotifications};commands={commandNotifications};deviceRefresh={deviceRefreshes};memorySaveRequests={memorySaves};forbidden={forbidden.Count};remainingSubscribers={service.SubscriberCount}");
            Log("UNCOVERED=真实用户 MainVM 构造/配置/DB/云/设备采集/VAD/识别协议/自动洞察生成/物理输入/高DPI；本专项通过不能证明用户监听不出字的现场已修复。");
            Checkpoint();
            passed = true;
        }
        catch (Exception ex) { Log($"FAIL_STAGE={stage};{ex}"); throw; }
        finally
        {
            try
            {
                window?.Close();
                (configVm == null ? null : Field(configVm, "_subscriptionLampTimer").GetValue(configVm) as DispatcherTimer)?.Stop();
                (main == null ? null : Field(main, "_infoBarCts").GetValue(main) as CancellationTokenSource)?.Cancel();
                if (window == null) main?.Dispose();
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = appTheme;
                Dispatcher.UIThread.UnhandledException -= OnUnhandled;
                Log("SONG_SCROLL_REALTIME_INTERACTION_EXIT=" + (passed ? 0 : 1));
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
            }
        }
    }

    private static TranslationItem Item(string original, string translated, string key, int seconds) => new()
    { OriginalText = original, TranslatedText = translated, SubtitleSegmentKey = key, Timestamp = new DateTime(2026, 11, 5, 20, 28, 20).AddSeconds(seconds), Source = VadGateController.ActiveSource.Mic };
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, Private) ?? throw new MissingFieldException(value.GetType().Name, name);
    private static MethodInfo Method(object value, string name) => value.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(value.GetType().Name, name);
    private static void Set(object value, string name, object data) => Field(value, name).SetValue(value, data);
    private static void SetCommand(object value, string name, object data) => Set(value, "<" + name + ">k__BackingField", data);
    private static async Task Pump(Window window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window) ?? throw new InvalidOperationException("测试窗没有真实合成器");
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(token), DispatcherPriority.Background);
    }
    private static async Task UntilAsync(Func<bool> ready, CancellationToken token)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3)); while (!ready()) await Task.Delay(20, timeout.Token); }

    private sealed class EventTranslation(Action<string> log) : IRealtimeTranslationService
    {
        public RealtimeConnectorFamily ConnectorFamily => RealtimeConnectorFamily.Custom;
        public int Starts, Stops;
        private TaskCompletionSource<bool>? _start;
        public event EventHandler<TranslationItem>? OnRealtimeTranslationReceived;
        public event EventHandler<TranslationItem>? OnFinalTranslationReceived;
        public event EventHandler<string>? OnStatusChanged;
        public event EventHandler<string>? OnReconnectTriggered;
        public event EventHandler<double>? OnAudioLevelUpdated;
        public event EventHandler<string>? OnDiagnosticsUpdated;
        private Delegate[] Handlers => new Delegate?[] { OnRealtimeTranslationReceived, OnFinalTranslationReceived, OnStatusChanged, OnReconnectTriggered, OnAudioLevelUpdated, OnDiagnosticsUpdated }
            .Where(d => d != null).SelectMany(d => d!.GetInvocationList()).ToArray();
        public int SubscriberCount => Handlers.Length;
        public bool HasProductionSubscribers(MainWindowViewModel vm) => Handlers.Length == 6 && Handlers.All(d => ReferenceEquals(d.Target, vm))
            && Handlers.Select(d => d.Method.Name).Order().SequenceEqual(new[] { "OnRealtimeTranslationReceived", "OnFinalTranslationReceived", "OnStatusChanged", "OnReconnectTriggered", "OnAudioLevelUpdated", "OnDiagnosticsUpdated" }.Order());
        public Task<bool> StartTranslationAsync()
        { Starts++; log($"SERVICE Start #{Starts};subscribers={string.Join(",", Handlers.Select(d => d.Method.Name))}"); _start = new(TaskCreationOptions.RunContinuationsAsynchronously); return _start.Task; }
        public void CompleteStart() => (_start ?? throw new InvalidOperationException("Start 未进入")).SetResult(true);
        public Task StopTranslationAsync() { Stops++; log($"SERVICE Stop #{Stops}"); return Task.CompletedTask; }
        public void Emit(TranslationItem item, bool final)
        { if (final) OnFinalTranslationReceived?.Invoke(this, item); else OnRealtimeTranslationReceived?.Invoke(this, item); }
        public void EmitAuxiliary() { OnStatusChanged?.Invoke(this, "夹具服务监听中"); OnDiagnosticsUpdated?.Invoke(this, "夹具诊断：无真实音频"); OnAudioLevelUpdated?.Invoke(this, .625); }
        public Task UpdateConfigAsync(AzureSpeechConfig config) => throw new InvalidOperationException("本专项不更新服务配置");
        public bool TryApplyLiveAudioRoutingFromCurrentConfig(int fadeMilliseconds = 30) => throw new InvalidOperationException("本专项不应用真实音频路由");
    }
    private sealed class EventFactory(EventTranslation service, Action<string> log) : IRealtimeTranslationServiceFactory
    {
        public int Creates, Resolves;
        public bool TryResolveConnectorFamily(AzureSpeechConfig config, out RealtimeConnectorFamily family, out string error)
        { Resolves++; family = RealtimeConnectorFamily.Custom; error = ""; return true; }
        public bool TryCreate(AzureSpeechConfig config, Action<string>? audit, out IRealtimeTranslationService? result, out string error)
        { Creates++; log("FACTORY TryCreate via " + new StackTrace(1, false).ToString().Replace(Environment.NewLine, " <- ")); result = service; error = ""; return true; }
        public Task<RealtimeProbeResult> ProbeThirdPartyRealtimeAsync(AzureSpeechConfig config, SpeechResource resource, CancellationToken token) => throw new InvalidOperationException("禁止探活");
        public Task<RealtimeProbeResult> ProbeThirdPartyTranslationAsync(AzureSpeechConfig config, SpeechResource resource, CancellationToken token) => throw new InvalidOperationException("禁止探活");
    }
    private sealed class UnconfiguredModel : IModelRuntimeResolver
    {
        public bool TryResolve(AzureSpeechConfig config, ModelReference? reference, ModelCapability capability, out ModelRuntimeResolution? runtime, out string errorMessage)
        { runtime = null; errorMessage = "隔离夹具未配置模型，禁止云请求"; return false; }
    }
}

public class RealtimeDenyProxy : DispatchProxy
{
    private Action<string> _denied = null!;
    internal static T Create<T>(Action<string> denied) where T : class
    { var proxy = Create<T, RealtimeDenyProxy>(); ((RealtimeDenyProxy)(object)proxy)._denied = denied; return proxy; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    { var name = targetMethod?.DeclaringType?.Name + "." + targetMethod?.Name; _denied(name); throw new InvalidOperationException("隔离回归禁止调用 " + name); }
}