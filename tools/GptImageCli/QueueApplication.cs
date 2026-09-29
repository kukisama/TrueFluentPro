using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace GptImageCli;

internal static class QueueApplication
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "submit" or "queue";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        long? submittedId = null;
        try
        {
            var remaining = args.ToList();
            var directory = Extract(remaining, "--queue-dir");
            if (remaining.Contains("--help"))
            {
                await (json ? error : output).WriteLineAsync("""
                    队列用法（Windows 10/11，本机当前用户共享）：
                      gpt-image submit [现有生图参数] [--name 任务名称] [--json]
                      gpt-image queue                         打开交互维护界面
                      gpt-image queue list [--queue ID] [--state 状态] [--page N] [--json]
                      gpt-image queue show 编号 [--json]       查看结果与尝试记录
                      gpt-image queue pause|resume [队列ID]    省略ID表示全局
                      gpt-image queue cancel 编号             仅取消排队或等待重试任务
                      gpt-image queue clear --yes [--queue ID] 取消剩余作业，保留执行中及图片
                      gpt-image queue config                  显示配置文件路径与内容
                      gpt-image queue start                   启动后台并恢复任务
                      gpt-image queue worker                  前台诊断执行器，Ctrl+C停止派发并等待在途任务
                    通用 --queue-dir 本机目录 可隔离队列；默认 %LOCALAPPDATA%/GptImageCli。
                    配置 queue-settings.json：每队列 models、requestsPerMinute、maxConcurrency、retryCount、retryDelaySeconds、enabled。
                    状态：pending/running/retry_wait/succeeded/failed/unknown/cancelled。
                    submit 成功仅表示入队；生成结果请用 queue show 查询。旧同步调用仍兼容且不自动重试。
                    """);
                if (json) await WriteAsync(output, new(true, "队列帮助已输出到 stderr。"));
                return 0;
            }
            var paths = new QueuePaths(directory);
            var settings = paths.LoadSettings();
            var store = new QueueStore(paths);
            QueueReply reply;
            if (remaining[0] == "submit")
            {
                remaining.RemoveAt(0);
                var name = Extract(remaining, "--name");
                if (remaining.Any(a => a is "--list-endpoints" or "--help")) throw new CliException("submit 仅接受生图参数。");
                var options = await CommandLineParser.ParseAsync(remaining.ToArray());
                using var gate = await paths.LockAsync("lifecycle");
                if (store.ActiveQueues().Any(q => settings.Queues.All(p => p.Id != q)))
                    throw new CliException("配置移除了仍有未完成任务的队列；请恢复对应 id 或取消剩余作业后再提交。未入队。");
                if (paths.WorkerRunning()) await QueueWorkerHost.EnsureStartedUnderLockAsync(paths);
                submittedId = QueueSubmission.Submit(paths, store, settings, options, name);
                var active = await QueueWorkerHost.EnsureStartedUnderLockAsync(paths);
                reply = new(true, "任务已入队；后台按配置调度，入队不代表图片已生成。", submittedId, active);
            }
            else
            {
                remaining.RemoveAt(0);
                remaining.RemoveAll(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
                var command = remaining.Count > 0 ? remaining[0] : "console";
                if (remaining.Count > 0) remaining.RemoveAt(0);
                switch (command)
                {
                    case "console":
                        RequireCount(remaining, 0);
                        if (json) throw new CliException("交互管理不支持 --json；请用 queue list --json。");
                        return await QueueConsole.RunAsync(paths, store);
                    case "list":
                        var filter = Extract(remaining, "--queue");
                        var state = Extract(remaining, "--state");
                        var page = ParsePositive(Extract(remaining, "--page") ?? "1");
                        RequireCount(remaining, 0);
                        var snapshot = store.Snapshot(paths, settings, filter, state, page);
                        if (!json) { QueueConsole.Render(snapshot, page); return 0; }
                        reply = new(true, "队列状态。", Snapshot: snapshot); break;
                    case "show":
                        RequireCount(remaining, 1);
                        var id = ParseId(remaining[0]);
                        reply = new(true, "任务详情。", Job: store.Get(id) ?? throw new CliException("未找到任务编号。"), Attempts: store.Attempts(id)); break;
                    case "pause":
                    case "resume":
                        if (remaining.Count > 1) throw new CliException("pause/resume 最多指定一个队列 ID。");
                        var queue = remaining.FirstOrDefault();
                        if (queue is not null && settings.Queues.All(q => q.Id != queue)) throw new CliException("未知队列 ID。");
                        store.Pause(queue, command == "pause");
                        if (command == "resume") await QueueWorkerHost.EnsureStartedAsync(paths);
                        reply = new(true, command == "pause" ? "已暂停派发；在途请求继续完成。" : "已继续派发（仍遵守限速与冷却）。"); break;
                    case "cancel":
                        RequireCount(remaining, 1);
                        var affected = store.Cancel(ParseId(remaining[0]));
                        if (affected == 0) throw new CliException("未取消任务：编号不存在，或任务已执行/结束；执行中请求不能保证撤回。");
                        reply = new(true, "任务已取消；保留历史及图片。", Affected: affected); break;
                    case "clear":
                        var group = Extract(remaining, "--queue");
                        if (remaining.Count != 1 || remaining[0] != "--yes") throw new CliException("请显式使用 queue clear --yes 确认取消剩余作业。");
                        reply = new(true, "已取消未执行作业；保留执行中任务、历史及图片。", Affected: store.Cancel(queue: group)); break;
                    case "config":
                        RequireCount(remaining, 0);
                        reply = new(true, "直接编辑此配置；后台约一秒内重新加载，非法配置不会覆盖运行中的有效设置。", Settings: settings, SettingsPath: paths.SettingsFile); break;
                    case "start":
                        RequireCount(remaining, 0);
                        reply = new(true, "后台已启动；无未完成任务时自动退出。", WorkerRunning: await QueueWorkerHost.EnsureStartedAsync(paths)); break;
                    case "worker":
                        RequireCount(remaining, 0);
                        using (var stop = new CancellationTokenSource())
                        {
                            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
                            Console.CancelKeyPress += cancel;
                            try { return await QueueWorker.RunAsync(paths, stop.Token); }
                            finally { Console.CancelKeyPress -= cancel; }
                        }
                    default: throw new CliException("未知队列命令；请运行 gpt-image queue --help。");
                }
            }
            if (json || reply.Job is not null || reply.Settings is not null) await WriteAsync(output, reply);
            else await output.WriteLineAsync(reply.Message + (reply.JobId is { } id ? $" 编号：{id}" : "") +
                (reply.Affected is { } count ? $" 数量：{count}" : ""));
            return 0;
        }
        catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException or SqliteException or
            CryptographicException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            var message = ex is CliException ? ex.Message : "队列操作失败；请检查本机目录权限、数据库、配置及当前 Windows 用户。";
            await error.WriteLineAsync(message);
            if (json) await WriteAsync(output, new(false, message, submittedId, false));
            return submittedId is null ? 2 : 1;
        }
    }

    private static Task WriteAsync(TextWriter output, QueueReply reply) =>
        output.WriteLineAsync(JsonSerializer.Serialize(reply, QueueJsonContext.Default.QueueReply));
    private static string? Extract(List<string> args, string name)
    {
        string? result = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 == args.Count || args[i + 1].StartsWith("--")) throw new CliException(name + " 缺少参数值。");
                result = args[i + 1]; args.RemoveRange(i, 2); i--;
            }
            else if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            { result = args[i][(name.Length + 1)..]; args.RemoveAt(i--); }
        }
        if (result is not null && string.IsNullOrWhiteSpace(result)) throw new CliException(name + " 不能为空。");
        return result;
    }
    private static void RequireCount(List<string> args, int count)
    { if (args.Count != count) throw new CliException("队列命令参数数量不正确；请运行 queue --help。"); }
    private static long ParseId(string value) => long.TryParse(value, out var id) && id > 0 ? id : throw new CliException("任务编号必须是正整数。");
    private static int ParsePositive(string value) => int.TryParse(value, out var number) && number > 0 ? number : throw new CliException("页码必须是正整数。");
}