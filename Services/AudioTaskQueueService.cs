using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 音频任务队列服务接口 — 负责任务的提交、取消、重试和查询。
    /// </summary>
    public interface IAudioTaskQueueService
    {
        /// <summary>提交单个任务，返回 task_id。若已存在 Pending/Running 的同 (audio, stage) 任务则返回已有 task_id。</summary>
        string Submit(string audioItemId, AudioLifecycleStage stage, int priority = 0);

        /// <summary>为音频自动提交所有缺失阶段的任务（按 DAG 依赖）。</summary>
        List<string> SubmitAll(string audioItemId);

        /// <summary>为音频自动提交所有缺失阶段的任务，尊重阶段预设配置。</summary>
        List<string> SubmitAll(string audioItemId, List<AudioLabStagePreset>? stagePresets);

        /// <summary>取消指定任务。</summary>
        void Cancel(string taskId);

        /// <summary>取消指定音频的所有 Pending/Running 任务。</summary>
        void CancelAllForAudio(string audioItemId);

        /// <summary>重试失败的任务（重置为 Pending）。</summary>
        string Retry(string taskId);

        /// <summary>查询任务列表（支持筛选）。</summary>
        List<AudioTaskRecord> Query(AudioTaskQueryFilter? filter = null);

        /// <summary>获取队列统计数据。</summary>
        AudioTaskQueueStats GetStats();

        /// <summary>清理已完成/已取消的旧任务记录。</summary>
        int CleanupCompleted(TimeSpan olderThan);

        /// <summary>配置变更后同步队列：取消已禁用阶段的 Pending 任务，补提交新启用阶段的缺失任务。</summary>
        void SyncWithPresets(string audioItemId, List<AudioLabStagePreset>? stagePresets);

        /// <summary>有新任务入队时触发，供 Executor 订阅以唤醒调度循环。</summary>
        event Action? NewTaskEnqueued;
    }

    /// <summary>
    /// 音频任务队列服务实现 — 管理任务提交/去重/级联依赖/取消/重试。
    /// </summary>
    public sealed class AudioTaskQueueService : IAudioTaskQueueService
    {
        private readonly IAudioTaskRepository _taskRepo;
        private readonly IAudioLifecycleRepository _lifecycleRepo;
        private readonly ITaskEventBus _eventBus;

        public event Action? NewTaskEnqueued;

        public AudioTaskQueueService(
            IAudioTaskRepository taskRepo,
            IAudioLifecycleRepository lifecycleRepo,
            ITaskEventBus eventBus)
        {
            _taskRepo = taskRepo;
            _lifecycleRepo = lifecycleRepo;
            _eventBus = eventBus;
        }

        public string Submit(string audioItemId, AudioLifecycleStage stage, int priority = 0)
        {
            var stageStr = stage.ToString();

            // 去重检查：同一 (audio, stage) 是否已有 Pending/Running 任务
            var existing = _taskRepo.FindActiveTask(audioItemId, stageStr);
            if (existing != null)
                return existing.TaskId;

            var taskId = UlidGenerator.NewUlid();
            var record = new AudioTaskRecord
            {
                TaskId = taskId,
                AudioItemId = audioItemId,
                Stage = stageStr,
                Status = AudioTaskStatus.Pending,
                Priority = priority,
                SubmittedAt = DateTime.Now,
            };

            _taskRepo.Insert(record);

            _eventBus.Publish(new TaskStatusChangedEvent(
                taskId, audioItemId, stage,
                AudioTaskStatus.Pending, AudioTaskStatus.Pending));

            // 通知执行器有新任务
            NewTaskEnqueued?.Invoke();

            return taskId;
        }

        public List<string> SubmitAll(string audioItemId) => SubmitAll(audioItemId, null);

        public List<string> SubmitAll(string audioItemId, List<AudioLabStagePreset>? stagePresets)
        {
            var taskIds = new List<string>();
            var completedStages = _lifecycleRepo.GetAllStages(audioItemId);
            var completedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in completedStages)
            {
                if (!r.IsStale)
                    completedSet.Add(r.Stage);
            }

            // 按 DAG 顺序提交缺失阶段的任务，记录新提交的 taskId 以设置依赖
            var taskIdMap = new Dictionary<AudioLifecycleStage, string>();

            foreach (var kvp in AudioTaskDependencies.Prerequisites)
            {
                var stage = kvp.Key;
                var stageStr = stage.ToString();

                // 跳过已完成且未过期的阶段
                if (completedSet.Contains(stageStr))
                    continue;

                // 根据阶段预设配置决定是否跳过（Transcribed 始终提交）
                if (stage != AudioLifecycleStage.Transcribed
                    && !AudioLabStagePresetDefaults.ShouldIncludeInBatch(stagePresets, stageStr))
                    continue;

                // 先检查去重
                var existingActive = _taskRepo.FindActiveTask(audioItemId, stageStr);
                if (existingActive != null)
                {
                    taskIdMap[stage] = existingActive.TaskId;
                    taskIds.Add(existingActive.TaskId);
                    continue;
                }

                // 构建依赖列表
                var deps = new List<string>();
                foreach (var prereq in kvp.Value)
                {
                    if (taskIdMap.TryGetValue(prereq, out var depTaskId))
                        deps.Add(depTaskId);
                }

                var taskId = UlidGenerator.NewUlid();
                var record = new AudioTaskRecord
                {
                    TaskId = taskId,
                    AudioItemId = audioItemId,
                    Stage = stageStr,
                    Status = AudioTaskStatus.Pending,
                    Priority = 0,
                    DependsOn = deps.Count > 0 ? JsonSerializer.Serialize(deps) : null,
                    SubmittedAt = DateTime.Now,
                };

                _taskRepo.Insert(record);
                taskIdMap[stage] = taskId;
                taskIds.Add(taskId);

                _eventBus.Publish(new TaskStatusChangedEvent(
                    taskId, audioItemId, stage,
                    AudioTaskStatus.Pending, AudioTaskStatus.Pending));
            }

            // 提交自定义阶段（非内置 enum 定义的阶段）
            if (stagePresets != null)
            {
                var knownStages = new HashSet<string>(
                    AudioTaskDependencies.Prerequisites.Keys.Select(k => k.ToString()),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var preset in stagePresets.Where(p =>
                    p.IsEnabled && p.IncludeInBatch &&
                    !string.IsNullOrWhiteSpace(p.Stage) &&
                    !knownStages.Contains(p.Stage)))
                {
                    var customStage = preset.Stage;

                    if (completedSet.Contains(customStage))
                        continue;

                    var existingActive = _taskRepo.FindActiveTask(audioItemId, customStage);
                    if (existingActive != null)
                    {
                        taskIds.Add(existingActive.TaskId);
                        continue;
                    }

                    // 自定义阶段依赖 Transcribed
                    var deps = new List<string>();
                    if (taskIdMap.TryGetValue(AudioLifecycleStage.Transcribed, out var depId))
                        deps.Add(depId);

                    var customTaskId = UlidGenerator.NewUlid();
                    _taskRepo.Insert(new AudioTaskRecord
                    {
                        TaskId = customTaskId,
                        AudioItemId = audioItemId,
                        Stage = customStage,
                        Status = AudioTaskStatus.Pending,
                        Priority = 0,
                        DependsOn = deps.Count > 0 ? JsonSerializer.Serialize(deps) : null,
                        SubmittedAt = DateTime.Now,
                    });
                    taskIds.Add(customTaskId);
                }
            }

            // 通知执行器
            if (taskIds.Count > 0)
                NewTaskEnqueued?.Invoke();

            return taskIds;
        }

        public void Cancel(string taskId)
        {
            var task = _taskRepo.GetById(taskId);
            if (task == null) return;

            if (task.Status != AudioTaskStatus.Pending && task.Status != AudioTaskStatus.Running)
                return;

            var oldStatus = task.Status;
            _taskRepo.MarkCancelled(taskId);

            if (Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage))
            {
                _eventBus.Publish(new TaskStatusChangedEvent(
                    taskId, task.AudioItemId, stage,
                    oldStatus, AudioTaskStatus.Cancelled));
            }
        }

        public void CancelAllForAudio(string audioItemId)
        {
            var activeTasks = _taskRepo.GetActiveTasksForAudio(audioItemId);
            foreach (var task in activeTasks)
            {
                Cancel(task.TaskId);
            }
        }

        public void SyncWithPresets(string audioItemId, List<AudioLabStagePreset>? stagePresets)
        {
            // 1. 取消已禁用阶段的 Pending 任务（不取消 Running，以免中断正在执行的工作）
            var activeTasks = _taskRepo.GetActiveTasksForAudio(audioItemId);
            foreach (var task in activeTasks)
            {
                if (task.Status != AudioTaskStatus.Pending) continue;
                // Transcribed 始终保留
                if (task.Stage == nameof(AudioLifecycleStage.Transcribed)) continue;
                if (!AudioLabStagePresetDefaults.ShouldIncludeInBatch(stagePresets, task.Stage))
                    Cancel(task.TaskId);
            }

            // 2. 补提交新启用阶段的缺失任务
            SubmitAll(audioItemId, stagePresets);
        }

        public string Retry(string taskId)
        {
            var task = _taskRepo.GetById(taskId);
            if (task == null)
                throw new InvalidOperationException($"任务 {taskId} 不存在。");

            if (task.Status != AudioTaskStatus.Failed && task.Status != AudioTaskStatus.Cancelled)
                throw new InvalidOperationException($"只有 Failed/Cancelled 状态的任务可以重试，当前状态：{task.Status}。");

            _taskRepo.Retry(taskId);

            if (Enum.TryParse<AudioLifecycleStage>(task.Stage, out var stage))
            {
                _eventBus.Publish(new TaskStatusChangedEvent(
                    taskId, task.AudioItemId, stage,
                    task.Status, AudioTaskStatus.Pending));
            }

            NewTaskEnqueued?.Invoke();
            return taskId;
        }

        public List<AudioTaskRecord> Query(AudioTaskQueryFilter? filter = null)
        {
            return _taskRepo.Query(filter);
        }

        public AudioTaskQueueStats GetStats()
        {
            return _taskRepo.GetStats();
        }

        public int CleanupCompleted(TimeSpan olderThan)
        {
            return _taskRepo.CleanupOldTasks(olderThan);
        }
    }
}
