using System;
using System.Collections.Generic;
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

        public List<string> SubmitAll(string audioItemId)
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

                // 跳过 PodcastAudio（TTS 生成，本次不队列化）
                if (stage == AudioLifecycleStage.PodcastAudio)
                    continue;

                // 跳过 Translated（翻译阶段暂不支持队列化执行）
                if (stage == AudioLifecycleStage.Translated)
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
