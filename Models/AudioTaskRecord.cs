using System;

namespace TrueFluentPro.Models
{
    /// <summary>音频任务队列记录 — 对应 audio_task_queue 表中的一行。</summary>
    public class AudioTaskRecord
    {
        /// <summary>ULID，全局唯一任务标识。</summary>
        public string TaskId { get; set; } = "";

        /// <summary>关联的音频库项目 ID（FK → audio_library_items.id）。</summary>
        public string AudioItemId { get; set; } = "";

        /// <summary>处理阶段（AudioLifecycleStage 枚举值的字符串表示）。</summary>
        public string Stage { get; set; } = "";

        /// <summary>任务状态。</summary>
        public AudioTaskStatus Status { get; set; } = AudioTaskStatus.Pending;

        /// <summary>优先级（0=普通，数字越大越优先）。</summary>
        public int Priority { get; set; }

        /// <summary>前置依赖的 task_id 列表（JSON 数组），null 表示无依赖。</summary>
        public string? DependsOn { get; set; }

        /// <summary>失败时的错误信息。</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>当前进度描述（例如"上传音频到服务器"、"等待AI返回"等），用于任务监控面板展示。</summary>
        public string? ProgressMessage { get; set; }

        /// <summary>已重试次数。</summary>
        public int RetryCount { get; set; }

        /// <summary>提交时间（ISO 8601）。</summary>
        public DateTime SubmittedAt { get; set; }

        /// <summary>开始执行时间。</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>完成/失败时间。</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>提交者标识（未来多用户场景预留）。</summary>
        public string? SubmittedBy { get; set; }
    }
}
