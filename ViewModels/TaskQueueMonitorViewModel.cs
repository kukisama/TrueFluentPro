using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 任务监控页面 ViewModel — 展示全局任务队列，支持筛选、取消、重试等操作。
    /// </summary>
    public sealed class TaskQueueMonitorViewModel : ViewModelBase, IDisposable
    {
        private readonly IAudioTaskQueueService _queueService;
        private readonly ITaskEventBus _eventBus;
        private readonly IAudioLibraryRepository _audioRepo;

        private ObservableCollection<TaskQueueItemViewModel> _tasks = new();
        private TaskQueueItemViewModel? _selectedTask;
        private AudioTaskQueueStats _stats = new();
        private bool _showPending = true;
        private bool _showRunning = true;
        private bool _showCompleted = true;
        private bool _showFailed = true;
        private bool _showCancelled;

        public TaskQueueMonitorViewModel(
            IAudioTaskQueueService queueService,
            ITaskEventBus eventBus,
            IAudioLibraryRepository audioRepo)
        {
            _queueService = queueService;
            _eventBus = eventBus;
            _audioRepo = audioRepo;

            CancelTaskCommand = new RelayCommand(
                _ => CancelSelectedTask(),
                _ => SelectedTask != null &&
                     (SelectedTask.Status == AudioTaskStatus.Pending ||
                      SelectedTask.Status == AudioTaskStatus.Running));

            RetryTaskCommand = new RelayCommand(
                _ => RetrySelectedTask(),
                _ => SelectedTask != null &&
                     (SelectedTask.Status == AudioTaskStatus.Failed ||
                      SelectedTask.Status == AudioTaskStatus.Cancelled));

            CleanupCompletedCommand = new RelayCommand(
                _ => CleanupCompleted(),
                _ => _stats.CompletedCount > 0 || _stats.CancelledCount > 0);

            RefreshCommand = new RelayCommand(_ => Refresh());

            _eventBus.TaskStatusChanged += OnTaskStatusChanged;

            Refresh();
        }

        // ── 属性 ──────────────────────────────────────────

        public ObservableCollection<TaskQueueItemViewModel> Tasks
        {
            get => _tasks;
            set => SetProperty(ref _tasks, value);
        }

        public TaskQueueItemViewModel? SelectedTask
        {
            get => _selectedTask;
            set
            {
                if (SetProperty(ref _selectedTask, value))
                {
                    OnPropertyChanged(nameof(HasSelectedTask));
                    (CancelTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RetryTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasSelectedTask => _selectedTask != null;

        public int PendingCount => _stats.PendingCount;
        public int RunningCount => _stats.RunningCount;
        public int CompletedCount => _stats.CompletedCount;
        public int FailedCount => _stats.FailedCount;

        public string StatsText =>
            $"排队 {_stats.PendingCount} | 执行中 {_stats.RunningCount}/{AudioTaskExecutor.DefaultMaxConcurrency} | 已完成 {_stats.CompletedCount} | 失败 {_stats.FailedCount}";

        public bool ShowPending
        {
            get => _showPending;
            set { if (SetProperty(ref _showPending, value)) Refresh(); }
        }

        public bool ShowRunning
        {
            get => _showRunning;
            set { if (SetProperty(ref _showRunning, value)) Refresh(); }
        }

        public bool ShowCompleted
        {
            get => _showCompleted;
            set { if (SetProperty(ref _showCompleted, value)) Refresh(); }
        }

        public bool ShowFailed
        {
            get => _showFailed;
            set { if (SetProperty(ref _showFailed, value)) Refresh(); }
        }

        public bool ShowCancelled
        {
            get => _showCancelled;
            set { if (SetProperty(ref _showCancelled, value)) Refresh(); }
        }

        // ── 命令 ──────────────────────────────────────────

        public ICommand CancelTaskCommand { get; }
        public ICommand RetryTaskCommand { get; }
        public ICommand CleanupCompletedCommand { get; }
        public ICommand RefreshCommand { get; }

        // ── 操作 ──────────────────────────────────────────

        public void Refresh()
        {
            // 加载统计数据
            _stats = _queueService.GetStats();
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(RunningCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(StatsText));

            // 加载任务列表
            var allTasks = _queueService.Query(new AudioTaskQueryFilter { Limit = 200 });

            // 客户端过滤
            var filtered = allTasks.Where(t =>
            {
                return t.Status switch
                {
                    AudioTaskStatus.Pending => _showPending,
                    AudioTaskStatus.Running => _showRunning,
                    AudioTaskStatus.Completed => _showCompleted,
                    AudioTaskStatus.Failed => _showFailed,
                    AudioTaskStatus.Cancelled => _showCancelled,
                    _ => true,
                };
            });

            Tasks = new ObservableCollection<TaskQueueItemViewModel>(
                filtered.Select(t => new TaskQueueItemViewModel(t, ResolveAudioFileName(t.AudioItemId))));

            (CleanupCompletedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void CancelSelectedTask()
        {
            if (SelectedTask == null) return;
            _queueService.Cancel(SelectedTask.TaskId);
        }

        private void RetrySelectedTask()
        {
            if (SelectedTask == null) return;
            _queueService.Retry(SelectedTask.TaskId);
        }

        private void CleanupCompleted()
        {
            // 清理 7 天前的已完成/已取消任务
            _queueService.CleanupCompleted(TimeSpan.FromDays(7));
            Refresh();
        }

        private void OnTaskStatusChanged(TaskStatusChangedEvent e)
        {
            // 事件触发时刷新相关行
            Refresh();
        }

        private string ResolveAudioFileName(string audioItemId)
        {
            var item = _audioRepo.GetById(audioItemId);
            return item?.FileName ?? audioItemId;
        }

        public void Dispose()
        {
            _eventBus.TaskStatusChanged -= OnTaskStatusChanged;
        }
    }

    /// <summary>任务列表项 ViewModel — 用于 UI 绑定的单行显示数据。</summary>
    public sealed class TaskQueueItemViewModel : ViewModelBase
    {
        public TaskQueueItemViewModel(AudioTaskRecord record, string audioFileName)
        {
            TaskId = record.TaskId;
            AudioItemId = record.AudioItemId;
            AudioFileName = audioFileName;
            Stage = record.Stage;
            Status = record.Status;
            Priority = record.Priority;
            ErrorMessage = record.ErrorMessage;
            RetryCount = record.RetryCount;
            SubmittedAt = record.SubmittedAt;
            StartedAt = record.StartedAt;
            CompletedAt = record.CompletedAt;
        }

        public string TaskId { get; }
        public string ShortTaskId => TaskId.Length > 12 ? TaskId[..12] + ".." : TaskId;
        public string AudioItemId { get; }
        public string AudioFileName { get; }
        public string Stage { get; }
        public AudioTaskStatus Status { get; }
        public int Priority { get; }
        public string? ErrorMessage { get; }
        public int RetryCount { get; }
        public DateTime SubmittedAt { get; }
        public DateTime? StartedAt { get; }
        public DateTime? CompletedAt { get; }

        public string StageDisplayName => Stage switch
        {
            "Transcribed" => "转录",
            "Summarized" => "总结",
            "MindMap" => "脑图",
            "Insight" => "顿悟",
            "PodcastScript" => "播客",
            "PodcastAudio" => "播客音频",
            "Research" => "研究",
            "Translated" => "翻译",
            _ => Stage,
        };

        public string StatusDisplayName => Status switch
        {
            AudioTaskStatus.Pending => "排队中",
            AudioTaskStatus.Running => "执行中",
            AudioTaskStatus.Completed => "已完成",
            AudioTaskStatus.Failed => "失败",
            AudioTaskStatus.Cancelled => "已取消",
            _ => Status.ToString(),
        };

        public string ElapsedTime
        {
            get
            {
                if (StartedAt == null) return "--";
                var end = CompletedAt ?? DateTime.Now;
                var elapsed = end - StartedAt.Value;
                return elapsed.TotalHours >= 1
                    ? $"{elapsed.Hours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                    : $"{elapsed.Minutes}:{elapsed.Seconds:D2}";
            }
        }
    }
}
