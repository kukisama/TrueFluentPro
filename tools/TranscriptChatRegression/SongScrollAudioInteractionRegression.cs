using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using TrueFluentPro;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

// 真实 MainWindow 双缓存/共享 VM/合法 WAV 的窄交互诊断；只加载，不播放或生成。
internal static class SongScrollAudioInteractionRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string SummaryText = "总结夹具正文：三百零九段已准备。";
    private const string PodcastText = "发言人 A：播客夹具台本。";
    private static readonly (AudioLabTabKind Tab, string Button, string Heading)[] Cases =
    {
        (AudioLabTabKind.Summary, "总结", "音频总结"),
        (AudioLabTabKind.Transcript, "转录", "转录全文"),
        (AudioLabTabKind.MindMap, "导图", "思维导图"),
        (AudioLabTabKind.Podcast, "播客", "播客台本")
    };

    internal static async Task VerifyAsync()
    {
        Check(OperatingSystem.IsWindows(), "需要 Windows 原生输入验收，不能静默降级。");
        var repo = Directory.GetCurrentDirectory();
        Check(File.Exists(Path.Combine(repo, "TrueFluentPro.csproj")), "请从仓库根运行。");
        var root = Path.Combine(repo, "artifacts", "transcript-chat-regression",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-audio-interaction-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var report = new List<string>();
        var failures = new List<string>();
        var clock = Stopwatch.StartNew();
        void Log(string text)
        {
            var line = $"{clock.Elapsed.TotalMilliseconds:F1}ms {text}";
            report.Add(line);
            Console.WriteLine(line);
        }
        void Expect(bool value, string text)
        {
            if (!value) { failures.Add(text); Log("FAIL " + text); }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(38));
        var token = deadline.Token; // Program 的既有总 deadline 为45秒，不改其他分派行为。
        AudioLabViewModel? vm = null;
        MainWindow? window = null;
        var windowOwnsVm = false;
        var stage = "setup";
        var forbidden = new List<string>();
        var memoryReads = 0;
        var selections = 0;
        var selectedFileChanges = 0;
        var loadCalls = 0;
        var maxLoadDepth = 0;
        var playbackStarts = 0;
        var generationStarts = 0;
        var selectedTabChanges = new List<AudioLabTabKind>();
        try
        {
            Log("ARTIFACTS=" + root);
            Log($"ASSEMBLY={typeof(AudioLabViewModel).Assembly.Location};MVID={typeof(AudioLabViewModel).Module.ModuleVersionId};Avalonia={typeof(Application).Assembly.GetName().Version}");
            Log("SCOPE=真实MainWindow/ApplyShellAppearance/ShowPage双缓存 + 共享真实AudioLabVM/ControlPanel/Pipeline/播放器；合法静音WAV经页格式检查→LoadAudioFile恢复309段；不构造MainVM、不访问App.Services/用户配置/用户DB，不启动播放/录音/生成/云。");
            Log("LIMIT=仅ConfigurationService使用GetUninitializedObject（普通构造触碰用户PathManager），两个readonly路径均指向本次artifacts；不反射种私有Session，不改生产或Program。未接后台队列/真实任务事件/用户现场数据。");

            T Deny<T>() where T : class => SongScrollAudioDenyProxy.Create<T>(name =>
            { forbidden.Add(name); Log("FORBIDDEN=" + name); });
            var audioPath = Path.Combine(root, "安全夹具309段.wav");
            using (var writer = new WaveFileWriter(audioPath, new WaveFormat(16000, 16, 1)))
                writer.Write(new byte[32000], 0, 32000); // 一秒 PCM 静音，只制作夹具。
            using (var reader = new WaveFileReader(audioPath))
                Check(reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm
                    && reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.BitsPerSample == 16
                    && reader.WaveFormat.Channels == 1 && reader.Length == 32000
                    && reader.TotalTime == TimeSpan.FromSeconds(1), "WAV夹具格式/长度无效。");
            Check(AudioFormatConverter.IsMicrosoftSpeechSupportedFormat(audioPath, out _), "合法WAV不应走格式转换。");
            Log($"WAV path={audioPath};bytes={new FileInfo(audioPath).Length};pcm=16000/16/mono;duration=1s;silent=true");
            var raw = new RawTranscriptionData
            {
                ApiType = "fast",
                RawResponse = JsonSerializer.Serialize(new
                {
                    phrases = Enumerable.Range(1, 309).Select(i => new
                    {
                        text = $"转录夹具第{i:D3}段。", speaker = i % 2,
                        offsetMilliseconds = i * 4000L, durationMilliseconds = 1000L
                    })
                })
            };
            AudioItemRecord? audioItem = null;
            var library = SongScrollAudioDenyProxy.Create<IAudioLibraryRepository>(name =>
            { forbidden.Add(name); Log("FORBIDDEN=" + name); }, (method, args) =>
            {
                if (method.Name == nameof(IAudioLibraryRepository.GetByFilePath) && Equals(args?[0], audioPath))
                {
                    // 每个真实LoadAudioFile都会经过EnsureAudioItem→GetByFilePath；不插桩生产代码。
                    var stack = new StackTrace(1, false);
                    var depth = stack.GetFrames().Count(f => f.GetMethod() is { } m
                        && m.DeclaringType == typeof(AudioLabViewModel) && m.Name == nameof(AudioLabViewModel.LoadAudioFile));
                    if (depth > 0)
                    {
                        loadCalls++;
                        maxLoadDepth = Math.Max(maxLoadDepth, depth);
                        Log($"LOAD_AUDIO_FILE count={loadCalls};depth={depth};stack={stack.ToString().Replace(Environment.NewLine, " <- ")}");
                    }
                    return (true, audioItem);
                }
                if (method.Name == nameof(IAudioLibraryRepository.Upsert) && args?[0] is AudioItemRecord item
                    && item.FilePath == audioPath)
                {
                    audioItem = item;
                    Log($"MEMORY_UPSERT AudioItem;size={item.FileSize}");
                    return (true, null);
                }
                return (false, null);
            });
            var cache = new Dictionary<string, AudioLifecycleRecord>(StringComparer.Ordinal);
            var lifecycle = SongScrollAudioDenyProxy.Create<IAudioLifecycleRepository>(name =>
            { forbidden.Add(name); Log("FORBIDDEN=" + name); }, (method, args) =>
            {
                if (method.Name == nameof(IAudioLifecycleRepository.Upsert)
                    && args?[0] is AudioLifecycleRecord record && record.AudioItemId == audioItem?.Id)
                {
                    cache[record.Stage] = record;
                    Log("MEMORY_UPSERT " + record.Stage);
                    return (true, null);
                }
                if (!Equals(args?[0], audioItem?.Id)) return (false, null);
                if (method.Name == nameof(IAudioLifecycleRepository.Get) && args?[1] is string key)
                {
                    if (key == "Transcribed") memoryReads++;
                    Log($"MEMORY_GET {key};hit={cache.ContainsKey(key)}");
                    return (true, cache.GetValueOrDefault(key));
                }
                if (method.Name == nameof(IAudioLifecycleRepository.GetAllStages))
                    return (true, cache.Values.ToList());
                return (false, null);
            });
            var config = new AzureSpeechConfig
            {
                AudioLabFilePanelOpen = true, EnableBatchSubtitleSentenceSplit = false,
                AudioLabStagePresets = AudioLabStagePresetDefaults.CreateDefaults()
            };
            // 本专项验证这四个已显示的标签，不依赖/读取用户的预设。
            foreach (var preset in config.AudioLabStagePresets)
                if (preset.Stage is "Summarized" or "MindMap" or "PodcastScript")
                { preset.IsEnabled = true; preset.ShowInTab = true; }
            var configService = (ConfigurationService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigurationService));
            Set(configService, "_configFilePath", Path.Combine(root, "never-read-or-saved.json"));
            Set(configService, "_backupConfigFilePath", Path.Combine(root, "never-read-or-saved.json.bak"));
            var ai = Deny<IAiInsightService>();
            var tokens = Deny<IAzureTokenProviderStore>();
            var tts = new SpeechSynthesisService(); // 只构造HttpClient；没有任何TTS调用/语音加载。
            var pipeline = new AudioLifecyclePipelineService(library, lifecycle, ai, tts);
            var audioId = pipeline.EnsureAudioItem(audioPath); // 唯一音频库记录也经真实管线在内存创建。
            pipeline.SaveStageContent(audioId, AudioLifecycleStage.Transcribed, TranscriptionDataHelper.SerializeRawData(raw));
            pipeline.SaveStageContent(audioId, AudioLifecycleStage.Summarized, SummaryText);
            pipeline.SaveStageContent(audioId, AudioLifecycleStage.MindMap,
                "{\"title\":\"导图夹具根\",\"children\":[{\"title\":\"导图夹具子节点\"}]}");
            pipeline.SaveStageContent(audioId, AudioLifecycleStage.PodcastScript, PodcastText);
            Check(cache.Count == 4 && cache.Values.All(r => !r.IsStale), "加载前四阶段缓存必须完整就绪。");
            var panel = new AudioLabControlPanelViewModel(pipeline, tts, () => config, tokens);
            vm = new AudioLabViewModel(ai, tokens, Deny<IModelRuntimeResolver>(),
                Deny<ISpeechResourceRuntimeResolver>(), Deny<IAiAudioTranscriptionService>(), () => config,
                configService, pipeline, panel, Deny<IAudioLabExportService>(), Deny<IDesktopFolderLauncher>());
            var business = vm;
            foreach (var playback in new[] { vm.Playback, vm.PodcastPlayback })
                playback.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(playback.IsPlaying) && playback.IsPlaying)
                    { playbackStarts++; Check(false, "隔离测试意外启动播放。"); }
                };

            vm.PropertyChanging += (_, e) =>
            {
                if (e.PropertyName is nameof(vm.SelectedTab) or nameof(vm.SelectedAudioFile))
                    Log($"VM_CHANGING {e.PropertyName};tab={business.SelectedTab};stack={new StackTrace(1, false).ToString().Replace(Environment.NewLine, " <- ")}");
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.SelectedTab)) selectedTabChanges.Add(business.SelectedTab);
                if (e.PropertyName == nameof(vm.SelectedAudioFile)) selectedFileChanges++;
                if (e.PropertyName is nameof(vm.SelectedTab) or nameof(vm.SelectedAudioFile) or nameof(vm.StatusMessage))
                    Log($"VM_CHANGED {e.PropertyName};tab={business.SelectedTab};status={business.StatusMessage};file={business.SelectedAudioFile?.Name}");
                if (e.PropertyName == nameof(vm.IsGenerating) && business.IsGenerating)
                { generationStarts++; Check(false, "隔离测试意外启动生成。"); }
            };
            window = new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root, "never-read-appearance.json")))
            {
                Title = "听析标签交互 · 主窗双缓存（只加载静音夹具）", Width = 1256, Height = 831,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            Set(window, "_audioLabBusiness", vm);
            Set(window, "_audioLabInitialized", true);
            windowOwnsVm = true;
            var pages = (Dictionary<string, Control>)Field(window, "_pageCache").GetValue(window)!;
            void ShowAudio() => typeof(MainWindow).GetMethod("ShowPage", Private)!
                .Invoke(window, new object[] { MainWindowViewModel.NavTagAudioLab });
            // 先创建两棵真实缓存树，再真实加载。旧页首次创建后再路由一次走BindBusinessPage。
            window.ApplyShellAppearance(ShellAppearancePreference.Classic);
            ShowAudio();
            ShowAudio();
            var classic = (AudioLabView)pages[MainWindowViewModel.NavTagAudioLab];
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            ShowAudio();
            var scroll = (SongScrollAudioLabView)pages["song-scroll:" + MainWindowViewModel.NavTagAudioLab];
            Check(ReferenceEquals(classic.ViewModel, vm) && ReferenceEquals(scroll.ViewModel, vm)
                && pages.Count == 2 && !ReferenceEquals(classic, scroll), "主窗双缓存未绑定同一个真实VM。");
            Check(window.DataContext == null, "本专项不构造MainVM。");
            var screen = window.Screens.Primary ?? window.Screens.All.First();
            window.Width = Math.Min(window.Width, screen.WorkingArea.Width / screen.Scaling - 32);
            window.Height = Math.Min(window.Height, screen.WorkingArea.Height / screen.Scaling - 64);
            window.Position = new PixelPoint(screen.WorkingArea.X + 16, screen.WorkingArea.Y + 16);
            window.Show();
            await Pump(window, token);
            // 官方11.3.11 Layoutable.MeasureCore仅在IsVisible时ApplyTemplate。
            // 两页均须真正呈现一次，不能读取从未布局的隐藏旧页的空视觉树。
            window.ApplyShellAppearance(ShellAppearancePreference.Classic);
            ShowAudio();
            await Pump(window, token);
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            ShowAudio();
            await Pump(window, token);
            var fileLists = new Dictionary<AudioLabView, ListBox>();
            var allTabs = new Dictionary<AudioLabView, Dictionary<AudioLabTabKind, RadioButton>>();
            var allClicks = new Dictionary<AudioLabView, Dictionary<AudioLabTabKind, int>>();
            foreach (var view in new AudioLabView[] { classic, scroll })
            {
                var listForView = view.GetVisualDescendants().OfType<ListBox>()
                    .Single(l => ReferenceEquals(l.ItemsSource, vm.AudioFiles));
                fileLists.Add(view, listForView);
                listForView.SelectionChanged += (_, e) =>
                {
                    selections++;
                    Log($"FILE_SELECTION view={view.GetType().Name};count={selections};added={e.AddedItems.Count};removed={e.RemovedItems.Count};stack={new StackTrace(1, false).ToString().Replace(Environment.NewLine, " <- ")}");
                };
                var viewTabs = Cases.ToDictionary(c => c.Tab, c => view.GetVisualDescendants().OfType<RadioButton>()
                    .Single(b => (b.Classes.Contains("lab-tab") || b.Classes.Contains("tab-button"))
                        && (Equals(b.Content, c.Button) || b.Content is Control content
                            && content.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == c.Button))));
                allTabs.Add(view, viewTabs);
                var viewClicks = Cases.ToDictionary(c => c.Tab, _ => 0);
                allClicks.Add(view, viewClicks);
                foreach (var entry in viewTabs)
                {
                    entry.Value.AddHandler(Button.ClickEvent, (_, e) =>
                    {
                        if (!ReferenceEquals(e.Source, entry.Value)) return;
                        viewClicks[entry.Key]++;
                        Log($"NATIVE_CLICK view={view.GetType().Name};{entry.Key};count={viewClicks[entry.Key]};tab={business.SelectedTab};checked={entry.Value.IsChecked};handled={e.Handled}");
                    }, RoutingStrategies.Bubble, handledEventsToo: true);
                    entry.Value.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == ToggleButton.IsCheckedProperty)
                            Log($"RADIO_CHANGED view={view.GetType().Name};{entry.Key};checked={entry.Value.IsChecked};vm={business.SelectedTab};binding={HasCheckedBinding(entry.Value)}");
                    };
                }
            }
            stage = "real-file-load";
            await classic.LoadAudioFileWithFormatCheckAsync(audioPath);
            await Pump(window, token);
            var file = vm.SelectedAudioFile ?? throw new InvalidOperationException("真实加载未设置文件选择。");
            var session = Field(vm, "_activeSession").GetValue(vm);
            var mindMap = vm.MindMapRoot ?? throw new InvalidOperationException("导图缓存未恢复。");
            Check(vm.CurrentFilePath == audioPath && file.FullPath == audioPath && vm.AudioFiles.Count == 1
                && session != null && ((IDictionary)Field(vm, "_sessions").GetValue(vm)!).Count == 1
                && vm.Segments.Count == 309 && vm.SummaryMarkdown == SummaryText && vm.PodcastMarkdown == PodcastText
                && vm.TranscribeState == StageContentState.Ready && vm.SummaryState == StageContentState.Ready
                && vm.MindMapState == StageContentState.Ready && vm.PodcastState == StageContentState.Ready
                && panel.HasTranscription && panel.HasSummary && panel.HasMindMap && panel.HasPodcastScript,
                "真实LoadAudioFile未从内存缓存恢复完整就绪状态。");
            Check(loadCalls == 1 && maxLoadDepth == 1, "初次真实加载已发生额外LoadAudioFile/递归重入。");
            Check(vm.Playback.IsPlayEnabled && vm.Playback.IsStopEnabled
                && Math.Abs(vm.Playback.Duration.TotalSeconds - 1) < .02, "真实播放器没有成功加载一秒WAV。");
            var initialSelections = selections;
            var initialFileChanges = selectedFileChanges;
            Log($"LOAD_BASELINE calls={loadCalls};depth={maxLoadDepth};fileChanges={initialFileChanges};selections={initialSelections};segments={vm.Segments.Count};duration={vm.Playback.Duration};ready={vm.Playback.IsPlayEnabled}");
            // 两页均已绑定并加载同一真实文件后，再主题往返，不直接挂载/替换Content。
            window.ApplyShellAppearance(ShellAppearancePreference.Classic);
            ShowAudio();
            await Pump(window, token);
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            ShowAudio();
            await Pump(window, token);
            AudioLabView page = scroll;
            var tabs = allTabs[page];
            var clickCounts = allClicks[page];
            var list = fileLists[page];
            var transcript = page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TranscriptListBox");
            Dictionary<AudioLabTabKind, Control> Bodies(AudioLabView view)
            {
                if (view is SongScrollAudioLabView)
                    return Cases.ToDictionary(c => c.Tab, c => (Control)view.GetVisualDescendants().OfType<TextBlock>()
                        .Single(t => t.Classes.Contains("scroll-section-title") && t.Text == c.Heading)
                        .GetVisualAncestors().OfType<Grid>().First(g => g.RowDefinitions.Count > 0));
                // 旧XAML的正文是同一Panel中的固定内置子项。隐藏正文的模板可能尚未生成，
                // 以已命名转录列表的逻辑父级定位，不依赖Markdown/导图的隐藏视觉子树。
                var legacyTranscript = view.FindControl<ListBox>("TranscriptListBox")!;
                var transcriptBody = legacyTranscript.Parent as DockPanel
                    ?? throw new InvalidOperationException("旧页转录逻辑容器不符。");
                var content = transcriptBody.Parent as Panel
                    ?? throw new InvalidOperationException("旧页标签逻辑容器不符。");
                Check(content.Children.Count == 9 && ReferenceEquals(content.Children[2], transcriptBody)
                    && content.Children[1] is ScrollViewer && content.Children[3] is Panel
                    && content.Children[6] is ScrollViewer, "旧页内置正文结构与生产XAML不符。");
                return new()
                {
                    [AudioLabTabKind.Summary] = content.Children[1],
                    [AudioLabTabKind.Transcript] = transcriptBody,
                    [AudioLabTabKind.MindMap] = content.Children[3],
                    [AudioLabTabKind.Podcast] = content.Children[6]
                };
            }
            var bodies = Bodies(page);
            Log($"WINDOW type={window.GetType().FullName};hwnd={window.TryGetPlatformHandle()?.Handle};client={window.ClientSize};scale={window.RenderScaling};mainVM=false;sharedVM=true");

            void Snapshot(string label)
            {
                Log($"SNAPSHOT {label};page={page.GetType().Name};vmTab={business.SelectedTab};status={business.StatusMessage};segments={business.Segments.Count};selectedFileChanges={selectedFileChanges};fileSelections={selections};loadCalls={loadCalls};maxLoadDepth={maxLoadDepth};reads={memoryReads}");
                foreach (var view in new AudioLabView[] { classic, scroll })
                    Log($"CACHE view={view.GetType().Name};visible={view.IsEffectivelyVisible};vmSame={ReferenceEquals(view.ViewModel, business)};fileSame={ReferenceEquals(fileLists[view].SelectedItem, file)};radios={string.Join(",", allTabs[view].Select(t => $"{t.Key}:{t.Value.IsChecked}/{HasCheckedBinding(t.Value)}"))}");
                foreach (var c in Cases)
                {
                    var b = tabs[c.Tab]; var body = bodies[c.Tab];
                    Log($"TREE {c.Tab};checked={b.IsChecked};binding={HasCheckedBinding(b)};radioVisible={b.IsEffectivelyVisible};bodyVisible={body.IsVisible};effective={body.IsEffectivelyVisible};bounds={body.Bounds};contextSame={ReferenceEquals(body.DataContext, business)}");
                }
                Log($"TRANSCRIPT visible={transcript.IsEffectivelyVisible};bounds={transcript.Bounds};items={transcript.ItemCount};sourceSame={ReferenceEquals(transcript.ItemsSource, business.Segments)};realized={transcript.GetVisualDescendants().OfType<ListBoxItem>().Count()}");
                foreach (var text in page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible
                    && (t.Classes.Contains("scroll-section-title") || (t.Text?.Contains("夹具") ?? false) || t.Text == business.StatusMessage)).Take(30))
                    Log($"VISIBLE_TEXT text={text.Text};bounds={text.Bounds};glyphs={text.TextLayout.TextLines.Count}");
                foreach (var text in page.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible))
                    Log($"VISIBLE_MARKDOWN text={text.Text};runs={string.Join("", text.Inlines?.OfType<Run>().Select(r => r.Text) ?? Array.Empty<string>())};bounds={text.Bounds}");
            }
            void VerifyBody(AudioLabTabKind expected, string label)
            {
                Expect(business.SelectedTab == expected, label + $": SelectedTab={business.SelectedTab}, expected={expected}");
                foreach (var c in Cases)
                {
                    Expect(bodies[c.Tab].IsEffectivelyVisible == (c.Tab == expected), label + $": {c.Tab}正文可见性不正确");
                    Expect(tabs[c.Tab].IsChecked == (c.Tab == expected), label + $": {c.Tab}勾选状态不正确");
                    Expect(HasCheckedBinding(tabs[c.Tab]), label + $": {c.Tab} IsChecked绑定已消失");
                }
                Expect(ReferenceEquals(list.SelectedItem, file) && ReferenceEquals(business.SelectedAudioFile, file), label + ": 文件选择发生变化");
                Expect(selectedFileChanges == initialFileChanges && selections == initialSelections && loadCalls == 1,
                    label + ": 主题/点击标签引发文件ListBox重选/VM文件通知/LoadAudioFile重入");
                Expect(business.Playback.IsPlayEnabled && !business.Playback.IsPlaying && !business.PodcastPlayback.IsPlaying
                    && !business.IsGenerating && !business.HasActiveProcessing && !panel.IsBusy
                    && playbackStarts == 0 && generationStarts == 0,
                    label + ": 意外进入音频或生成");
                Expect(Field(business.Playback, "_output").GetValue(business.Playback) is WaveOutEvent output
                    && output.PlaybackState == PlaybackState.Stopped
                    && Field(business.Playback, "_timer").GetValue(business.Playback) is DispatcherTimer timer && !timer.IsEnabled,
                    label + ": 真实播放器并非Stopped/计时器已启动");
                Expect(ReferenceEquals(Field(business, "_activeSession").GetValue(business), session)
                    && ReferenceEquals(classic.ViewModel, business) && ReferenceEquals(scroll.ViewModel, business)
                    && fileLists.Values.All(l => ReferenceEquals(l.SelectedItem, file)), label + ": 双缓存共享状态丢失");
                var body = bodies[expected];
                Expect(body.Bounds.Width > 0 && body.Bounds.Height > 0, label + ": 正文未布局");
                if (expected == AudioLabTabKind.Transcript)
                {
                    Expect(transcript.IsEffectivelyVisible && transcript.ItemCount == 309 && ReferenceEquals(transcript.ItemsSource, business.Segments), label + ": 实际列表没有309段");
                    Expect(transcript.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "转录夹具第001段。" && t.Bounds.Height > 0), label + ": 首段没有实际TextBlock");
                    Expect(business.StatusMessage == "转录段落已刷新：309 个段落", label + ": 未执行真实OnSelectedTabChanged刷新分支");
                }
                else if (expected == AudioLabTabKind.MindMap)
                {
                    var canvas = body.GetLogicalDescendants().OfType<MindMapCanvas>().Single();
                    Expect(ReferenceEquals(canvas.Root, mindMap) && canvas.IsEffectivelyVisible
                        && canvas.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == mindMap.Title && t.IsEffectivelyVisible && t.Bounds.Height > 0), label + ": 导图未产生实际根节点文字");
                }
                else
                {
                    var text = expected == AudioLabTabKind.Summary ? SummaryText : PodcastText;
                    Expect(body.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.IsEffectivelyVisible
                        && t.Bounds.Height > 0 && (t.Text == text || t.Inlines?.OfType<Run>().Any(r => r.Text == text) == true)),
                        label + ": Markdown没有实际正文文字节点");
                }
            }
            stage = "initial-summary";
            Snapshot(stage);
            VerifyBody(AudioLabTabKind.Summary, stage);
            foreach (var appearance in new[] { ShellAppearancePreference.SongScroll, ShellAppearancePreference.Classic })
            {
                stage = "theme-" + appearance;
                window.ApplyShellAppearance(appearance);
                ShowAudio();
                await Pump(window, token);
                page = appearance == ShellAppearancePreference.SongScroll ? scroll : classic;
                tabs = allTabs[page];
                clickCounts = allClicks[page];
                list = fileLists[page];
                transcript = page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TranscriptListBox");
                bodies = Bodies(page);
                Check(ReferenceEquals(Field(window, "_currentPage").GetValue(window), page)
                    && page.IsEffectivelyVisible, "主题路由没有命中指定缓存页。");
                Snapshot(stage);
                VerifyBody(AudioLabTabKind.Summary, stage);
                stage = "foreground-" + appearance;
                var drag = page is SongScrollAudioLabView
                    ? scroll.Shell.FindControl<Border>("ShellTitleDrag")!
                    : window.FindControl<Border>("ShellTitleDragArea")!;
                await WindowChromeRegression.ScrollForegroundAsync(window, drag, token);
                await Pump(window, token);
                Check(GetForegroundWindow() == window.TryGetPlatformHandle()!.Handle, "测试窗未获前台，不降级原生输入断言");
                foreach (var target in new[] { AudioLabTabKind.Transcript, AudioLabTabKind.MindMap, AudioLabTabKind.Podcast, AudioLabTabKind.Summary })
                {
                    stage = "native-" + appearance + "-" + target;
                    var button = tabs[target];
                    button.BringIntoView();
                    await Pump(window, token);
                    var point = button.PointToScreen(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2));
                    var hit = window.InputHitTest(window.PointToClient(point)) as Visual;
                    var hitMatches = hit != null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button));
                    Log($"INPUT_HIT {stage};point={point};hit={hit?.GetType().Name};ancestors={string.Join("/", hit?.GetVisualAncestors().Select(v => v.GetType().Name) ?? Array.Empty<string>())};match={hitMatches}");
                    Expect(hitMatches, stage + ": InputHitTest不是目标按钮/子元素");
                    var before = clickCounts[target];
                    var historyStart = selectedTabChanges.Count;
                    {
                        Check(hitMatches && button.IsEffectivelyVisible && button.IsEffectivelyEnabled, stage + ": 原生目标不可点击");
                        Check(GetForegroundWindow() == window.TryGetPlatformHandle()!.Handle
                            && WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }) == window.TryGetPlatformHandle()!.Handle,
                            stage + ": 前台/HWND命中不属于自身");
                        await FloatingSubtitleRegression.MouseClickAsync(point);
                    }
                    Log($"IMMEDIATE {stage};vm={business.SelectedTab};clickDelta={clickCounts[target] - before}");
                    await Pump(window, token);
                    Snapshot(stage + "-rendered");
                    VerifyBody(target, stage);
                    await Task.Delay(180, token);
                    await Pump(window, token);
                    Snapshot(stage + "-settled");
                    VerifyBody(target, stage + "-settled");
                    Expect(clickCounts[target] - before == 1, stage + ": Click回执不等于1，不重试点击");
                    var changes = selectedTabChanges.Skip(historyStart).ToArray();
                    Log($"TAB_HISTORY {stage}=[{string.Join(",", changes)}]");
                    Expect(changes.SequenceEqual(new[] { target }), stage + ": SelectedTab并非仅变化一次至目标（可能发生重置）");
                    if (target == AudioLabTabKind.Transcript && transcript.IsEffectivelyVisible)
                    {
                        // 同一真实列表的末段虚拟化呈现；不选中段落，不触发播放。
                        transcript.ScrollIntoView(business.Segments[^1]);
                        await Pump(window, token);
                        Expect(transcript.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible
                            && t.Text == "转录夹具第309段。" && t.Bounds.Height > 0), stage + ": 末段未实际呈现");
                        transcript.ScrollIntoView(business.Segments[0]);
                        await Pump(window, token);
                    }
                }
            }
            stage = "final-return-song-scroll";
            window.ApplyShellAppearance(ShellAppearancePreference.SongScroll);
            ShowAudio();
            await Pump(window, token);
            page = scroll;
            tabs = allTabs[page];
            list = fileLists[page];
            transcript = page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TranscriptListBox");
            bodies = Bodies(page);
            Snapshot(stage);
            VerifyBody(AudioLabTabKind.Summary, stage);
            Expect(ReferenceEquals(pages[MainWindowViewModel.NavTagAudioLab], classic)
                && ReferenceEquals(pages["song-scroll:" + MainWindowViewModel.NavTagAudioLab], scroll)
                && ReferenceEquals(Field(window, "_currentPage").GetValue(window), scroll), "主题往返未复用原双缓存实例");
            Expect(memoryReads == 3, $"初次恢复加两次真实转录刷新应读内存缓存3次，实际={memoryReads}");
            Expect(forbidden.Count == 0, "出现禁止的依赖调用");
            Expect(File.Exists(audioPath) && !File.Exists(configService.GetConfigFilePath())
                && !File.Exists(Path.Combine(root, "never-read-appearance.json")), "夹具丢失或意外写入配置");
            Log($"FINAL vm={business.SelectedTab};segments={business.Segments.Count};fileSelectionEvents={selections};selectedFileChanges={selectedFileChanges};loadCalls={loadCalls};maxLoadDepth={maxLoadDepth};memoryReads={memoryReads};playbackStarts={playbackStarts};generationStarts={generationStarts};forbidden={forbidden.Count};failures={failures.Count}");
            Log("CONCLUSION=" + (failures.Count == 0 ? "真实MainWindow双缓存/共享VM/实际WAV加载与原生点击均通过，仍未复现用户现场；没有修复或否定用户故障。" : "检测到实际交互/绑定失败，查看TREE/TAB_HISTORY/LOAD_AUDIO_FILE调用栈；不修改生产代码或放宽断言，下次先核官方。"));
            if (failures.Count == 0)
                Log("NEXT=仅在现场下一次点击采集一条脱敏链路：Click目标/命中→SelectedTab前后→SelectedAudioFile通知→LoadAudioFile次数及调用栈→当前页/缓存VM身份/正文有效可见性；不采正文、路径、配置或DB。用于区分点击未到达、加载重置及呈现未更新。");
            Check(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        catch (Exception ex)
        {
            failures.Add($"{stage}: {ex.GetType().Name}: {ex.Message}");
            Log($"FAIL_STAGE={stage};{ex}");
            throw;
        }
        finally
        {
            try { window?.Close(); if (!windowOwnsVm) vm?.Dispose(); }
            finally
            {
                Log($"CLEANUP windowClosed={window == null || !window.IsVisible};onlyFixtureWindow=true");
                Log("FAILURES=" + failures.Count);
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
            }
        }
    }

    private static bool HasCheckedBinding(RadioButton b)
        => BindingOperations.GetBindingExpressionBase(b, ToggleButton.IsCheckedProperty) != null;
    private static async Task Pump(Window window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window) ?? throw new InvalidOperationException("没有真实合成器");
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(token), DispatcherPriority.Background);
    }
    private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, Private)
        ?? throw new MissingFieldException(value.GetType().Name, name);
    private static void Set(object value, string name, object data) => Field(value, name).SetValue(value, data);
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}

// 接口替身不是未初始化对象；只允许上述精确夹具的内存仓储操作，其余调用立即拒绝。
public class SongScrollAudioDenyProxy : DispatchProxy
{
    private Action<string> _denied = null!;
    private Func<MethodInfo, object?[]?, (bool Handled, object? Value)>? _allowed;
    internal static T Create<T>(Action<string> denied,
        Func<MethodInfo, object?[]?, (bool Handled, object? Value)>? allowed = null) where T : class
    {
        var result = DispatchProxy.Create<T, SongScrollAudioDenyProxy>();
        var proxy = (SongScrollAudioDenyProxy)(object)result;
        proxy._denied = denied;
        proxy._allowed = allowed;
        return result;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("缺少替身方法");
        if (_allowed?.Invoke(method, args) is { Handled: true } result) return result.Value;
        var name = method.DeclaringType?.Name + "." + method.Name;
        _denied(name);
        throw new InvalidOperationException("隔离测试禁止调用 " + name);
    }
}