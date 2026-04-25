namespace TrueFluentPro.Models
{
    /// <summary>
    /// 任务 staging 状态。
    /// </summary>
    public enum TaskStagingStatus
    {
        Pending = 0,
        Running = 1,
        Landed = 2,
        Failed = 3,
        Cancelled = 4,
        Committed = 5
    }

    /// <summary>
    /// Staging 表记录——追踪每个生成任务的完整生命周期，含失败和取消。
    /// </summary>
    public sealed class TaskStagingEntry
    {
        public required string TaskId { get; init; }
        public required string SessionId { get; init; }
        public required string TaskType { get; init; }
        public TaskStagingStatus Status { get; set; }
        public required string Prompt { get; init; }

        // 错误追踪
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }

        // 成本追踪
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public double? EstimatedCostUsd { get; set; }

        // 时间线
        public required string CreatedAt { get; init; }
        public string? StartedAt { get; set; }
        public string? FinishedAt { get; set; }

        // 文件信息
        public string? ResultFilePath { get; set; }
        public long? FileSize { get; set; }

        // 视频专用
        public string? RemoteVideoId { get; set; }
        public string? RemoteGenerationId { get; set; }
    }
}
