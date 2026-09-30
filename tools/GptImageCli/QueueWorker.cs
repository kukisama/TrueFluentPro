using System.Security.Cryptography;
using System.Text.Json;

namespace GptImageCli;

internal static class QueueWorker
{
    public static async Task<int> RunAsync(QueuePaths paths, CancellationToken cancellation = default,
        HttpMessageHandler? handler = null)
    {
        // File lease is process-wide, works across await continuations and is released on process death.
        FileStream? lease = paths.TryLock("worker");
        if (lease is null) return 0;
        var running = new List<Task>();
        try
        {
            var store = new QueueStore(paths);
            var settings = paths.LoadSettings();
            store.RecoverInterrupted();
            store.Log("worker_started", "后台执行器启动。");
            QueueWorkerHost.WriteReady(paths, store);
            string? lastConfigError = null;
            long lastLoad = 0;
            while (!cancellation.IsCancellationRequested)
            {
                foreach (var finished in running.Where(t => t.IsCompleted).ToArray())
                { await finished; running.Remove(finished); }
                var now = QueueStore.Now;
                if (now - lastLoad >= 1000)
                {
                    lastLoad = now;
                    try
                    {
                        var updated = paths.LoadSettings();
                        var active = store.ActiveQueues();
                        if (active.Any(q => updated.Queues.All(p => p.Id != q)))
                            throw new CliException("配置移除了仍有未完成任务的队列；请保留其 id，或先取消剩余任务。");
                        settings = updated;
                        if (lastConfigError is not null) store.Log("settings_recovered", "队列配置恢复有效。");
                        lastConfigError = null;
                    }
                    catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException)
                    {
                        const string message = "队列配置无效、不可读或移除了活动队列；保持上一份有效配置。";
                        if (lastConfigError != message) store.Log("settings_error", message);
                        lastConfigError = message;
                    }
                }
                foreach (var policy in settings.Queues)
                {
                    if (cancellation.IsCancellationRequested) break;
                    var job = store.TryClaim(policy, QueueStore.Now);
                    if (job is not null) running.Add(ExecuteJobAsync(store, job, policy, handler));
                }
                if (running.Count == 0 && !store.HasWork())
                {
                    // Submitters take the same gate before committing and checking the worker lease.
                    using var gate = await paths.LockAsync("lifecycle", cancellation);
                    if (!store.HasWork())
                    {
                        store.Log("worker_stopped", "可执行任务已全部处理完毕。");
                        lease.Dispose(); lease = null;
                        return 0;
                    }
                }
                await Task.Delay(200, cancellation);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        finally
        {
            // Graceful shutdown stops dispatch; already sent requests finish before releasing the singleton.
            try { await Task.WhenAll(running); }
            finally { lease?.Dispose(); }
        }
    }

    private static async Task ExecuteJobAsync(QueueStore store, ClaimedJob job, QueueDefinition policy, HttpMessageHandler? handler)
    {
        var report = new CliReport();
        var exit = 1;
        try
        {
            var options = QueueSubmission.Decode(job.Payload);
            ParameterValidation.Validate(options);
            exit = await CliApplication.ExecuteAsync(options, TextWriter.Null, TextWriter.Null, report, handler);
            report.RedactTransportMetadata(options.ApiKey);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or CliException or IOException or UnauthorizedAccessException)
        {
            report.Error = "无法读取或解密任务参数/输入；请确认使用提交任务的 Windows 用户及完整输入文件。";
        }
        // Storage failures are fatal to the worker, not a reason to resubmit a paid request.
        store.Complete(job, policy, report, exit, QueueStore.Now);
    }
}