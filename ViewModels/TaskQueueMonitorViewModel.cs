using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 任务监控页面 ViewModel — 采用批量字幕风格的分类导航 + 排序列表。
    /// </summary>
    public sealed class TaskQueueMonitorViewModel : ViewModelBase, IDisposable
    {
        private readonly IAudioTaskQueueService _queueService;
        private readonly ITaskEventBus _eventBus;
        private readonly IAudioLibraryRepository _audioRepo;
        private readonly IAudioTaskExecutor _executor;
        private readonly ITaskExecutionRepository _executionRepo;

        private ObservableCollection<TaskQueueItemViewModel> _currentBucketTasks = new();
        private BatchBucketNavItem? _selectedBucket;
        private TaskQueueItemViewModel? _selectedTask;

        private int _maxTranscriptionConcurrency;
        private int _maxAiConcurrency;
        private int _transcriptionTimeoutMinutes;
        private readonly DispatcherTimer _autoRefreshTimer;

        // 排序
        private string _sortColumn = "SubmittedAt";
        private bool _sortAscending;

        // P3: 执行历史
        private ObservableCollection<ExecutionRecordViewModel> _executions = new();

        // P4: 运营统计
        private TaskExecutionGlobalStats _globalStats = new();

        public TaskQueueMonitorViewModel(
            IAudioTaskQueueService queueService,
            ITaskEventBus eventBus,
            IAudioLibraryRepository audioRepo,
            IAudioTaskExecutor executor,
            ITaskExecutionRepository executionRepo)
        {
            _queueService = queueService;
            _eventBus = eventBus;
            _audioRepo = audioRepo;
            _executor = executor;
            _executionRepo = executionRepo;

            _maxTranscriptionConcurrency = _executor.MaxTranscriptionConcurrency;
            _maxAiConcurrency = _executor.MaxAiConcurrency;
            _transcriptionTimeoutMinutes = _executor.TranscriptionTimeoutMinutes;

            // 初始化分类 buckets
            Buckets = new ObservableCollection<BatchBucketNavItem>
            {
                new() { Key = "pending",   Title = "排队中",    IconValue = "fa-regular fa-clock" },
                new() { Key = "running",   Title = "执行中",    IconValue = "fa-solid fa-arrows-rotate" },
                new() { Key = "completed", Title = "已完成",    IconValue = "fa-solid fa-check" },
                new() { Key = "failed",    Title = "失败",      IconValue = "fa-solid fa-triangle-exclamation" },
                new() { Key = "cancelled", Title = "已取消",    IconValue = "fa-regular fa-circle-xmark" },
            };
            _selectedBucket = Buckets[0];

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

            CleanupCompletedCommand = new RelayCommand(_ => CleanupCompleted());
            RefreshCommand = new RelayCommand(_ => Refresh());

            SortByColumnCommand = new RelayCommand(col =>
            {
                var colName = col as string ?? "SubmittedAt";
                if (_sortColumn == colName)
                    _sortAscending = !_sortAscending;
                else
                {
                    _sortColumn = colName;
                    _sortAscending = true;
                }
                OnPropertyChanged(nameof(SortColumn));
                OnPropertyChanged(nameof(SortAscending));
                RebuildCurrentBucketTasks();
            });

            _eventBus.TaskStatusChanged += OnTaskStatusChanged;
            _eventBus.TaskProgressUpdated += OnTaskProgressUpdated;

            Refresh();

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoRefreshTimer.Tick += (_, _) => RefreshElapsedTimes();
            _autoRefreshTimer.Start();
        }

        // ── Bucket 导航 ──────────────────────────────────────

        public ObservableCollection<BatchBucketNavItem> Buckets { get; }

        public BatchBucketNavItem? SelectedBucket
        {
            get => _selectedBucket;
            set
            {
                if (SetProperty(ref _selectedBucket, value))
                {
                    SelectedTask = null;
                    RebuildCurrentBucketTasks();
                }
            }
        }

        public ObservableCollection<TaskQueueItemViewModel> CurrentBucketTasks
        {
            get => _currentBucketTasks;
            set => SetProperty(ref _currentBucketTasks, value);
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
                    RefreshExecutionHistory();
                }
            }
        }

        public bool HasSelectedTask => _selectedTask != null;

        // ── 排序 ─────────────────────────────────────────────

        public string SortColumn => _sortColumn;
        public bool SortAscending => _sortAscending;

        // ── 并发 ─────────────────────────────────────────────

        public int MaxTranscriptionConcurrency
        {
            get => _maxTranscriptionConcurrency;
            set
            {
                var clamped = Math.Clamp(value, 1, 20);
                if (SetProperty(ref _maxTranscriptionConcurrency, clamped))
                    _executor.SetConcurrencyLimits(clamped, _maxAiConcurrency);
            }
        }

        public int MaxAiConcurrency
        {
            get => _maxAiConcurrency;
            set
            {
                var clamped = Math.Clamp(value, 1, 20);
                if (SetProperty(ref _maxAiConcurrency, clamped))
                    _executor.SetConcurrencyLimits(_maxTranscriptionConcurrency, clamped);
            }
        }

        // ── 超时 ─────────────────────────────────────────────

        public int TranscriptionTimeoutMinutes
        {
            get => _transcriptionTimeoutMinutes;
            set
            {
                var clamped = Math.Clamp(value, 1, 60);
                if (SetProperty(ref _transcriptionTimeoutMinutes, clamped))
                    _executor.SetTranscriptionTimeout(clamped);
            }
        }

        // ── 执行历史 ─────────────────────────────────────────

        public ObservableCollection<ExecutionRecordViewModel> Executions
        {
            get => _executions;
            set => SetProperty(ref _executions, value);
        }

        public bool HasExecutions => _executions.Count > 0;

        // ── 运营统计 ─────────────────────────────────────────

        public int TotalExecutions => _globalStats.TotalExecutions;
        public int BillableExecutions => _globalStats.BillableExecutions;
        public long BillableTokensIn => _globalStats.BillableTokensIn;
        public long BillableTokensOut => _globalStats.BillableTokensOut;
        public string TokensDisplayText => (BillableTokensIn + BillableTokensOut) > 0
            ? $"入 {BillableTokensIn:N0} / 出 {BillableTokensOut:N0}"
            : "暂无数据";

        // ── 命令 ──────────────────────────────────────────────

        public ICommand CancelTaskCommand { get; }
        public ICommand RetryTaskCommand { get; }
        public ICommand CleanupCompletedCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SortByColumnCommand { get; }

        // ── 核心操作 ──────────────────────────────────────────

        // 缓存全量任务，切 bucket 时无需重查
        private List<AudioTaskRecord> _allTasks = new();

        public void Refresh()
        {
            _allTasks = _queueService.Query(new AudioTaskQueryFilter { Limit = 500 });
            RefreshBucketCounts();
            RebuildCurrentBucketTasks();
            RefreshGlobalStats();
        }

        private void RefreshBucketCounts()
        {
            foreach (var b in Buckets)
            {
                b.Count = b.Key switch
                {
                    "pending" => _allTasks.Count(t => t.Status == AudioTaskStatus.Pending),
                    "running" => _allTasks.Count(t => t.Status == AudioTaskStatus.Running),
                    "completed" => _allTasks.Count(t => t.Status == AudioTaskStatus.Completed),
                    "failed" => _allTasks.Count(t => t.Status == AudioTaskStatus.Failed),
                    "cancelled" => _allTasks.Count(t => t.Status == AudioTaskStatus.Cancelled),
                    _ => 0,
                };
            }
        }

        private void RebuildCurrentBucketTasks()
        {
            var statusFilter = _selectedBucket?.Key switch
            {
                "pending" => AudioTaskStatus.Pending,
                "running" => AudioTaskStatus.Running,
                "completed" => AudioTaskStatus.Completed,
                "failed" => AudioTaskStatus.Failed,
                "cancelled" => AudioTaskStatus.Cancelled,
                _ => (AudioTaskStatus?)null,
            };

            IEnumerable<AudioTaskRecord> filtered = statusFilter.HasValue
                ? _allTasks.Where(t => t.Status == statusFilter.Value)
                : _allTasks;

            // 排序
            filtered = (_sortColumn, _sortAscending) switch
            {
                ("TaskId", true)      => filtered.OrderBy(t => t.TaskId),
                ("TaskId", false)     => filtered.OrderByDescending(t => t.TaskId),
                ("AudioFileName", true)  => filtered.OrderBy(t => ResolveAudioFileName(t.AudioItemId)),
                ("AudioFileName", false) => filtered.OrderByDescending(t => ResolveAudioFileName(t.AudioItemId)),
                ("Stage", true)       => filtered.OrderBy(t => t.Stage),
                ("Stage", false)      => filtered.OrderByDescending(t => t.Stage),
                ("SubmittedAt", true) => filtered.OrderBy(t => t.SubmittedAt),
                ("SubmittedAt", false) => filtered.OrderByDescending(t => t.SubmittedAt),
                _ => filtered.OrderByDescending(t => t.SubmittedAt),
            };

            CurrentBucketTasks = new ObservableCollection<TaskQueueItemViewModel>(
                filtered.Select(t => new TaskQueueItemViewModel(t, ResolveAudioFileName(t.AudioItemId))));
        }

        private void RefreshElapsedTimes()
        {
            foreach (var item in _currentBucketTasks)
                item.NotifyElapsedTimeChanged();

            // 顺便刷新 bucket 计数
            var prev = _allTasks;
            _allTasks = _queueService.Query(new AudioTaskQueryFilter { Limit = 500 });
            RefreshBucketCounts();

            // 如果当前 bucket 与缓存不一致，重建列表
            var statusFilter = _selectedBucket?.Key switch
            {
                "pending" => AudioTaskStatus.Pending,
                "running" => AudioTaskStatus.Running,
                "completed" => AudioTaskStatus.Completed,
                "failed" => AudioTaskStatus.Failed,
                "cancelled" => AudioTaskStatus.Cancelled,
                _ => (AudioTaskStatus?)null,
            };
            if (statusFilter.HasValue)
            {
                var currentCount = _allTasks.Count(t => t.Status == statusFilter.Value);
                if (currentCount != _currentBucketTasks.Count)
                    RebuildCurrentBucketTasks();
            }
        }

        private void CancelSelectedTask()
        {
            if (SelectedTask == null) return;
            CancelTask(SelectedTask.TaskId);
        }

        private void RetrySelectedTask()
        {
            if (SelectedTask == null) return;
            RetryTask(SelectedTask.TaskId);
        }

        public void CancelTask(string taskId)
        {
            _queueService.Cancel(taskId);
        }

        public void RetryTask(string taskId)
        {
            _queueService.Retry(taskId);
        }

        private void CleanupCompleted()
        {
            _queueService.CleanupCompleted(TimeSpan.FromDays(7));
            Refresh();
        }

        private void OnTaskStatusChanged(TaskStatusChangedEvent e)
        {
            Refresh();
        }

        private void OnTaskProgressUpdated(TaskProgressEvent e)
        {
            var item = _currentBucketTasks.FirstOrDefault(t => t.TaskId == e.TaskId);
            if (item != null)
                item.ProgressMessage = e.ProgressMessage;
        }

        private readonly Dictionary<string, string> _audioNameCache = new();

        private string ResolveAudioFileName(string audioItemId)
        {
            if (_audioNameCache.TryGetValue(audioItemId, out var cached))
                return cached;
            var item = _audioRepo.GetById(audioItemId);
            var name = item?.FileName ?? audioItemId;
            _audioNameCache[audioItemId] = name;
            return name;
        }

        private void RefreshExecutionHistory()
        {
            if (_selectedTask == null)
            {
                Executions = new ObservableCollection<ExecutionRecordViewModel>();
                OnPropertyChanged(nameof(HasExecutions));
                return;
            }

            var records = _executionRepo.GetByTaskId(_selectedTask.TaskId);
            Executions = new ObservableCollection<ExecutionRecordViewModel>(
                records.Select(r => new ExecutionRecordViewModel(r)));
            OnPropertyChanged(nameof(HasExecutions));
        }

        private void RefreshGlobalStats()
        {
            _globalStats = _executionRepo.GetGlobalStats();
            OnPropertyChanged(nameof(TotalExecutions));
            OnPropertyChanged(nameof(BillableExecutions));
            OnPropertyChanged(nameof(BillableTokensIn));
            OnPropertyChanged(nameof(BillableTokensOut));
            OnPropertyChanged(nameof(TokensDisplayText));
        }

        public void Dispose()
        {
            _autoRefreshTimer.Stop();
            _eventBus.TaskStatusChanged -= OnTaskStatusChanged;
            _eventBus.TaskProgressUpdated -= OnTaskProgressUpdated;
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
            ProgressMessage = record.ProgressMessage;
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
        public string? ProgressMessage
        {
            get => _progressMessage;
            set
            {
                if (SetProperty(ref _progressMessage, value))
                {
                    OnPropertyChanged(nameof(StatusDisplayName));
                }
            }
        }
        private string? _progressMessage;
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

        /// <summary>阶段对应的颜色标记（Hex）。</summary>
        public string StageColor => Stage switch
        {
            "Transcribed"   => "#4FC3F7",  // 浅蓝
            "Summarized"    => "#81C784",  // 绿
            "MindMap"        => "#CE93D8",  // 紫
            "Insight"        => "#FFB74D",  // 橙
            "PodcastScript"  => "#F06292",  // 粉
            "PodcastAudio"   => "#E57373",  // 红
            "Research"       => "#64B5F6",  // 蓝
            "Translated"     => "#4DB6AC",  // 青
            _ => "#90A4AE",                 // 灰
        };

        public string StatusDisplayName => Status switch
        {
            AudioTaskStatus.Pending => "排队中",
            AudioTaskStatus.Running => string.IsNullOrWhiteSpace(ProgressMessage) ? "执行中" : ProgressMessage,
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

        public void NotifyElapsedTimeChanged()
        {
            if (Status == AudioTaskStatus.Running)
                OnPropertyChanged(nameof(ElapsedTime));
        }
    }

    /// <summary>执行记录 ViewModel — 用于 UI 绑定显示单条执行记录。</summary>
    public sealed class ExecutionRecordViewModel
    {
        public ExecutionRecordViewModel(TaskExecutionRecord record)
        {
            ExecutionId = record.ExecutionId;
            Status = record.Status;
            Billable = record.Billable;
            ModelName = record.ModelName ?? "--";
            TokensIn = record.TokensIn;
            TokensOut = record.TokensOut;
            DurationMs = record.DurationMs;
            ErrorMessage = record.ErrorMessage;
            CancelReason = record.CancelReason;
            StartedAt = record.StartedAt;
            FinishedAt = record.FinishedAt;
            DebugPrompt = record.DebugPrompt;
            DebugResponse = record.DebugResponse;
        }

        public string ExecutionId { get; }
        public string ShortId => ExecutionId.Length > 10 ? ExecutionId[..10] + ".." : ExecutionId;
        public string Status { get; }
        public bool Billable { get; }
        public string BillableDisplay => Billable ? "是" : "否";
        public string ModelName { get; }
        public int? TokensIn { get; }
        public int? TokensOut { get; }
        public int? DurationMs { get; }
        public string? ErrorMessage { get; }
        public string? CancelReason { get; }
        public DateTime StartedAt { get; }
        public DateTime? FinishedAt { get; }
        public string? DebugPrompt { get; }
        public string? DebugResponse { get; }
        public bool HasDebugData => !string.IsNullOrEmpty(DebugPrompt) || !string.IsNullOrEmpty(DebugResponse);

        public string StatusDisplayName => Status switch
        {
            "Running" => "执行中",
            "Completed" => "✓ 完成",
            "Failed" => "✗ 失败",
            "Cancelled" => "已取消",
            "Interrupted" => "中断",
            _ => Status,
        };

        public string TokensDisplay => (TokensIn.HasValue || TokensOut.HasValue)
            ? $"入 {TokensIn ?? 0} / 出 {TokensOut ?? 0}"
            : "--";

        public string DurationDisplay => DurationMs.HasValue
            ? DurationMs.Value >= 60000
                ? $"{DurationMs.Value / 60000}:{(DurationMs.Value / 1000 % 60):D2}"
                : $"{DurationMs.Value / 1000.0:F1}s"
            : "--";

        public string TimeDisplay => $"{StartedAt:HH:mm:ss}";
    }
}
