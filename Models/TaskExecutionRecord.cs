using System;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 任务执行记录 — 每次 API 调用产生一条，用于对账和执行历史审计。
    /// </summary>
    public class TaskExecutionRecord
    {
        public string ExecutionId { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string AudioItemId { get; set; } = "";
        public string Stage { get; set; } = "";
        public string Status { get; set; } = "Running";  // Running / Completed / Failed / Interrupted / Cancelled
        public bool Billable { get; set; }
        public string? CancelReason { get; set; }         // user / config_changed / app_restart / null
        public string? ModelName { get; set; }
        public int? TokensIn { get; set; }                // null=未知, 0=确认为零
        public int? TokensOut { get; set; }
        public int? DurationMs { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        /// <summary>调试模式下记录的完整提示词（system + user）。</summary>
        public string? DebugPrompt { get; set; }
        /// <summary>调试模式下记录的 AI 完整响应。</summary>
        public string? DebugResponse { get; set; }
    }

    /// <summary>按 task_id 汇总的执行统计。</summary>
    public class TaskExecutionSummary
    {
        public int TotalExecutions { get; set; }
        public int RetryCount => Math.Max(0, TotalExecutions - 1);
        public int? BillableTokensIn { get; set; }
        public int? BillableTokensOut { get; set; }
        public int? BillableDurationMs { get; set; }
        public int NonBillableExecutions { get; set; }
    }
}
