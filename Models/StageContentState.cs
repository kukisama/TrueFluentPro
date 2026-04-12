namespace TrueFluentPro.Models
{
    /// <summary>
    /// 每个生命周期阶段在 UI 层的显示状态（三态）。
    /// </summary>
    public enum StageContentState
    {
        /// <summary>无数据（未生成）</summary>
        Empty,

        /// <summary>有 Pending/Running 的任务正在处理</summary>
        Processing,

        /// <summary>有已完成的 lifecycle 数据</summary>
        Ready,
    }
}
