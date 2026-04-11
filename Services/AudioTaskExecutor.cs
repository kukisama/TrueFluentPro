using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 音频任务执行器接口 — 后台调度循环，从队列取任务执行。
    /// </summary>
    public interface IAudioTaskExecutor
    {
        /// <summary>启动后台调度循环。</summary>
        Task StartAsync(CancellationToken appShutdown);

        /// <summary>通知有新任务入队，唤醒调度循环。</summary>
        void NotifyNewTask();
    }

    /// <summary>
    /// 音频任务执行器实现 — 信号量控制并发，DAG 依赖检查，独立 CancellationToken per task。
    /// </summary>
    public sealed class AudioTaskExecutor : IAudioTaskExecutor
    {
        /// <summary>最大并发执行任务数。</summary>
        private const int MaxConcurrency = 2;

        /// <summary>任务超时阈值（分钟），启动时用于恢复卡死的 Running 任务。</summary>
        private const int StallThresholdMinutes = 30;

        private readonly IAudioTaskRepository _taskRepo;
        private readonly IAudioLifecycleRepository _lifecycleRepo;
        private readonly ITaskEventBus _eventBus;
        private readonly SemaphoreSlim _concurrencySlot = new(MaxConcurrency, MaxConcurrency);
        private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
        private readonly Dictionary<string, CancellationTokenSource> _runningCts = new();
        private readonly object _ctsLock = new();

        public AudioTaskExecutor(
            IAudioTaskRepository taskRepo,
            IAudioLifecycleRepository lifecycleRepo,
            ITaskEventBus eventBus)
        {
            _taskRepo = taskRepo;
            _lifecycleRepo = lifecycleRepo;
            _eventBus = eventBus;
        }

        public void NotifyNewTask()
        {
            try { _wakeSignal.Release(); }
            catch (SemaphoreFullException) { /* 已有待处理信号 */ }
        }

        public async Task StartAsync(CancellationToken appShutdown)
        {
            // 启动时恢复卡死的 Running 任务
            var recovered = _taskRepo.RecoverStalledRunningTasks(TimeSpan.FromMinutes(StallThresholdMinutes));
            if (recovered > 0)
            {
                Debug.WriteLine($"[AudioTaskExecutor] 恢复 {recovered} 个卡死的 Running 任务");
            }

            // 主调度循环
            while (!appShutdown.IsCancellationRequested)
            {
                try
                {
                    // 等待新任务信号或超时（定期轮询兜底）
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(5), appShutdown);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 尝试调度所有可执行的任务
                await TryScheduleTasksAsync(appShutdown);
            }
        }

        private async Task TryScheduleTasksAsync(CancellationToken appShutdown)
        {
            while (!appShutdown.IsCancellationRequested)
            {
                // 等待并发槽位
                if (_concurrencySlot.CurrentCount == 0)
                    return;

                // 从 DB 取下一个 Pending 任务
                var task = _taskRepo.GetNextPendingTask();
                if (task == null)
                    return;

                // DAG 依赖检查
                if (!AreDependenciesSatisfied(task))
                    return; // 依赖未满足，等待下次调度

                // 占用并发槽
                if (!await _concurrencySlot.WaitAsync(0, appShutdown))
                    return;

                // 标记为 Running
                var oldStatus = task.Status;
                _taskRepo.MarkRunning(task.TaskId);

                if (Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage))
                {
                    _eventBus.Publish(new TaskStatusChangedEvent(
                        task.TaskId, task.AudioItemId, stage,
                        oldStatus, AudioTaskStatus.Running));
                }

                // 创建独立的 CancellationToken
                var cts = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
                lock (_ctsLock) { _runningCts[task.TaskId] = cts; }

                // 在线程池执行任务（不阻塞调度循环）
                var capturedTask = task;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteTaskAsync(capturedTask, cts.Token);
                    }
                    finally
                    {
                        lock (_ctsLock) { _runningCts.Remove(capturedTask.TaskId); }
                        cts.Dispose();
                        _concurrencySlot.Release();

                        // 任务完成后，唤醒调度循环以检查依赖此任务的后续任务
                        NotifyNewTask();
                    }
                }, appShutdown);
            }
        }

        private async Task ExecuteTaskAsync(AudioTaskRecord task, CancellationToken ct)
        {
            if (!Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage))
            {
                _taskRepo.MarkFailed(task.TaskId, $"无法解析阶段: {task.Stage}");
                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, stage,
                    AudioTaskStatus.Running, AudioTaskStatus.Failed,
                    $"无法解析阶段: {task.Stage}"));
                return;
            }

            try
            {
                // ── 实际任务执行占位（Phase 3 将填充具体的生成逻辑） ──
                // 目前作为框架，只做状态流转验证
                await Task.Delay(100, ct); // 模拟最小延迟

                // 标记完成
                _taskRepo.MarkCompleted(task.TaskId);

                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, stage,
                    AudioTaskStatus.Running, AudioTaskStatus.Completed));
            }
            catch (OperationCanceledException)
            {
                _taskRepo.MarkCancelled(task.TaskId);
                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, stage,
                    AudioTaskStatus.Running, AudioTaskStatus.Cancelled));
            }
            catch (Exception ex)
            {
                var errorMsg = ex.Message;
                _taskRepo.MarkFailed(task.TaskId, errorMsg);
                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, stage,
                    AudioTaskStatus.Running, AudioTaskStatus.Failed,
                    errorMsg));
            }
        }

        /// <summary>
        /// 检查任务的所有前置依赖是否已满足。
        /// 依赖满足条件：前置阶段在 audio_lifecycle 中存在且 is_stale = 0。
        /// 如果有 depends_on 指定的 task_id，还要检查这些任务是否已 Completed。
        /// </summary>
        private bool AreDependenciesSatisfied(AudioTaskRecord task)
        {
            if (!Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage))
                return false;

            // 检查 DAG 定义的前置阶段
            if (AudioTaskDependencies.Prerequisites.TryGetValue(stage, out var prereqs))
            {
                foreach (var prereq in prereqs)
                {
                    var record = _lifecycleRepo.Get(task.AudioItemId, prereq.ToString());
                    if (record == null || record.IsStale)
                    {
                        // 前置阶段在 lifecycle 中不存在或已过期
                        // 检查是否有对应的 depends_on task 已完成
                        if (!string.IsNullOrEmpty(task.DependsOn))
                        {
                            var depTaskIds = JsonSerializer.Deserialize<List<string>>(task.DependsOn);
                            if (depTaskIds != null)
                            {
                                foreach (var depId in depTaskIds)
                                {
                                    var depTask = _taskRepo.GetById(depId);
                                    if (depTask == null || depTask.Status != AudioTaskStatus.Completed)
                                        return false;
                                }
                                // depends_on 中的任务都已完成，但 lifecycle 数据未写入
                                // 可能是写入延迟，此处放行
                                continue;
                            }
                        }
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>取消指定任务的 CancellationToken（供外部调用）。</summary>
        public void CancelTask(string taskId)
        {
            lock (_ctsLock)
            {
                if (_runningCts.TryGetValue(taskId, out var cts))
                {
                    cts.Cancel();
                }
            }
        }
    }
}
