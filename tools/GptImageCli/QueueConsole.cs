using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GptImageCli;

internal static class QueueConsole
{
    public static async Task<int> RunAsync(QueuePaths paths, QueueStore store)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var session = new Session(paths, store,
                !Console.IsInputRedirected && !Console.IsOutputRedirected, cancellation.Token);
            await session.RunAsync();
            Console.WriteLine("已退出管理界面；未停止后台，也未取消正在执行的任务。");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("已退出管理界面；未停止后台，已生效的操作保留。");
            return 0;
        }
        catch (IOException) { return 1; } // A closed output pipe cannot display another menu.
        finally { Console.CancelKeyPress -= cancel; }
    }

    // Rendering is deliberately append-only. Only the interactive session may clear the screen.
    public static void Render(QueueSnapshot snapshot, int page = 1)
    {
        var now = QueueStore.Now;
        Console.WriteLine("=== 生图队列管理 ===");
        Console.WriteLine($"后台：{(snapshot.WorkerRunning ? "运行中" : "未运行（继续可启动）")} | 全局：{(snapshot.Paused ? "已暂停" : "未暂停")}");
        Console.WriteLine($"队列配置：{SafeText(snapshot.SettingsPath, 180)}");
        foreach (var q in snapshot.Queues)
        {
            Console.WriteLine($"队列 {SafeText(q.Id, 64)} | RPM {q.SentLastMinute}/{q.RequestsPerMinute}（近60秒/上限） | 并发 {q.Running}/{q.MaxConcurrency}");
            Console.WriteLine($"  排队 {q.Pending} | 重试 {q.RetryWaiting} | 成功 {q.Succeeded} | 失败 {q.Failed} | 待确认 {q.Unknown} | 已取消 {q.Cancelled}");
            var pause = snapshot.Paused ? q.Paused ? "全局及本队列暂停" : "全局暂停" : q.Paused ? "本队列暂停" : "否";
            var cooldown = q.NextDispatchAt > now ? $"等待 {Math.Ceiling((q.NextDispatchAt - (double)now) / 1000)} 秒" : "无";
            Console.WriteLine($"  启用：{(q.Enabled ? "是" : "否")} | 暂停：{pause} | 冷却/限速：{cooldown} | 下一发送（最早）：{DispatchTime(q.NextDispatchAt, now)}");
            Console.WriteLine($"  额外重试上限 {q.RetryCount} 次 | 重试等待 {q.RetryDelaySeconds} 秒");
        }
        Console.WriteLine("下一发送是最早允许时间，仍受暂停、禁用、并发、滚动 RPM 和任务重试时间约束。");
        Console.WriteLine($"--- 任务 · 第 {Math.Max(1, page)} 页（每页最多 20 条；编号是数据库 ID）---");
        Console.WriteLine("编号 | 队列 | 名称 | 状态 | 尝试数");
        foreach (var job in snapshot.Jobs)
            Console.WriteLine($"{job.Id} | {SafeText(job.Queue, 64)} | {SafeText(job.Name, 48)} | {SafeText(JobState.Label(job.State), 24)} | {job.Attempts}");
        if (snapshot.Jobs.Count == 0) Console.WriteLine("本页没有任务。");
        WriteMenu();
    }

    private static void WriteMenu()
    {
        Console.WriteLine("1 筛选（队列/状态）  N 下一页  P 前一页");
        Console.WriteLine("2 全部暂停  3 全部继续  4 清除剩余  5 按编号取消");
        Console.WriteLine("6 详情 JSON  7 单队列暂停/继续  8 编辑队列配置");
        Console.WriteLine("Q 退出管理（不停止后台）；Ctrl+C 也仅退出管理。");
    }

    private static string DispatchTime(long value, long now)
    {
        if (value <= 0) return "未设置";
        try
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            return value <= now ? time + "（已到）" : time;
        }
        catch (ArgumentOutOfRangeException) { return "时间无效"; }
    }

    private static bool Visible(Rune rune) => Rune.GetUnicodeCategory(rune) is not
        (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    private static string SafeText(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Visible(rune)) continue;
            if (result.Length + rune.Utf16SequenceLength > limit)
            {
                if (result.Length == limit)
                    result.Length -= char.IsLowSurrogate(result[^1]) ? 2 : 1;
                result.Append('…');
                break;
            }
            result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static string? SafeOptional(string? value, int limit) => value is null ? null : SafeText(value, limit);

    private sealed class InputException(string message) : Exception(message);

    private static bool Recoverable(Exception ex) => ex is InputException or CliException or DbException or
        IOException or UnauthorizedAccessException or JsonException or ArgumentException or
        InvalidOperationException or NotSupportedException or FormatException or OverflowException;

    private static string FriendlyError(Exception ex) => ex switch
    {
        InputException => SafeText(ex.Message, 240), // Only our own fixed validation messages.
        CliException => "队列操作未完成。请检查 queue-settings.json 是否有效、队列是否仍存在，或稍后重试。",
        DbException => "暂时无法访问队列数据库，可能被占用。请稍后重试；已生效的操作保留。",
        JsonException => "队列配置或已保存结果的 JSON 无效，请检查后重试。",
        UnauthorizedAccessException => "没有权限读取或保存队列数据，请检查目录权限。",
        IOException => "队列文件暂时无法读取或保存，请检查目录及文件占用情况。",
        _ => "操作未完成，请检查输入和队列状态后重试；已生效的操作保留。"
    };

    private sealed class Session(QueuePaths paths, QueueStore store, bool interactive, CancellationToken cancellation)
    {
        private bool _interactive = interactive;
        private string? _queue;
        private string? _state;
        private int _page = 1;
        private string _notice = "";

        public async Task RunAsync()
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                QueueSnapshot? snapshot = null;
                try
                {
                    snapshot = store.Snapshot(paths, paths.LoadSettings(), _queue, _state, _page);
                    if (_page > 1 && snapshot.Jobs.Count == 0)
                    {
                        _page = 1;
                        snapshot = store.Snapshot(paths, paths.LoadSettings(), _queue, _state, _page);
                    }
                }
                catch (Exception ex) when (Recoverable(ex)) { _notice = FriendlyError(ex); }

                ClearIfInteractive();
                if (snapshot is not null) Render(snapshot, _page);
                else { Console.WriteLine("暂时无法读取队列概览，仍可选择菜单或退出。"); WriteMenu(); }
                Console.WriteLine($"任务筛选：队列 {SafeText(_queue ?? "全部", 64)} / 状态 {SafeText(_state is null ? "全部" : JobState.Label(_state), 24)}（概览始终显示全部队列）");
                if (_notice.Length > 0) Console.WriteLine("提示：" + SafeText(_notice, 320));
                Console.WriteLine(_interactive ? "输入选项并回车；空闲每 3 秒刷新，输入及确认期间不刷新。" : "逐行输入选项并回车；重定向模式不清屏、不自动刷新。");
                string command;
                try { command = (await ReadInputAsync("选项 > ", refresh: true)).Trim().ToUpperInvariant(); }
                catch (InputException ex) { _notice = FriendlyError(ex); continue; }
                if (command == "Q") return;
                if (command.Length == 0) continue;
                try { _notice = await ExecuteAsync(command); }
                catch (Exception ex) when (Recoverable(ex)) { _notice = FriendlyError(ex); }
            }
        }

        private void ClearIfInteractive()
        {
            if (!_interactive) return;
            try { Console.Clear(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            { _interactive = false; }
        }

        private async Task<string> ReadInputAsync(string prompt, bool refresh = false)
        {
            cancellation.ThrowIfCancellationRequested();
            Console.Write(prompt);
            if (!_interactive) return await ReadLineAsync();
            var input = new StringBuilder();
            var editing = false;
            var refreshAt = Environment.TickCount64 + 3000;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                ConsoleKeyInfo? key = null;
                try { if (Console.KeyAvailable) key = Console.ReadKey(intercept: true); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
                {
                    _interactive = false;
                    Console.WriteLine("\n终端按键不可用，已切换逐行输入；请重新输入本项。");
                    Console.Write(prompt);
                    return await ReadLineAsync();
                }
                if (key is not { } pressed)
                {
                    if (refresh && !editing && Environment.TickCount64 >= refreshAt) return "";
                    await Task.Delay(50, cancellation);
                    continue;
                }
                editing = true; // Even after backspace to empty, never redraw an active prompt.
                if (pressed.Key == ConsoleKey.C && (pressed.Modifiers & ConsoleModifiers.Control) != 0)
                    throw new OperationCanceledException(cancellation);
                if (pressed.Key == ConsoleKey.Enter) { Console.WriteLine(); return input.ToString(); }
                if (pressed.Key == ConsoleKey.Backspace)
                {
                    if (input.Length > 0)
                    {
                        var c = input[^1];
                        input.Length--;
                        // All editable fields are IDs, numbers or menu words; cover common CJK input too.
                        Console.Write(c is >= '\u2e80' and <= '\uff60' ? "\b \b\b \b" : "\b \b");
                    }
                    continue;
                }
                var character = pressed.KeyChar;
                if (input.Length < 256 && !char.IsSurrogate(character) && Visible(new Rune(character)))
                { input.Append(character); Console.Write(character); }
            }
        }

        private async Task<string> ReadLineAsync()
        {
            // Console's synchronized reader can block even for ReadLineAsync. Wait only on the
            // UI's read task so Ctrl+C also exits while redirected input is waiting for a line.
            var line = await Task.Run(() => Console.ReadLine()).WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            Console.WriteLine(); // Redirected input does not echo its newline; keep JSON on its own line.
            if (line is null) throw new OperationCanceledException(cancellation); // EOF, including mid-confirmation.
            if (line.Length > 256) throw new InputException("输入过长，请限制在 256 个字符以内。");
            return line;
        }

        private async Task<string> ExecuteAsync(string command)
        {
            cancellation.ThrowIfCancellationRequested();
            switch (command)
            {
                case "1":
                    var queue = await ChooseQueueAsync(allowAll: true);
                    Console.WriteLine("状态：1 排队中 / 2 执行中 / 3 等待重试 / 4 成功 / 5 失败 / 6 结果待确认 / 7 已取消；留空或 * 为全部。");
                    var state = ParseState((await ReadInputAsync("状态（序号或状态名） > ")).Trim());
                    _queue = queue; _state = state; _page = 1;
                    return "筛选已更新。";
                case "N":
                    if (_page == int.MaxValue || store.List(_queue, _state, _page + 1).Count == 0) return "已到最后一页。";
                    _page++; return "";
                case "P":
                    if (_page == 1) return "已是第一页。";
                    _page--; return "";
                case "2":
                    store.Pause(null, true);
                    return "全部队列已暂停发送；正在执行的任务保留。";
                case "3":
                    // Global resume also clears individual pause flags; Enabled remains configuration-owned.
                    var settings = paths.LoadSettings();
                    foreach (var definition in settings.Queues)
                    { cancellation.ThrowIfCancellationRequested(); store.Pause(definition.Id, false); }
                    store.Pause(null, false);
                    return "已解除全部暂停（禁用配置不变）。" + await StartWorkerAsync();
                case "4": return await ClearRemainingAsync();
                case "5": return await CancelJobAsync();
                case "6": return await ShowDetailsAsync();
                case "7":
                    var id = await ChooseQueueAsync();
                    if (id is null) return "已返回，未更改队列。";
                    var action = (await ReadInputAsync("1 暂停 / 2 继续（留空返回） > ")).Trim().ToUpperInvariant();
                    if (action.Length == 0) return "未更改队列。";
                    if (action is not ("1" or "2" or "P" or "R" or "暂停" or "继续"))
                        throw new InputException("请选择 1 暂停或 2 继续。");
                    var paused = action is "1" or "P" or "暂停";
                    store.Pause(id, paused);
                    return paused ? "队列已暂停；正在执行的任务保留。" :
                        "队列已继续；若全局仍暂停或配置禁用，仍不会发送。" + await StartWorkerAsync();
                case "8": return await EditSettingsAsync();
                default: return "无法识别选项，请输入 1..8、N、P 或 Q。";
            }
        }

        private async Task<string> StartWorkerAsync()
        {
            try
            {
                // Cancelling this wait must not cancel or kill the detached worker.
                var running = await QueueWorkerHost.EnsureStartedAsync(paths).WaitAsync(cancellation);
                return running || paths.WorkerRunning() ? "后台已运行。" : "尚未确认后台运行，请稍后再次选择继续。";
            }
            catch (Exception ex) when (Recoverable(ex))
            { return "暂停状态已更新，但后台启动未成功；请检查队列配置及启动权限后再次选择继续。"; }
        }

        private async Task<string?> ChooseQueueAsync(bool allowAll = false)
        {
            var settings = paths.LoadSettings();
            Console.WriteLine("可选队列 ID：");
            foreach (var queue in settings.Queues) Console.WriteLine("  " + SafeText(queue.Id, 64));
            var id = (await ReadInputAsync(allowAll ? "队列 ID（留空或 * 为全部） > " : "队列 ID（留空返回） > ")).Trim();
            if (id.Length == 0 || allowAll && (id is "*" or "全部")) return null;
            return settings.Queues.FirstOrDefault(q => q.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new InputException("没有找到该队列，请使用配置中的完整 ID。");
        }

        private static string? ParseState(string value) => value.ToLowerInvariant() switch
        {
            "" or "*" or "全部" => null,
            "1" or JobState.Pending or "排队" or "排队中" => JobState.Pending,
            "2" or JobState.Running or "执行中" => JobState.Running,
            "3" or JobState.Retry or "重试" or "等待重试" => JobState.Retry,
            "4" or JobState.Succeeded or "成功" => JobState.Succeeded,
            "5" or JobState.Failed or "失败" => JobState.Failed,
            "6" or JobState.Unknown or "结果待确认" => JobState.Unknown,
            "7" or JobState.Cancelled or "已取消" => JobState.Cancelled,
            _ => throw new InputException("状态无效，请使用列出的序号、中文状态或数据库状态名。")
        };

        private async Task<long?> ReadJobIdAsync()
        {
            var input = (await ReadInputAsync("数据库任务 ID（不是行号；留空返回） > ")).Trim();
            if (input.Length == 0) return null;
            if (!long.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InputException("任务 ID 必须是正整数，请使用任务表中的数据库编号。");
            return id;
        }

        private async Task<string> CancelJobAsync()
        {
            var id = await ReadJobIdAsync();
            if (id is null) return "未取消任务。";
            var job = store.Get(id.Value);
            if (job is null) return "找不到该任务编号。";
            if (job.State == JobState.Running) return "执行中的任务不可取消，将保留到执行结束。";
            if (job.State is not (JobState.Pending or JobState.Retry)) return "只能取消排队中或等待重试的任务。";
            cancellation.ThrowIfCancellationRequested();
            return store.Cancel(id.Value) == 1 ? $"任务 {id.Value} 已取消。" : "任务状态已变化；执行中的任务不可取消，未强制中止。";
        }

        private async Task<string> ClearRemainingAsync()
        {
            // Freeze IDs before confirmation. Cancel() without an ID could also cancel newly
            // submitted jobs the user never confirmed. Its state predicate handles claim races.
            var ids = new HashSet<long>();
            foreach (var state in new[] { JobState.Pending, JobState.Retry })
            {
                for (var page = 1; ; page++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var jobs = store.List(state: state, page: page);
                    foreach (var job in jobs) ids.Add(job.Id);
                    if (jobs.Count < 20) break;
                }
            }
            if (ids.Count == 0) return "没有可清除的排队或重试任务。";
            Console.WriteLine($"二次确认：取消全部队列当前读取到的 {ids.Count} 个排队/重试任务（忽略当前筛选）。");
            Console.WriteLine("正在执行的任务保留；历史结果不删除；确认后新增任务不在本次范围内。");
            var confirmation = (await ReadInputAsync($"输入 Y 或数量 {ids.Count} 确认，其他输入返回 > ")).Trim();
            if (!confirmation.Equals("Y", StringComparison.OrdinalIgnoreCase) &&
                !confirmation.Equals("YES", StringComparison.OrdinalIgnoreCase) &&
                confirmation != ids.Count.ToString(CultureInfo.InvariantCulture)) return "未取消任何任务。";
            var affected = 0;
            foreach (var id in ids)
            { cancellation.ThrowIfCancellationRequested(); affected += store.Cancel(id); }
            return $"已取消 {affected}/{ids.Count} 个已确认任务；已进入执行或其他状态的任务保留。";
        }

        private async Task<string> ShowDetailsAsync()
        {
            var id = await ReadJobIdAsync();
            if (id is null) return "已返回。";
            var job = store.Get(id.Value);
            QueueReply reply;
            if (job is null) reply = new(false, "找不到该任务编号。", JobId: id);
            else
            {
                var safeJob = job with
                {
                    Queue = SafeText(job.Queue, 64), Name = SafeText(job.Name, 160), State = SafeText(job.State, 32),
                    Error = SafeOptional(job.Error, 512), Result = SafeResult(job.Result)
                };
                var attempts = store.Attempts(id.Value).Select(a => a with
                {
                    RequestId = SafeOptional(a.RequestId, 160), Stage = SafeText(a.Stage, 64), Error = SafeOptional(a.Error, 512)
                }).ToList();
                reply = new(true, "任务详情（安全字段；长文本已限长，不包含请求载荷或原始服务响应）。", JobId: id, Job: safeJob, Attempts: attempts);
            }
            Console.WriteLine(JsonSerializer.Serialize(reply, QueueJsonContext.Default.QueueReply));
            // A script's next line is its next menu command, not an acknowledgement.
            if (_interactive) await ReadInputAsync("按回车返回菜单 > ");
            return "详情已输出。";
        }

        private async Task<string> EditSettingsAsync()
        {
            var id = await ChooseQueueAsync();
            if (id is null) return "未修改配置。";
            var current = paths.LoadSettings().Queues.FirstOrDefault(q => q.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InputException("该队列已被其他界面移除，请重新选择。");
            Console.WriteLine("留空保留保存时的最新值；仅提交本次明确输入的字段，不修改模型和启用状态。");
            var rpm = await ReadNumberAsync("RPM", current.RequestsPerMinute, 1, 10000);
            var concurrency = await ReadNumberAsync("并发上限", current.MaxConcurrency, 1, 64);
            var retries = await ReadNumberAsync("额外重试数", current.RetryCount, 0, 100);
            var delay = await ReadNumberAsync("重试等待秒数", current.RetryDelaySeconds, 1, 86400);
            if (rpm is null && concurrency is null && retries is null && delay is null) return "全部留空，配置未修改。";
            // Never hold the settings lease while a person is typing.
            using var lease = await paths.LockAsync("settings", cancellation);
            var settings = paths.LoadSettings();
            var target = settings.Queues.FirstOrDefault(q => q.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InputException("该队列已被其他界面移除，未保存。请重新选择。");
            if (rpm.HasValue) target.RequestsPerMinute = rpm.Value;
            if (concurrency.HasValue) target.MaxConcurrency = concurrency.Value;
            if (retries.HasValue) target.RetryCount = retries.Value;
            if (delay.HasValue) target.RetryDelaySeconds = delay.Value;
            cancellation.ThrowIfCancellationRequested();
            paths.SaveSettings(settings);
            return "队列配置已保存；未更改任何暂停或启用状态。";
        }

        private async Task<int?> ReadNumberAsync(string label, int current, int minimum, int maximum)
        {
            var text = (await ReadInputAsync($"{label} [{current}]（{minimum}..{maximum}，留空保留） > ")).Trim();
            if (text.Length == 0) return null;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
                throw new InputException($"{label} 必须为 {minimum}..{maximum} 的整数；本次配置未保存。");
            return number;
        }
    }

    private static JsonElement? SafeResult(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value) return null;
        // QueueStore persists CliReport.Serialize(), not the HTTP body. Keep its safe report
        // fields only; never expose future payload/credential/raw-response additions by default.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is not ("ok" or "exit_code" or "files" or "mode" or "http_status" or
                    "request_id" or "elapsed_ms" or "usage" or "api_error" or "error" or "response_metadata")) continue;
                writer.WritePropertyName(property.Name);
                WriteSafeJson(writer, property.Value, 0);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteSafeJson(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 8) { writer.WriteNullValue(); return; }
        switch (value.ValueKind)
        {
            case JsonValueKind.String: writer.WriteStringValue(SafeText(value.GetString(), 2048)); break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().Take(128))
                { writer.WritePropertyName(SafeText(property.Name, 64)); WriteSafeJson(writer, property.Value, depth + 1); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray().Take(128)) WriteSafeJson(writer, item, depth + 1);
                writer.WriteEndArray(); break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null: value.WriteTo(writer); break;
            default: writer.WriteNullValue(); break;
        }
    }
}