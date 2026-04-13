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

        /// <summary>转录类最大并发数。</summary>
        int MaxTranscriptionConcurrency { get; }

        /// <summary>AI 类最大并发数。</summary>
        int MaxAiConcurrency { get; }

        /// <summary>转录任务超时（分钟）。</summary>
        int TranscriptionTimeoutMinutes { get; }

        /// <summary>动态设置并发上限。</summary>
        void SetConcurrencyLimits(int maxTranscription, int maxAi);

        /// <summary>动态设置转录任务超时（分钟），范围 1~60。</summary>
        void SetTranscriptionTimeout(int minutes);
    }

    /// <summary>
    /// 音频任务执行器实现 — 分类信号量控制并发，DAG 依赖检查，独立 CancellationToken per task。
    /// </summary>
    public sealed class AudioTaskExecutor : IAudioTaskExecutor
    {
        /// <summary>转录类默认最大并发数。</summary>
        public static readonly int DefaultMaxTranscriptionConcurrency = 10;

        /// <summary>AI 类默认最大并发数。</summary>
        public static readonly int DefaultMaxAiConcurrency = 5;

        /// <summary>任务超时阈值（分钟），启动时用于恢复卡死的 Running 任务。</summary>
        public static readonly int DefaultStallThresholdMinutes = 30;

        /// <summary>调度循环轮询间隔（秒），兜底防止信号丢失。</summary>
        private const int PollIntervalSeconds = 5;

        /// <summary>转录类任务默认超时（分钟）。</summary>
        public static readonly int DefaultTranscriptionTimeoutMinutes = 15;
        /// <summary>语音类任务（转录/TTS）超时，可通过 SetTranscriptionTimeout 动态调整。</summary>
        private volatile int _transcriptionTimeoutMinutes;
        /// <summary>AI 类任务总超时（含首字节 2 分钟 + 流式输出）。</summary>
        private static readonly TimeSpan AiTaskTimeout = TimeSpan.FromMinutes(5);
        /// <summary>超时后自动重试次数上限，超过则标记失败。</summary>
        private const int MaxAutoRetries = 3;

        private volatile int _maxTranscriptionConcurrency;
        private volatile int _maxAiConcurrency;
        private int _runningTranscriptionCount;
        private int _runningAiCount;
        private readonly int _stallThresholdMinutes;

        private readonly IAudioTaskRepository _taskRepo;
        private readonly IAudioLifecycleRepository _lifecycleRepo;
        private readonly ITaskExecutionRepository _executionRepo;
        private readonly ITaskEventBus _eventBus;
        private readonly AudioTaskStageHandlerService _stageHandler;
        private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
        private readonly Dictionary<string, CancellationTokenSource> _runningCts = new();
        private readonly HashSet<string> _userCancelledTasks = new();
        private readonly object _ctsLock = new();

        /// <summary>调度循环单次批量查询上限。</summary>
        private const int BatchLookaheadBuffer = 20;

        /// <summary>语音服务类阶段（Transcribed / PodcastAudio），占用转录并发槽。</summary>
        private static bool IsTranscriptionStage(string stage) =>
            stage is "Transcribed" or "PodcastAudio";

        public int MaxTranscriptionConcurrency => _maxTranscriptionConcurrency;
        public int MaxAiConcurrency => _maxAiConcurrency;
        public int TranscriptionTimeoutMinutes => _transcriptionTimeoutMinutes;

        public void SetConcurrencyLimits(int maxTranscription, int maxAi)
        {
            if (maxTranscription > 0) _maxTranscriptionConcurrency = maxTranscription;
            if (maxAi > 0) _maxAiConcurrency = maxAi;
            // 唤醒调度循环，让新限制立即生效
            NotifyNewTask();
        }

        public void SetTranscriptionTimeout(int minutes)
        {
            _transcriptionTimeoutMinutes = Math.Clamp(minutes, 1, 60);
        }

        public AudioTaskExecutor(
            IAudioTaskRepository taskRepo,
            IAudioLifecycleRepository lifecycleRepo,
            ITaskExecutionRepository executionRepo,
            ITaskEventBus eventBus,
            AudioTaskStageHandlerService stageHandler,
            int maxTranscriptionConcurrency = 0,
            int maxAiConcurrency = 0,
            int stallThresholdMinutes = 0)
        {
            _taskRepo = taskRepo;
            _lifecycleRepo = lifecycleRepo;
            _executionRepo = executionRepo;
            _eventBus = eventBus;
            _stageHandler = stageHandler;
            _maxTranscriptionConcurrency = maxTranscriptionConcurrency > 0
                ? maxTranscriptionConcurrency : DefaultMaxTranscriptionConcurrency;
            _maxAiConcurrency = maxAiConcurrency > 0
                ? maxAiConcurrency : DefaultMaxAiConcurrency;
            _stallThresholdMinutes = stallThresholdMinutes > 0 ? stallThresholdMinutes : DefaultStallThresholdMinutes;
            _transcriptionTimeoutMinutes = DefaultTranscriptionTimeoutMinutes;
        }

        public void NotifyNewTask()
        {
            try { _wakeSignal.Release(); }
            catch (SemaphoreFullException) { /* 已有待处理信号 */ }
        }

        public async Task StartAsync(CancellationToken appShutdown)
        {
            // 启动时恢复所有上次遭中断的 Running 任务 → 直接回到 Pending 重新执行
            var recovered = _taskRepo.RecoverStalledRunningTasks(TimeSpan.Zero);
            if (recovered > 0)
            {
                Debug.WriteLine($"[AudioTaskExecutor] 恢复 {recovered} 个上次中断的任务");
            }

            // 恢复中断的执行记录（上次未正常关闭时遗留的 Running 执行）
            var recoveredExec = _executionRepo.RecoverInterruptedExecutions();
            if (recoveredExec > 0)
            {
                Debug.WriteLine($"[AudioTaskExecutor] 恢复 {recoveredExec} 条中断的执行记录");
            }

            // 主调度循环
            while (!appShutdown.IsCancellationRequested)
            {
                try
                {
                    // 等待新任务信号或超时（定期轮询兜底）
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(PollIntervalSeconds), appShutdown);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 尝试调度所有可执行的任务（异常不终止循环）
                try
                {
                    await TryScheduleTasksAsync(appShutdown);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioTaskExecutor] 调度异常（将在下轮重试）: {ex.Message}");
                }
            }
        }

        private async Task TryScheduleTasksAsync(CancellationToken appShutdown)
        {
            // 批量获取所有 Pending 任务，逐个检查依赖，跳过不满足的
            var totalMax = _maxTranscriptionConcurrency + _maxAiConcurrency;
            var pendingTasks = _taskRepo.GetPendingTasks(totalMax + BatchLookaheadBuffer);

            foreach (var task in pendingTasks)
            {
                if (appShutdown.IsCancellationRequested)
                    return;

                bool isTranscription = IsTranscriptionStage(task.Stage);

                // 检查对应类别的并发槽位
                if (isTranscription)
                {
                    if (Interlocked.CompareExchange(ref _runningTranscriptionCount, 0, 0) >= _maxTranscriptionConcurrency)
                        continue; // 转录类已满，跳过，后面可能有 AI 类任务
                }
                else
                {
                    if (Interlocked.CompareExchange(ref _runningAiCount, 0, 0) >= _maxAiConcurrency)
                        continue; // AI 类已满，跳过，后面可能有转录类任务
                }

                // DAG 依赖检查 — 不满足则跳过，继续检查后续任务
                if (!AreDependenciesSatisfied(task))
                    continue;

                // 占用并发槽（原子递增）
                if (isTranscription)
                    Interlocked.Increment(ref _runningTranscriptionCount);
                else
                    Interlocked.Increment(ref _runningAiCount);

                // 标记为 Running
                var oldStatus = task.Status;
                _taskRepo.MarkRunning(task.TaskId);

                if (Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage))
                {
                    _eventBus.Publish(new TaskStatusChangedEvent(
                        task.TaskId, task.AudioItemId, stage,
                        oldStatus, AudioTaskStatus.Running));
                }

                // 创建独立的 CancellationToken（含任务级超时）
                var taskTimeout = isTranscription
                    ? TimeSpan.FromMinutes(_transcriptionTimeoutMinutes)
                    : AiTaskTimeout;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
                cts.CancelAfter(taskTimeout);
                lock (_ctsLock) { _runningCts[task.TaskId] = cts; }

                // 在线程池执行任务（不阻塞调度循环）
                var capturedTask = task;
                var capturedIsTranscription = isTranscription;
                var capturedCts = cts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteTaskAsync(capturedTask, capturedCts.Token, appShutdown);
                    }
                    finally
                    {
                        lock (_ctsLock) { _runningCts.Remove(capturedTask.TaskId); }
                        cts.Dispose();

                        // 释放对应类别的并发槽
                        if (capturedIsTranscription)
                            Interlocked.Decrement(ref _runningTranscriptionCount);
                        else
                            Interlocked.Decrement(ref _runningAiCount);

                        // 任务完成后，唤醒调度循环以检查依赖此任务的后续任务
                        NotifyNewTask();
                    }
                }, appShutdown);
            }
        }

        private async Task ExecuteTaskAsync(AudioTaskRecord task, CancellationToken ct, CancellationToken appShutdown)
        {
            var isBuiltIn = Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage);
            if (!isBuiltIn)
            {
                // 自定义阶段：使用自定义执行路径
                await ExecuteCustomTaskAsync(task, ct);
                return;
            }

            var executionId = Helpers.UlidGenerator.NewUlid();
            var sw = Stopwatch.StartNew();

            try
            {
                // 写入执行记录（放在 try 内，避免 Insert 异常导致任务卡在 Running）
                _executionRepo.Insert(new TaskExecutionRecord
                {
                    ExecutionId = executionId,
                    TaskId = task.TaskId,
                    AudioItemId = task.AudioItemId,
                    Stage = task.Stage,
                    Status = "Running",
                    StartedAt = DateTime.Now
                });

                // 构建进度回调：更新 DB + 发布事件
                Action<string> reportProgress = message =>
                {
                    _taskRepo.UpdateProgressMessage(task.TaskId, message);
                    _eventBus.PublishProgress(new TaskProgressEvent(
                        task.TaskId, task.AudioItemId, stage, message));
                };

                reportProgress("准备执行...");

                // 调用阶段处理器执行实际的生成逻辑
                var outcome = await _stageHandler.ExecuteStageAsync(task.AudioItemId, stage, ct, reportProgress);

                sw.Stop();

                // 标记执行记录完成
                _executionRepo.MarkCompleted(executionId,
                    tokensIn: outcome?.PromptTokens,
                    tokensOut: outcome?.CompletionTokens,
                    durationMs: (int)sw.Elapsed.TotalMilliseconds,
                    modelName: outcome?.ModelName);

                // 调试模式：保存提示词和响应
                if (outcome?.DebugPrompt != null || outcome?.DebugResponse != null)
                    _executionRepo.SaveDebugData(executionId, outcome.DebugPrompt, outcome.DebugResponse);

                // 标记任务完成
                _taskRepo.MarkCompleted(task.TaskId);

                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, stage,
                    AudioTaskStatus.Running, AudioTaskStatus.Completed));
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                var durationMs = (int)sw.Elapsed.TotalMilliseconds;

                // 判断取消来源：用户手动取消 / app 关闭 / 任务超时
                bool isUserCancel;
                lock (_ctsLock) { isUserCancel = _userCancelledTasks.Remove(task.TaskId); }

                if (isUserCancel || appShutdown.IsCancellationRequested)
                {
                    // 用户取消 或 app 关闭
                    var reason = appShutdown.IsCancellationRequested ? "shutdown" : "user";
                    _executionRepo.MarkCancelled(executionId, reason, null, null, durationMs);
                    _taskRepo.MarkCancelled(task.TaskId);
                    _eventBus.Publish(new TaskStatusChangedEvent(
                        task.TaskId, task.AudioItemId, stage,
                        AudioTaskStatus.Running, AudioTaskStatus.Cancelled));
                }
                else if (task.RetryCount < MaxAutoRetries)
                {
                    // 任务超时且未达重试上限 → 自动重试
                    var timeoutMsg = $"任务执行超时（已运行 {sw.Elapsed.TotalSeconds:F0} 秒），将自动重试（第 {task.RetryCount + 1}/{MaxAutoRetries} 次）...";
                    _executionRepo.MarkFailed(executionId, null, null, durationMs, timeoutMsg);
                    _taskRepo.Retry(task.TaskId);
                    _eventBus.Publish(new TaskStatusChangedEvent(
                        task.TaskId, task.AudioItemId, stage,
                        AudioTaskStatus.Running, AudioTaskStatus.Pending,
                        timeoutMsg));
                    Debug.WriteLine($"[AudioTaskExecutor] 任务 {task.TaskId} 超时，自动重试 ({task.RetryCount + 1}/{MaxAutoRetries})");
                }
                else
                {
                    // 超时且已达重试上限 → 标记失败
                    var timeoutMsg = $"任务执行超时（已运行 {sw.Elapsed.TotalSeconds:F0} 秒，已重试 {task.RetryCount} 次）。请检查网络连接。";
                    _executionRepo.MarkFailed(executionId, null, null, durationMs, timeoutMsg);
                    _taskRepo.MarkFailed(task.TaskId, timeoutMsg);
                    _eventBus.Publish(new TaskStatusChangedEvent(
                        task.TaskId, task.AudioItemId, stage,
                        AudioTaskStatus.Running, AudioTaskStatus.Failed,
                        timeoutMsg));
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                var errorMsg = ex.Message;
                _executionRepo.MarkFailed(executionId, null, null, (int)sw.Elapsed.TotalMilliseconds, errorMsg);

                _taskRepo.MarkFailed(task.TaskId, errorMsg);
                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, stage,
                    AudioTaskStatus.Running, AudioTaskStatus.Failed,
                    errorMsg));
            }
        }

        /// <summary>执行自定义阶段任务（非内置枚举值）。</summary>
        private async Task ExecuteCustomTaskAsync(AudioTaskRecord task, CancellationToken ct)
        {
            var executionId = Helpers.UlidGenerator.NewUlid();
            var sw = Stopwatch.StartNew();

            try
            {
                _executionRepo.Insert(new TaskExecutionRecord
                {
                    ExecutionId = executionId,
                    TaskId = task.TaskId,
                    AudioItemId = task.AudioItemId,
                    Stage = task.Stage,
                    Status = "Running",
                    StartedAt = DateTime.Now
                });

                Action<string> reportProgress = message =>
                {
                    _taskRepo.UpdateProgressMessage(task.TaskId, message);
                };

                reportProgress("准备执行自定义阶段...");

                var outcome = await _stageHandler.ExecuteCustomStageAsync(
                    task.AudioItemId, task.Stage, ct, reportProgress);

                sw.Stop();

                _executionRepo.MarkCompleted(executionId,
                    tokensIn: outcome?.PromptTokens,
                    tokensOut: outcome?.CompletionTokens,
                    durationMs: (int)sw.Elapsed.TotalMilliseconds,
                    modelName: outcome?.ModelName);

                if (outcome?.DebugPrompt != null || outcome?.DebugResponse != null)
                    _executionRepo.SaveDebugData(executionId, outcome.DebugPrompt, outcome.DebugResponse);

                _taskRepo.MarkCompleted(task.TaskId);

                // 自定义阶段无对应 enum，使用 Summarized 作为占位通知 UI 刷新
                // （ViewModel 的 OnTaskStatusChanged 会检查 audioItemId 并刷新自定义内容）
                _eventBus.Publish(new TaskStatusChangedEvent(
                    task.TaskId, task.AudioItemId, AudioLifecycleStage.Summarized,
                    AudioTaskStatus.Running, AudioTaskStatus.Completed));
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _executionRepo.MarkCancelled(executionId, "user", null, null, (int)sw.Elapsed.TotalMilliseconds);
                _taskRepo.MarkCancelled(task.TaskId);
            }
            catch (Exception ex)
            {
                sw.Stop();
                var errorMsg = ex.Message;
                _executionRepo.MarkFailed(executionId, null, null, (int)sw.Elapsed.TotalMilliseconds, errorMsg);
                _taskRepo.MarkFailed(task.TaskId, errorMsg);
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
            {
                // 自定义阶段：依赖 Transcribed 完成
                var transcribed = _lifecycleRepo.Get(task.AudioItemId, nameof(AudioLifecycleStage.Transcribed));
                return transcribed != null && !transcribed.IsStale;
            }

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
                    _userCancelledTasks.Add(taskId);
                    cts.Cancel();
                }
            }
        }
    }
}
