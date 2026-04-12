using System;
using Avalonia.Threading;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>任务状态变更事件数据。</summary>
    public record TaskStatusChangedEvent(
        string TaskId,
        string AudioItemId,
        AudioLifecycleStage Stage,
        AudioTaskStatus OldStatus,
        AudioTaskStatus NewStatus,
        string? ErrorMessage = null
    );

    /// <summary>任务进度更新事件数据（不改变状态，仅更新进度描述）。</summary>
    public record TaskProgressEvent(
        string TaskId,
        string AudioItemId,
        AudioLifecycleStage Stage,
        string ProgressMessage
    );

    /// <summary>
    /// 任务事件总线接口 — 用于发布/订阅任务状态变更事件。
    /// 本地实现使用内存 event；未来云端可替换为 SignalR client。
    /// </summary>
    public interface ITaskEventBus
    {
        /// <summary>任务状态变更事件。</summary>
        event Action<TaskStatusChangedEvent>? TaskStatusChanged;

        /// <summary>任务进度更新事件（Running 状态下的详细进度描述）。</summary>
        event Action<TaskProgressEvent>? TaskProgressUpdated;

        /// <summary>发布任务状态变更事件。</summary>
        void Publish(TaskStatusChangedEvent e);

        /// <summary>发布任务进度更新事件。</summary>
        void PublishProgress(TaskProgressEvent e);
    }

    /// <summary>
    /// 内存版任务事件总线实现 — 将事件调度到 UI 线程。
    /// </summary>
    public sealed class TaskEventBus : ITaskEventBus
    {
        public event Action<TaskStatusChangedEvent>? TaskStatusChanged;
        public event Action<TaskProgressEvent>? TaskProgressUpdated;

        public void Publish(TaskStatusChangedEvent e)
        {
            if (TaskStatusChanged == null) return;

            // 如果已在 UI 线程，直接触发；否则 Post 到 UI 线程
            if (Dispatcher.UIThread.CheckAccess())
            {
                TaskStatusChanged.Invoke(e);
            }
            else
            {
                Dispatcher.UIThread.Post(() => TaskStatusChanged?.Invoke(e));
            }
        }

        public void PublishProgress(TaskProgressEvent e)
        {
            if (TaskProgressUpdated == null) return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                TaskProgressUpdated.Invoke(e);
            }
            else
            {
                Dispatcher.UIThread.Post(() => TaskProgressUpdated?.Invoke(e));
            }
        }
    }
}
