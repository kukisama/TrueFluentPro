namespace TrueFluentPro.Models
{
    /// <summary>音频任务状态枚举 — 任务队列中每个任务的生命周期状态。</summary>
    public enum AudioTaskStatus
    {
        /// <summary>已提交，等待调度</summary>
        Pending,

        /// <summary>执行中</summary>
        Running,

        /// <summary>已完成，结果已写入 audio_lifecycle</summary>
        Completed,

        /// <summary>执行失败，error_message 记录原因</summary>
        Failed,

        /// <summary>用户主动取消</summary>
        Cancelled,
    }
}
