using System.Text.Json;
using System.Text.Json.Serialization;

namespace GptImageCli;

internal sealed class QueueSettings
{
    public int Version { get; set; }
    public List<QueueDefinition> Queues { get; set; } = [];

    public void Validate()
    {
        if (Version != 1 || Queues is null || Queues.Count is < 1 or > 64)
            throw new CliException("队列配置 version 必须为 1，queues 必须包含 1..64 个队列。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var queue in Queues)
        {
            if (queue is null || string.IsNullOrEmpty(queue.Id) || queue.Id.Length > 64 ||
                !queue.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') || !ids.Add(queue.Id))
                throw new CliException("队列 id 必须唯一，只能包含英文字母、数字、点、横线或下划线（最多 64 字符）。");
            if (queue.Models is null || queue.Models.Count == 0 || queue.Models.Any(m =>
                string.IsNullOrWhiteSpace(m) || m.Length > 256 || m.Any(char.IsControl) || m != m.Trim() || !models.Add(m)))
                throw new CliException("每个队列必须配置 models；模型名称不能重复、为空或包含控制字符。");
            if (queue.RequestsPerMinute is < 1 or > 10000 || queue.MaxConcurrency is < 1 or > 64 ||
                queue.RetryCount is < 0 or > 100 || queue.RetryDelaySeconds is < 1 or > 86400)
                throw new CliException("队列范围：requestsPerMinute 1..10000，maxConcurrency 1..64，retryCount 0..100，retryDelaySeconds 1..86400。");
        }
    }

    public QueueDefinition ForModel(string model) => Queues.SingleOrDefault(q => q.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
        ?? throw new CliException("该模型尚未配置队列；请在 queue-settings.json 的 models 中添加模型及其配额。未入队。");
}

internal sealed class QueueDefinition
{
    public string Id { get; set; } = "";
    public List<string> Models { get; set; } = [];
    public int RequestsPerMinute { get; set; }
    public int MaxConcurrency { get; set; }
    public int RetryCount { get; set; }
    public int RetryDelaySeconds { get; set; }
    public bool Enabled { get; set; } = true;
}

internal static class JobState
{
    public const string Pending = "pending", Running = "running", Retry = "retry_wait",
        Succeeded = "succeeded", Failed = "failed", Unknown = "unknown", Cancelled = "cancelled";
    public static bool IsActive(string state) => state is Pending or Running or Retry;
    public static string Label(string state) => state switch
    {
        Pending => "排队中", Running => "执行中", Retry => "等待重试", Succeeded => "成功",
        Failed => "失败", Unknown => "结果待确认", Cancelled => "已取消", _ => state
    };
}

internal sealed record QueueJob(long Id, string Queue, string Name, string State, int Attempts,
    long CreatedAt, long UpdatedAt, long NextAttemptAt, int? HttpStatus, string? Error, JsonElement? Result);
internal sealed record QueueAttempt(long JobId, int Number, long StartedAt, long? FinishedAt,
    int? HttpStatus, string? RequestId, string Stage, string? Error);
internal sealed record ClaimedJob(long Id, string Queue, int Attempt, byte[] Payload);
internal sealed record QueueWorkerInfo(int ProtocolVersion, int ProcessId, long ProcessStartedAt, string BinaryHash);
internal sealed record QueueOverview(string Id, int RequestsPerMinute, int MaxConcurrency, int RetryCount,
    int RetryDelaySeconds, bool Enabled, bool Paused, long NextDispatchAt, int SentLastMinute,
    int Pending, int Running, int RetryWaiting, int Succeeded, int Failed, int Unknown, int Cancelled);
internal sealed record QueueSnapshot(bool WorkerRunning, bool Paused, string SettingsPath,
    List<QueueOverview> Queues, List<QueueJob> Jobs);
internal sealed record QueueReply(bool Ok, string Message, long? JobId = null, bool? WorkerRunning = null,
    QueueSnapshot? Snapshot = null, QueueJob? Job = null, List<QueueAttempt>? Attempts = null,
    QueueSettings? Settings = null, string? SettingsPath = null, int? Affected = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QueueSettings))]
[JsonSerializable(typeof(QueueReply))]
[JsonSerializable(typeof(QueueWorkerInfo))]
[JsonSerializable(typeof(CliOptions))]
internal partial class QueueJsonContext : JsonSerializerContext;