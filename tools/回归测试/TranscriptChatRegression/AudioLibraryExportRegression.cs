using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using TrueFluentPro;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

// 独立窄入口；不改变旧听析测试的内存仓储、断言或严格原生输入策略。
internal static class AudioLibraryExportRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int RecordingCount = 103; // 刻意越过生产仓储 List 的默认 100 条边界。

    internal static async Task VerifyAsync()
    {
        Check(OperatingSystem.IsWindows(), "本专项需要可显示真实 MainWindow 的 Windows 桌面。");
        Check(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "TrueFluentPro.csproj")), "请从仓库根执行专项。");
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "transcript-chat-regression",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-audio-library-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = deadline.Token;
        var report = new List<string>();
        void Log(string value) { report.Add(value); Console.WriteLine(value); }
        var app = Application.Current!;
        var oldTheme = app.RequestedThemeVariant;
        var paths = PathManager.Instance;
        var oldSessions = paths.SessionsPath; // 仅保存路径，不枚举或读取原目录。
        var logger = typeof(AudioLibraryRepository).Assembly.GetType("TrueFluentPro.Services.Storage.SqliteDebugLogger")!;
        var logDir = logger.GetField("_logDir", BindingFlags.Static | BindingFlags.NonPublic)!;
        var resolved = logger.GetField("_logDirResolved", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldLogDir = logDir.GetValue(null);
        var oldResolved = resolved.GetValue(null);
        var passed = false;
        var uiErrors = new ConcurrentQueue<string>();
        void OnUiError(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            uiErrors.Enqueue(e.Exception.ToString());
            e.Handled = true; // 留下失败证据并允许 finally 关闭本次窗口。
        }
        Dispatcher.UIThread.UnhandledException += OnUiError;
        try
        {
            logDir.SetValue(null, root);
            resolved.SetValue(null, true); // DEBUG 仓储日志也严格落在本次 artifacts。
            Log("ARTIFACTS=" + root);
            Log($"ASSEMBLY={typeof(AudioLabViewModel).Assembly.Location};MVID={typeof(AudioLabViewModel).Module.ModuleVersionId};Avalonia={typeof(Application).Assembly.GetName().Version}");
            Log("SCOPE=真实隔离SQLite/生产schema与repos/pipeline/export/真实AudioLabVM/MainWindow/Classic+SongScroll双缓存；不构造MainVM、不访问App.Services/用户配置DB/音频，不播放/录音/云请求。");
            Log("LIMIT=ConfigurationService跳过默认路径构造，仅种本次config路径；队列只有可计数的空查询/初次加载提交适配，无executor；打开导出目录仅计数，不启动Explorer。拖拽仅原生SendInput+实际事件回执；不可安全派发即失败，不重试/不降级合成事件。");
            Log("SHELL_API=SongScrollShell.SetLeftRatio(double)/LeftRatio；生产没有LeftPageRatio属性。");
            VerifyTitles();
            Log("PASS 标题：Audio时间优先/导入时间回退/H1提取/无总结保留原名");
            await VerifyLibraryAsync(Path.Combine(root, "library"), Log, token);
            foreach (var first in new[] { ShellAppearancePreference.Classic, ShellAppearancePreference.SongScroll })
                await VerifyWindowAsync(Path.Combine(root, "cache-" + first), first, Log, token);
            Check(uiErrors.IsEmpty, "出现异步UI异常：" + string.Join(Environment.NewLine, uiErrors));
            passed = true;
        }
        catch (Exception ex)
        {
            Log("FAIL " + ex);
            throw;
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUiError;
            foreach (var error in uiErrors) Log("UI_EXCEPTION=" + error);
            app.RequestedThemeVariant = oldTheme;
            paths.SetSessionsPath(oldSessions);
            logDir.SetValue(null, oldLogDir);
            resolved.SetValue(null, oldResolved);
            Log("AUDIO_LIBRARY_EXPORT_EXIT=" + (passed ? 0 : 1));
            File.WriteAllLines(Path.Combine(root, "result.txt"), report);
        }
    }

    private static void VerifyTitles()
    {
        var imported = new DateTime(2030, 1, 2, 3, 4, 5);
        const string markdown = "```md\n# 围栏伪标题\n```\n\n## 二级不选\n\n# **会议** `简报` ###\n\n总结正文";
        Check(AudioRecordingTitle.ExtractSummaryTitle(markdown) == "会议 简报", "未正确提取顶层H1、忽略围栏或去掉Markdown标记。");
        Check(AudioRecordingTitle.Build("Audio_20261113_091011.wav", imported, markdown) == "2026-11-13 09:10 会议 简报",
            "Audio_日期时间未优先于导入时间。");
        Check(AudioRecordingTitle.Build("导入录音.wav", imported, "# 导入会议") == "2030-01-02 03:04 导入会议",
            "非录音文件名未使用CreatedAt。");
        Check(AudioRecordingTitle.ExtractSummaryTitle("会议标题\n========\n\n正文") == "会议标题", "Setext一级标题未提取。");
        foreach (var summary in new string?[] { null, "", "只有正文\n\n## 不是一级标题" })
            Check(AudioRecordingTitle.Build("Audio_20261113_091011.wav", imported, summary) == "Audio_20261113_091011.wav",
                "无总结H1时没有回退原文件名。");
    }

    private static async Task VerifyLibraryAsync(string root, Action<string> log, CancellationToken token)
    {
        using var f = new Fixture(root);
        var good = new List<(AudioItemRecord Audio, string Marker, string? Summary)>();
        for (var i = 0; i < RecordingCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var fileName = i == 0 ? "Audio_20261113_091011.wav" : i is 6 or 7 ? "同名.wav" : $"离线{i:D3}.wav";
            var audio = f.Add(i.ToString("D3"), Path.Combine(root, "missing-" + i, fileName));
            var marker = $"转录标记_{i:D3}_END";
            var summary = i == 2 ? null : i is 6 or 7 ? "# 同名会议\n\n同名总结标记" : $"# 会议{i:D3}\n\n总结标记_{i:D3}_END";
            f.Pipeline.SaveStageContent(audio.Id, AudioLifecycleStage.Transcribed, i % 2 == 0 ? Raw(marker) : Legacy(marker));
            if (summary != null) f.Pipeline.SaveStageContent(audio.Id, AudioLifecycleStage.Summarized, summary);
            good.Add((audio, marker, summary));
        }
        f.Lifecycle.MarkStale(good[4].Audio.Id, nameof(AudioLifecycleStage.Transcribed));
        f.Lifecycle.MarkStale(good[5].Audio.Id, nameof(AudioLifecycleStage.Summarized));
        var noTranscript = f.Add("no-transcript", Path.Combine(root, "无转录.wav"));
        f.Pipeline.SaveStageContent(noTranscript.Id, AudioLifecycleStage.Summarized, "# 只有总结禁止导出");
        var invalid = new[] { ("bad-json", "{broken"), ("empty", "[]"), ("bad-v2", TranscriptionDataHelper.SerializeRawData(
            new RawTranscriptionData { ApiType = "fast", RawResponse = "not-json" })) };
        foreach (var (id, json) in invalid)
            f.Pipeline.SaveStageContent(f.Add(id, Path.Combine(root, id + ".wav")).Id, AudioLifecycleStage.Transcribed, json);
        var deleted = f.Add("deleted", Path.Combine(root, "已删除.wav"));
        f.Pipeline.SaveStageContent(deleted.Id, AudioLifecycleStage.Transcribed, Legacy("软删除禁止导出"));
        f.Library.SoftDelete(deleted.Id);

        using var observer = f.Db.CreateConnection();
        var snapshot = Snapshot(observer);
        var version = DataVersion(observer);
        var stored = f.Pipeline.ListStoredTranscripts();
        Check(stored.Count == RecordingCount + invalid.Length && stored.All(r => !r.Audio.IsDeleted)
            && stored.All(r => r.Audio.Id != noTranscript.Id && r.Audio.Id != deleted.Id), "全库枚举截断到100、混入软删除或无转录记录。");
        Check(stored.Single(r => r.Audio.Id == good[4].Audio.Id).IsStale
            && stored.Single(r => r.Audio.Id == good[5].Audio.Id).IsStale, "过期转录/总结被丢弃或未标记。");
        Check(good.All(r => !File.Exists(r.Audio.FilePath)), "离线导出夹具必须全部缺少原始音频。");
        var exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(exports);
        var occupied = Path.Combine(exports, "2026-11-13 09_10 会议000_录音稿.md");
        await File.WriteAllTextAsync(occupied, "已有文件，不可覆盖", token);
        var protectedFiles = FileHashes(exports);
        for (var pass = 1; pass <= 2; pass++)
        {
            var result = await f.Vm.ExportAllTranscriptsAsync(exports, token);
            Check(result != null, "未选当前音频的全库导出返回null：" + f.Vm.StatusMessage);
            var files = result!.ExportedFiles;
            Check(Path.GetFullPath(result.ExportDirectory) == Path.GetFullPath(exports)
                && files.Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Count, "导出结果目录或文件唯一性错误。");
            var documents = files.ToDictionary(path => path, path => File.ReadAllText(path));
            Check(files.Count >= RecordingCount && files.All(path => Path.GetExtension(path) == ".md"
                && Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(exports)), "未输出全量Markdown或意外复制音频/逃逸目录。");
            var transcriptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (audio, marker, summary) in good)
            {
                var matching = documents.Where(d => d.Value.Contains(marker, StringComparison.Ordinal)).ToArray();
                Check(matching.Length == 1, $"{audio.Id}：单条录音稿丢失、重复或被拼成整库一份文件。");
                Check(transcriptPaths.Add(matching[0].Key), "不同音频被拼进同一份录音稿。");
                Check(matching[0].Value.Contains(marker + "_末段", StringComparison.Ordinal)
                    && matching[0].Value.Contains("[00:00:10]", StringComparison.Ordinal)
                    && matching[0].Value.Contains("[00:40:10]", StringComparison.Ordinal), audio.Id + "：v2/legacy时间戳或末段丢失。");
                var title = summary == null ? audio.FileName : audio.Id == "000" ? "2026-11-13 09:10 会议000"
                    : "2030-01-02 03:04 " + (audio.Id is "006" or "007" ? "同名会议" : "会议" + audio.Id);
                Check(matching[0].Value.Contains(title, StringComparison.Ordinal), audio.Id + "：录音稿未使用独立预期显示名称。");
                var prefix = Path.GetFileNameWithoutExtension(title).Replace(':', '_');
                Check(Path.GetFileName(matching[0].Key).StartsWith(prefix, StringComparison.Ordinal), audio.Id + "：文件名未使用日期标题/无总结原名回退。");
            }
            Check(documents.Values.All(d => !d.Contains("软删除禁止导出") && !d.Contains("只有总结禁止导出")
                && !d.Contains("{broken") && !d.Contains("not-json")), "无效/删除/只有总结内容进入导出。");
            // 总结附件可选，但出现时必须是已存内容，且不能取代录音稿。
            foreach (var document in documents.Values.Where(d => !d.Contains("转录标记_", StringComparison.Ordinal)))
                Check(good.Any(r => r.Summary != null && document.Contains(r.Summary.Trim(), StringComparison.Ordinal)), "出现不属于录音稿或已有总结的产物。");
            Check(f.Vm.StatusMessage.Contains("过期", StringComparison.Ordinal)
                || documents.Values.Any(d => d.Contains("过期", StringComparison.Ordinal)), "过期内容虽导出但没有用户提示。");
            foreach (var (path, hash) in protectedFiles)
                Check(File.Exists(path) && HashFile(path) == hash, "同名/重复批量导出覆盖了已有文件：" + path);
            protectedFiles = FileHashes(exports);
            Check(!Directory.EnumerateDirectories(exports).Any(), "导出完成后仍有暂存目录。");
            Check(f.Vm.SelectedAudioFile == null && f.Vm.CurrentFilePath.Length == 0 && f.Vm.Segments.Count == 0
                && Sessions(f.Vm).Count == 0 && f.LoadCalls == 0 && !f.Vm.Playback.IsStopEnabled,
                "全库导出依赖当前选择，或为了导出创建/加载了会话。");
            Check(!f.Vm.IsExporting && f.Vm.HasExportFeedback && !f.Vm.IsGenerating && !f.Vm.HasActiveProcessing, "导出状态未收尾或触发生成。");
            Check(Snapshot(observer) == snapshot && DataVersion(observer) == version, "只读导出改写了SQLite库。");
            f.AssertQuiet();
            log($"PASS library pass={pass};recordings={RecordingCount};markdown={files.Count};stored={stored.Count};sourceFiles=0;loads=0;queue=0;SQLiteUnchanged=true;feedback={f.Vm.StatusMessage}");
        }
        Check(f.OpenedDirectories.Count <= 2 && f.OpenedDirectories.All(path => Path.GetFullPath(path) == Path.GetFullPath(exports)), "打开目录适配收到错误目录/重复调用。");
    }

    private static async Task VerifyWindowAsync(string root, ShellAppearancePreference first, Action<string> log, CancellationToken token)
    {
        using var f = new Fixture(root);
        var selectedPath = Path.Combine(root, "Audio_20261113_091011.wav");
        var otherPath = Path.Combine(root, "Audio_20261113_091012.wav");
        foreach (var path in new[] { selectedPath, otherPath })
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
            writer.Write(new byte[32000], 0, 32000);
        }
        var selectedRecord = f.Add("selected", selectedPath);
        // 旧导入以Sessions相对路径保存，列表实际扫描返回绝对路径。
        var otherRecord = f.Add("other", Path.GetFileName(otherPath));
        f.Pipeline.SaveStageContent(selectedRecord.Id, AudioLifecycleStage.Transcribed, Raw("当前音频"));
        f.Pipeline.SaveStageContent(selectedRecord.Id, AudioLifecycleStage.Summarized, "# 当前会议\n\n当前总结保持不变");
        f.Pipeline.SaveStageContent(otherRecord.Id, AudioLifecycleStage.Summarized, "# 非选中旧标题\n\n后台总结");
        var wavHashes = new[] { selectedPath, otherPath }.ToDictionary(path => path, HashFile);
        await f.Vm.RefreshAudioFilesAsync(token);
        Check(f.Vm.AudioFiles.Count == 2 && f.Vm.SelectedAudioFile == null && f.LoadCalls == 0, "目录扫描触发了加载/自动选择。");
        var other = f.Vm.AudioFiles.Single(item => item.FullPath == otherPath);
        Check(other.AudioDisplayName == other.Name, "未有转录时不应使用总结标题。");
        MainWindow? window = null;
        try
        {
            window = new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root, "appearance.json")))
            { Title = "音频库窄回归 · " + first, Width = 1256, Height = 831, WindowStartupLocation = WindowStartupLocation.Manual };
            Field(window, "_audioLabBusiness").SetValue(window, f.Vm);
            Field(window, "_audioLabInitialized").SetValue(window, true);
            f.WindowOwnsVm = true;
            var screen = window.Screens.Primary ?? window.Screens.All.First();
            Check(screen.WorkingArea.Width / screen.Scaling >= window.MinWidth + 32
                && screen.WorkingArea.Height / screen.Scaling >= window.MinHeight + 64, "桌面容纳不了真实主窗，不降低生产最小尺寸。");
            window.Width = Math.Min(window.Width, screen.WorkingArea.Width / screen.Scaling - 32);
            window.Height = Math.Min(window.Height, screen.WorkingArea.Height / screen.Scaling - 64);
            window.Position = new PixelPoint(screen.WorkingArea.X + 16, screen.WorkingArea.Y + 16);
            window.Show();
            await Pump(window, token); // OnOpened的无MainVM默认live页必须先完成，随后明确导航。
            var pages = (Dictionary<string, Control>)Field(window, "_pageCache").GetValue(window)!;
            AudioLabView Route(ShellAppearancePreference appearance)
            {
                window.ApplyShellAppearance(appearance);
                var show = typeof(MainWindow).GetMethod("ShowPage", Private)!;
                show.Invoke(window, new object[] { MainWindowViewModel.NavTagAudioLab });
                show.Invoke(window, new object[] { MainWindowViewModel.NavTagAudioLab }); // 新建经典页下一次路由执行生产BindBusinessPage。
                return (AudioLabView)pages[(appearance == ShellAppearancePreference.SongScroll ? "song-scroll:" : "") + MainWindowViewModel.NavTagAudioLab];
            }
            var firstView = Route(first);
            await Pump(window, token);
            f.AllowInitialQueueSubmit = true;
            try { await firstView.LoadAudioFileWithFormatCheckAsync(selectedPath); }
            finally { f.AllowInitialQueueSubmit = false; }
            await Until(() => f.Vm.SelectedAudioFile?.AudioDisplayName == "2026-11-13 09:10 当前会议", token);
            await Pump(window, token);
            Check(f.LoadCalls == 1 && f.Vm.Playback.IsPlayEnabled && f.Vm.Segments.Count == 2
                && f.Vm.CurrentFilePath == selectedPath, "真实WAV加载没有恢复生产缓存/播放器。");
            var second = first == ShellAppearancePreference.Classic ? ShellAppearancePreference.SongScroll : ShellAppearancePreference.Classic;
            var secondView = Route(second);
            await Pump(window, token);
            var classic = (AudioLabView)pages[MainWindowViewModel.NavTagAudioLab];
            var scroll = (SongScrollAudioLabView)pages["song-scroll:" + MainWindowViewModel.NavTagAudioLab];
            Check(!ReferenceEquals(firstView, secondView) && ReferenceEquals(classic.ViewModel, f.Vm)
                && ReferenceEquals(scroll.ViewModel, f.Vm) && window.DataContext == null, "不是主窗双缓存/共享真实VM。");
            const double customRatio = .413;
            scroll.Shell.SetLeftRatio(customRatio);
            var lists = new[] { classic, scroll }.Select(view => view.GetVisualDescendants().OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, f.Vm.AudioFiles))).ToArray();
            var selected = f.Vm.SelectedAudioFile!;
            var items = f.Vm.AudioFiles.ToArray();
            var currentFileName = f.Vm.CurrentFileName;
            var breadcrumb = f.Vm.BreadcrumbText;
            var selectedTab = f.Vm.SelectedTab;
            Check(selected.AudioDisplayName == "2026-11-13 09:10 当前会议", "加载后的当前列表项没有使用总结显示名。");
            var session = Field(f.Vm, "_activeSession").GetValue(f.Vm);
            var reader = Field(f.Vm.Playback, "_reader").GetValue(f.Vm.Playback);
            var output = Field(f.Vm.Playback, "_output").GetValue(f.Vm.Playback);
            var queueBaseline = f.QueueSubmissions;
            var selectionEvents = 0;
            var vmSelectionEvents = 0;
            var collectionChanges = 0;
            var titleNotifications = 0;
            foreach (var list in lists) list.SelectionChanged += (_, _) => selectionEvents++;
            f.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AudioLabViewModel.SelectedAudioFile)) vmSelectionEvents++; };
            f.Vm.AudioFiles.CollectionChanged += (_, _) => collectionChanges++;
            other.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MediaFileItem.AudioDisplayName)) titleNotifications++; };
            void Stable(string stage)
            {
                Check(ReferenceEquals(f.Vm.SelectedAudioFile, selected) && lists.All(list => ReferenceEquals(list.SelectedItem, selected))
                    && selectionEvents == 0 && vmSelectionEvents == 0 && collectionChanges == 0
                    && f.Vm.AudioFiles.SequenceEqual(items), stage + "：后台保存/主题往返重选或重建了列表。");
                Check(f.LoadCalls == 1 && Sessions(f.Vm).Count == 1 && ReferenceEquals(Field(f.Vm, "_activeSession").GetValue(f.Vm), session)
                    && ReferenceEquals(Field(f.Vm.Playback, "_reader").GetValue(f.Vm.Playback), reader)
                    && ReferenceEquals(Field(f.Vm.Playback, "_output").GetValue(f.Vm.Playback), output), stage + "：发生隐式加载/会话替换。");
                Check(f.Vm.CurrentFilePath == selectedPath && f.Vm.CurrentFileName == currentFileName
                    && f.Vm.BreadcrumbText == breadcrumb && f.Vm.SelectedTab == selectedTab
                    && f.Vm.SummaryMarkdown == "# 当前会议\n\n当前总结保持不变" && f.Vm.Segments.Count == 2, stage + "：非选中项刷新污染当前音频。");
                Check(Math.Abs(scroll.Shell.LeftRatio - customRatio) < 1e-9 && f.QueueSubmissions == queueBaseline,
                    stage + "：重置定制书脊/触发任务提交。");
                f.AssertQuiet();
            }
            var titleEvents = new ConcurrentQueue<string>();
            f.Pipeline.RecordingTitleChanged += titleEvents.Enqueue;
            // 真实后台线程SaveStageContent→真实仓储→生产事件→VM调度；不反射调用刷新处理函数。
            Route(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            await Task.Run(() => f.Pipeline.SaveStageContent(otherRecord.Id, AudioLifecycleStage.Transcribed, Legacy("非选中音频")), token);
            await Until(() => other.AudioDisplayName == "2026-11-13 09:10 非选中旧标题", token);
            await Pump(window, token);
            VisibleTitle(scroll, other, "2026-11-13 09:10 非选中旧标题");
            Stable("保存转录");
            Route(ShellAppearancePreference.Classic);
            await Pump(window, token);
            VisibleTitle(classic, other, "2026-11-13 09:10 非选中旧标题");
            await Task.Run(() => f.Pipeline.SaveStageContent(otherRecord.Id, AudioLifecycleStage.Summarized, "# 非选中新标题\n\n后台更新"), token);
            await Until(() => other.AudioDisplayName == "2026-11-13 09:10 非选中新标题", token);
            await Pump(window, token);
            VisibleTitle(classic, other, "2026-11-13 09:10 非选中新标题");
            Route(ShellAppearancePreference.SongScroll);
            await Pump(window, token);
            VisibleTitle(scroll, other, "2026-11-13 09:10 非选中新标题");
            Stable("保存总结并往返");
            Check(titleEvents.SequenceEqual(new[] { otherRecord.Id, otherRecord.Id }) && titleNotifications > 0,
                "生产保存事件/列表项属性通知缺失、重复或音频ID不符。");
            Check(f.Library.GetByFilePath(Path.GetFileName(otherPath))?.Id == otherRecord.Id
                && f.Library.GetByFilePath(otherPath) == null, "标题刷新意外创建绝对路径重复记录。");
            log($"PASS cache-order={first}->{second};nonselected-title=true;notifications={titleNotifications};selectionEvents=0;collectionChanges=0;loads=1;queueUnchanged=true");

            Route(ShellAppearancePreference.Classic);
            await Pump(window, token);
            await WindowChromeRegression.ScrollForegroundAsync(window, window.FindControl<Border>("ShellTitleDragArea")!, token);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                Route(ShellAppearancePreference.Classic);
                await Pump(window, token);
                Check(classic.ActualThemeVariant == theme, "经典页未实际进入要求的深/浅主题。");
                var layout = classic.FindControl<Grid>("FilePanelLayout")!;
                var rail = classic.FindControl<Border>("FilePanelRail")!;
                var splitter = classic.FindControl<GridSplitter>("FilePanelSplitter")!;
                Check(splitter.IsEffectivelyVisible && splitter.IsEffectivelyEnabled && splitter.Bounds.Width > 0,
                    "经典页实际分隔条未显示/不可用。");
                var before = rail.Bounds.Width;
                await DragSplitter(window, splitter, 64, log, token);
                await Pump(window, token);
                var expanded = rail.Bounds.Width;
                Check(expanded > before + 24, $"向右实际拖动未增大音频列表宽度：before={before:F2};expanded={expanded:F2}。");
                await DragSplitter(window, splitter, -32, log, token);
                await Pump(window, token);
                var savedWidth = rail.Bounds.Width;
                Check(savedWidth < expanded - 12 && savedWidth > before + 8, $"向左实际拖动未减小宽度或两次结果无区别：before={before:F2};expanded={expanded:F2};saved={savedWidth:F2}。");
                var toggle = classic.GetVisualDescendants().OfType<Button>().Single(button =>
                    AutomationProperties.GetName(button) == "展开或收起文件面板");
                await WindowChromeRegression.ScrollClickAsync(window, toggle, token);
                await Pump(window, token);
                Check(!f.Vm.IsFilePanelOpen && Near(rail.Bounds.Width, 52) && Near(layout.ColumnDefinitions[0].ActualWidth, 52)
                    && !splitter.IsVisible && !splitter.IsEnabled, "收起未进入52DIP或分隔条仍可交互。");
                Route(ShellAppearancePreference.SongScroll);
                await Pump(window, token);
                Stable("收起后进入长卷");
                Route(ShellAppearancePreference.Classic);
                await Pump(window, token);
                Check(Near(rail.Bounds.Width, 52), "主题往返丢失收起状态。");
                await WindowChromeRegression.ScrollClickAsync(window, toggle, token);
                await Pump(window, token);
                Check(f.Vm.IsFilePanelOpen && Near(rail.Bounds.Width, savedWidth) && splitter.IsEffectivelyVisible,
                    "展开未恢复拖动后的宽度。");
                Route(ShellAppearancePreference.SongScroll);
                await Pump(window, token);
                Stable("展开后进入长卷");
                Route(ShellAppearancePreference.Classic);
                await Pump(window, token);
                Check(Near(rail.Bounds.Width, savedWidth) && ReferenceEquals(pages[MainWindowViewModel.NavTagAudioLab], classic)
                    && ReferenceEquals(pages["song-scroll:" + MainWindowViewModel.NavTagAudioLab], scroll), "往返没有复用页面/恢复宽度。");
                Stable("经典深浅往返");
                // 等待此处及前述折叠触发的生产异步配置保存完成，仍只写夹具文件。
                await f.ConfigService.SaveConfigAsync(f.Config);
                log($"PASS splitter cache-order={first};theme={theme};input=native-SendInput;collapsed=52;restored=true;width={before:F2}->{expanded:F2}->{savedWidth:F2}->52->{rail.Bounds.Width:F2};Shell.LeftRatio={scroll.Shell.LeftRatio:F3}");
            }
            Check(wavHashes.All(pair => File.Exists(pair.Key) && HashFile(pair.Key) == pair.Value), "显示命名修改了原始音频文件。");
            Check(!File.Exists(Path.Combine(root, "appearance.json")), "直接应用主题不应保存外观偏好。");
        }
        finally { window?.Close(); }
    }

    private static void VisibleTitle(AudioLabView view, MediaFileItem item, string title)
    {
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, view.ViewModel!.AudioFiles));
        Check(list.GetVisualDescendants().OfType<ListBoxItem>().Any(row => ReferenceEquals(row.DataContext, item)
            && row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.Text == title)),
            view.GetType().Name + "：非选中项实际文字节点没有刷新（不能只验VM属性）。");
    }

    private static async Task DragSplitter(MainWindow window, GridSplitter splitter, double delta, Action<string> log, CancellationToken token)
    {
        Check(splitter.IsEffectivelyVisible && splitter.IsEffectivelyEnabled, "拖动目标不可用。");
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        GetWindowThreadProcessId(hwnd, out var process);
        Check(hwnd != IntPtr.Zero && process == (uint)Environment.ProcessId, "拖动窗口不是当前专项进程的HWND。");
        var start = splitter.PointToScreen(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2));
        var end = new PixelPoint(start.X + (int)Math.Round(delta * window.RenderScaling), start.Y);
        var hit = window.InputHitTest(window.PointToClient(start)) as Visual;
        Check(hit == splitter || hit?.GetVisualAncestors().Contains(splitter) == true, "实际InputHitTest没有命中生产GridSplitter。");
        var rail = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FilePanelRail" && b.IsEffectivelyVisible);
        var presses = 0; var windowPresses = 0; var starts = 0; var drags = 0; var completed = 0; var releases = 0; var nativeDowns = 0;
        IPointer? pointer = null;
        var pressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        static string Describe(object? value) => value is Control c ? c.GetType().Name + "#" + c.Name : value?.GetType().Name ?? "null";
        void Trace(string stage, RoutedEventArgs? e = null)
        {
            Check(GetCursorPos(out var cursor), "无法读取实际鼠标位置。");
            var actual = new PixelPoint(cursor.X, cursor.Y);
            log($"NATIVE_DRAG stage={stage};delta={delta};presses={presses};windowPresses={windowPresses};starts={starts};drags={drags};completed={completed};releases={releases};WM_LBUTTONDOWN={nativeDowns};Source={Describe(e?.Source)};Handled={e?.Handled};capture={Describe(pointer?.Captured)};nativeCapture={GetCapture()};width={rail.Bounds.Width:F2};HWND={hwnd};foreground={GetForegroundWindow()};HWNDhit={WindowFromPoint(cursor)};inputhit={Describe(window.InputHitTest(window.PointToClient(actual)))};requestedStart={start};requestedEnd={end};cursor={actual};scale={window.RenderScaling}");
        }
        void SafeTarget(PixelPoint point)
        {
            token.ThrowIfCancellationRequested();
            Check(GetForegroundWindow() == hwnd && WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }) == hwnd,
                $"原生输入安全门禁失败：own={hwnd};foreground={GetForegroundWindow()};point={point}；不向其他应用按下/移动。");
        }
        async Task Receipt(Func<bool> ready, string stage)
        {
            var clock = Stopwatch.StartNew();
            while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(10, token);
            }
            Trace(stage);
            Check(ready(), "原生拖拽回执超时：" + stage);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        EventHandler<PointerPressedEventArgs> windowObserved = (_, e) => { windowPresses++; pointer = e.Pointer; Trace("window-tunnel-down", e); };
        EventHandler<PointerPressedEventArgs> observed = (_, e) =>
        {
            presses++; pointer = e.Pointer; Trace("splitter-bubble-down", e); pressed.TrySetResult(true);
        };
        EventHandler<VectorEventArgs> onStart = (_, e) => { starts++; Trace("DragStarted", e); started.TrySetResult(true); };
        EventHandler<VectorEventArgs> onDrag = (_, e) => { drags++; Trace("DragDelta:" + e.Vector, e); };
        EventHandler<VectorEventArgs> onComplete = (_, e) => { completed++; Trace("DragCompleted", e); };
        EventHandler<PointerReleasedEventArgs> onRelease = (_, e) => { releases++; Trace("PointerReleased", e); };
        Win32Properties.CustomWndProcHookCallback hook = (IntPtr handle, uint message, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (message == 0x0201) { nativeDowns++; Trace("WM_LBUTTONDOWN"); }
            return IntPtr.Zero;
        };
        window.AddHandler(InputElement.PointerPressedEvent, windowObserved, RoutingStrategies.Tunnel, handledEventsToo: true);
        splitter.AddHandler(InputElement.PointerPressedEvent, observed, RoutingStrategies.Bubble, handledEventsToo: true);
        splitter.AddHandler(Thumb.DragStartedEvent, onStart, RoutingStrategies.Bubble, handledEventsToo: true);
        splitter.AddHandler(Thumb.DragDeltaEvent, onDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        splitter.AddHandler(Thumb.DragCompletedEvent, onComplete, RoutingStrategies.Bubble, handledEventsToo: true);
        splitter.AddHandler(InputElement.PointerReleasedEvent, onRelease, RoutingStrategies.Bubble, handledEventsToo: true);
        Win32Properties.AddWndProcHookCallback(window, hook);
        var downSent = false;
        try
        {
            SafeTarget(start);
            Check((GetAsyncKeyState(1) & 0x8000) == 0, "左键原已按住，不接管用户拖拽。");
            Check(SetCursorPos(start.X, start.Y), "无法定位真实拖动起点。");
            await Pump(window, token);
            await Task.Delay(100, token);
            Trace("before-down");
            SafeTarget(start);
            Check(GetCursorPos(out var cursor) && cursor.X == start.X && cursor.Y == start.Y, "按下前实际游标不在目标起点。");
            downSent = true; // 即使SendInput回报异常也必须在finally配对松键。
            SendNativeMouse(2);
            // Thumb在PointerPressed内部同步Raise DragStarted；后台优先级屏障确保整个按下路由已返回。
            // MouseDevice隐式捕获实际命中的子Border；绝不要求Captured==splitter，也不主动Capture。
            await Receipt(() => pressed.Task.IsCompletedSuccessfully && started.Task.IsCompletedSuccessfully, "down-and-start-ack");
            Check(presses == 1 && starts == 1, $"按下回执不唯一：presses={presses};starts={starts}。");
            foreach (var point in new[] { new PixelPoint((start.X + end.X) / 2, start.Y), end })
            {
                SafeTarget(point);
                Check(GetCursorPos(out cursor), "移动前无法读取实际游标。");
                SafeTarget(new PixelPoint(cursor.X, cursor.Y));
                var previous = drags;
                SendNativeMouse(0xE001, point); // MOVE|NOCOALESCE|VIRTUALDESK|ABSOLUTE；单独原生移动。
                await Receipt(() => drags > previous && GetCursorPos(out var actual)
                    && actual.X == point.X && actual.Y == point.Y, "move-ack:" + point);
                await Pump(window, token);
            }
            SafeTarget(end);
            SendNativeMouse(4);
            downSent = false;
            await Receipt(() => releases > 0 && completed > 0, "release-ack");
            await Pump(window, token);
            Trace("final");
            Check(presses == 1 && starts == 1 && drags >= 2 && completed == 1 && releases == 1,
                $"拖动回执错误：presses={presses};starts={starts};drags={drags};completed={completed};releases={releases}。");
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            try
            {
                // 只对本helper已注入的按下配对松键；失败/取消亦不遗留系统左键按住。
                if (downSent) SendNativeMouse(4);
            }
            finally
            {
                Trace("finally");
                Win32Properties.RemoveWndProcHookCallback(window, hook);
                window.RemoveHandler(InputElement.PointerPressedEvent, windowObserved);
                splitter.RemoveHandler(InputElement.PointerPressedEvent, observed);
                splitter.RemoveHandler(Thumb.DragStartedEvent, onStart);
                splitter.RemoveHandler(Thumb.DragDeltaEvent, onDrag);
                splitter.RemoveHandler(Thumb.DragCompletedEvent, onComplete);
                splitter.RemoveHandler(InputElement.PointerReleasedEvent, onRelease);
            }
        }
    }

    private static void SendNativeMouse(uint flags, PixelPoint? point = null)
    {
        var mouse = new NativeMouseInput { Flags = flags };
        if (point is { } p)
        {
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            Check(width > 1 && height > 1, "虚拟桌面尺寸无效。");
            mouse.X = (int)Math.Round((p.X - GetSystemMetrics(76)) * 65535d / (width - 1));
            mouse.Y = (int)Math.Round((p.Y - GetSystemMetrics(77)) * 65535d / (height - 1));
        }
        var sent = SendInput(1, new[] { new NativeMousePacket { Mouse = mouse } }, Marshal.SizeOf<NativeMousePacket>());
        Check(sent == 1, $"SendInput flags=0x{flags:X}:sent={sent}/1;Win32={Marshal.GetLastWin32Error()}。");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly IsolatedDb Db;
        internal readonly AudioLibraryRepository Library;
        internal readonly IAudioLifecycleRepository Lifecycle;
        internal readonly AudioLifecyclePipelineService Pipeline;
        internal readonly AudioLabViewModel Vm;
        internal readonly AzureSpeechConfig Config = new() { AudioLabFilePanelOpen = true, EnableBatchSubtitleSentenceSplit = false,
            AudioLabStagePresets = AudioLabStagePresetDefaults.CreateDefaults() };
        internal readonly ConfigurationService ConfigService;
        internal readonly List<string> OpenedDirectories = new();
        private readonly ConcurrentQueue<string> _forbidden = new();
        internal int LoadCalls, QueueSubmissions;
        internal bool AllowInitialQueueSubmit, WindowOwnsVm;

        internal Fixture(string root)
        {
            Directory.CreateDirectory(root);
            PathManager.Instance.SetSessionsPath(root);
            Check(PathManager.Instance.SessionsPath == Path.GetFullPath(root), "夹具Sessions路径未生效，禁止扫描其他目录。");
            Db = new IsolatedDb(Path.Combine(root, "audio.db"));
            Library = new AudioLibraryRepository(Db);
            Lifecycle = new AudioLifecycleRepository(Db);
            // 代理仅观测真实LoadAudioFile→EnsureAudioItem调用，所有方法原样转发生产SQLite仓储。
            var observedRepo = SongScrollAudioDenyProxy.Create<IAudioLibraryRepository>(_forbidden.Enqueue, (method, args) =>
            {
                if (method.Name == nameof(IAudioLibraryRepository.GetByFilePath))
                {
                    var stack = new StackTrace().GetFrames().Select(frame => frame.GetMethod()).ToArray();
                    if (stack.Any(m => m?.DeclaringType == typeof(AudioLifecyclePipelineService) && m.Name == nameof(AudioLifecyclePipelineService.EnsureAudioItem))
                        && stack.Any(m => m?.DeclaringType == typeof(AudioLabViewModel) && m.Name == nameof(AudioLabViewModel.LoadAudioFile)))
                        Interlocked.Increment(ref LoadCalls);
                }
                return (true, method.Invoke(Library, args));
            });
            T Deny<T>() where T : class => SongScrollAudioDenyProxy.Create<T>(_forbidden.Enqueue);
            var ai = Deny<IAiInsightService>();
            var tokens = Deny<IAzureTokenProviderStore>();
            var tts = new SpeechSynthesisService();
            Pipeline = new AudioLifecyclePipelineService(observedRepo, Lifecycle, ai, tts);
            ConfigService = (ConfigurationService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigurationService));
            Field(ConfigService, "_configFilePath").SetValue(ConfigService, Path.Combine(root, "config.json"));
            Field(ConfigService, "_backupConfigFilePath").SetValue(ConfigService, Path.Combine(root, "config.json.bak"));
            var folder = SongScrollAudioDenyProxy.Create<IDesktopFolderLauncher>(_forbidden.Enqueue, (method, args) =>
            {
                if (method.Name != nameof(IDesktopFolderLauncher.Open)) return (false, null);
                OpenedDirectories.Add((string)args![0]!);
                return (true, null);
            });
            var queue = SongScrollAudioDenyProxy.Create<IAudioTaskQueueService>(_forbidden.Enqueue, (method, _) =>
            {
                if (method.Name == nameof(IAudioTaskQueueService.Query)) return (true, new List<AudioTaskRecord>());
                if (method.Name == nameof(IAudioTaskQueueService.SubmitAll) && AllowInitialQueueSubmit)
                { QueueSubmissions++; return (true, new List<string>()); }
                return (false, null);
            });
            Vm = new AudioLabViewModel(ai, tokens, Deny<IModelRuntimeResolver>(), Deny<ISpeechResourceRuntimeResolver>(),
                Deny<IAiAudioTranscriptionService>(), () => Config, ConfigService, Pipeline,
                new AudioLabControlPanelViewModel(Pipeline, tts, () => Config, tokens), new AudioLabExportService(), folder, queue);
            foreach (var playback in new[] { Vm.Playback, Vm.PodcastPlayback })
                playback.PropertyChanged += (_, _) => { if (playback.IsPlaying) _forbidden.Enqueue("Playback.Play"); };
            Vm.PropertyChanged += (_, _) => { if (Vm.IsGenerating) _forbidden.Enqueue("VM.IsGenerating"); };
        }

        internal AudioItemRecord Add(string id, string path)
        {
            var record = new AudioItemRecord { Id = id, FilePath = path, FileName = Path.GetFileName(path),
                DirectoryPath = Path.GetDirectoryName(path) ?? "", CreatedAt = new DateTime(2030, 1, 2, 3, 4, 5), UpdatedAt = new DateTime(2030, 1, 2, 3, 4, 5) };
            Library.Upsert(record);
            return record;
        }
        internal void AssertQuiet() => Check(_forbidden.IsEmpty && !Vm.IsGenerating && !Vm.HasActiveProcessing
            && !Vm.Playback.IsPlaying && !Vm.PodcastPlayback.IsPlaying && !Vm.ControlPanel.IsBusy,
            "出现加载外的云/鉴权/队列/播放活动：" + string.Join(",", _forbidden));
        public void Dispose() { if (!WindowOwnsVm) Vm.Dispose(); Db.Dispose(); }
    }

    private static string Raw(string marker) => TranscriptionDataHelper.SerializeRawData(new RawTranscriptionData
    {
        ApiType = "fast", RawResponse = JsonSerializer.Serialize(new { phrases = new[]
        {
            new { text = marker, speaker = 0, offsetMilliseconds = 10000L, durationMilliseconds = 1000L },
            new { text = marker + "_末段", speaker = 1, offsetMilliseconds = 2410000L, durationMilliseconds = 1000L }
        } })
    });
    private static string Legacy(string marker) => JsonSerializer.Serialize(new[]
    {
        new { Speaker = "主持人", SpeakerIndex = 0, StartTimeTicks = TimeSpan.FromSeconds(10).Ticks, Text = marker },
        new { Speaker = "嘉宾", SpeakerIndex = 1, StartTimeTicks = TimeSpan.FromSeconds(2410).Ticks, Text = marker + "_末段" }
    });
    private static string Snapshot(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var text = new StringBuilder();
        foreach (var table in new[] { "audio_library_items", "audio_lifecycle" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                text.AppendLine(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static long DataVersion(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar()); // 不比较装箱object/ValueTuple引用。
    }
    private static Dictionary<string, string> FileHashes(string root) => Directory.GetFiles(root).ToDictionary(path => path, HashFile);
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static IDictionary Sessions(AudioLabViewModel vm) => (IDictionary)Field(vm, "_sessions").GetValue(vm)!;
    private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, Private) ?? throw new MissingFieldException(value.GetType().Name, name);
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1.1; // 最多一物理像素布局舍入，不放宽方向/宽度变化门槛。
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> ready, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(4), "阶段已保存，但非选中项显示名称未刷新。");
            await Task.Delay(20, token);
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static async Task Pump(Window window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window) ?? throw new InvalidOperationException("没有真实窗口合成器。");
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(token), DispatcherPriority.Background);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    // INPUT的最大union成员为MOUSEINPUT，保留其原生对齐/大小（x64=40，x86=28）。
    [StructLayout(LayoutKind.Sequential)] private struct NativeMousePacket { public uint Type; public NativeMouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeMousePacket[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}