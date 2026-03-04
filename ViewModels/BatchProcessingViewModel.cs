using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using Azure.Storage.Blobs;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public class BatchProcessingViewModel : ViewModelBase
    {
        private readonly ObservableCollection<BatchTaskItem> _batchTasks = new();
        private readonly ObservableCollection<BatchQueueItem> _batchQueueItems = new();
        private int _batchConcurrencyLimit = 10;
        private bool _isBatchRunning;
        private CancellationTokenSource? _batchCts;
        private string _batchStatusMessage = "";
        private string _batchQueueStatusText = "队列为空";
        private Task? _batchQueueRunnerTask;
        private List<ReviewSheetPreset> _batchReviewSheetSnapshot = new();

        private bool _isSpeechSubtitleGenerating;
        private string _speechSubtitleStatusMessage = "";
        private CancellationTokenSource? _speechSubtitleCts;

        private readonly ObservableCollection<ReviewSheetState> _reviewSheets = new();
        private ReviewSheetState? _selectedReviewSheet;

        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly Action<string> _statusSetter;
        private readonly AiInsightService _aiInsightService;
        private readonly FileLibraryViewModel _fileLibrary;
        private readonly PlaybackViewModel _playback;
        private readonly ConfigurationService _configService;
        private readonly Action _notifyReviewLampChanged;

        public RelayCommand LoadBatchTasksCommand { get; }
        public RelayCommand ClearBatchTasksCommand { get; }
        public RelayCommand StartBatchCommand { get; }
        public RelayCommand StopBatchCommand { get; }
        public RelayCommand RefreshBatchQueueCommand { get; }
        public RelayCommand CancelBatchQueueItemCommand { get; }
        public RelayCommand GenerateReviewSummaryCommand { get; }
        public RelayCommand GenerateAllReviewSheetsCommand { get; }
        public RelayCommand ReviewMarkdownLinkCommand { get; }
        public RelayCommand EnqueueSubtitleReviewCommand { get; }
        public RelayCommand GenerateSpeechSubtitleCommand { get; }
        public RelayCommand CancelSpeechSubtitleCommand { get; }
        public RelayCommand GenerateBatchSpeechSubtitleCommand { get; }

        private bool IsAiConfigured => _configProvider().AiConfig?.IsValid == true;

        public BatchProcessingViewModel(
            Func<AzureSpeechConfig> configProvider,
            Action<string> statusSetter,
            AiInsightService aiInsightService,
            FileLibraryViewModel fileLibrary,
            PlaybackViewModel playback,
            ConfigurationService configService,
            Action notifyReviewLampChanged)
        {
            _configProvider = configProvider;
            _statusSetter = statusSetter;
            _aiInsightService = aiInsightService;
            _fileLibrary = fileLibrary;
            _playback = playback;
            _configService = configService;
            _notifyReviewLampChanged = notifyReviewLampChanged;

            LoadBatchTasksCommand = new RelayCommand(
                execute: _ => LoadBatchTasksFromLibrary());

            ClearBatchTasksCommand = new RelayCommand(
                execute: _ => ClearBatchTasks(),
                canExecute: _ => BatchTasks.Count > 0);

            StartBatchCommand = new RelayCommand(
                execute: _ => StartBatchProcessing(),
                canExecute: _ => CanStartBatchProcessing());

            _batchTasks.CollectionChanged += (_, _) =>
            {
                ClearBatchTasksCommand.RaiseCanExecuteChanged();
                StartBatchCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(BatchStartButtonText));
            };
            _batchQueueItems.CollectionChanged += (_, _) => UpdateBatchQueueStatusText();

            StopBatchCommand = new RelayCommand(
                execute: _ => StopBatchProcessing(),
                canExecute: _ => IsBatchRunning);

            RefreshBatchQueueCommand = new RelayCommand(
                execute: _ => RefreshBatchQueue(),
                canExecute: _ => true);

            CancelBatchQueueItemCommand = new RelayCommand(
                execute: param => CancelBatchQueueItem(param as BatchQueueItem));

            GenerateReviewSummaryCommand = new RelayCommand(
                execute: _ => GenerateReviewSummary(),
                canExecute: _ => CanGenerateReviewSummary());

            GenerateAllReviewSheetsCommand = new RelayCommand(
                execute: _ => GenerateAllReviewSheets(),
                canExecute: _ => CanGenerateAllReviewSheets());

            ReviewMarkdownLinkCommand = new RelayCommand(
                execute: param => OnReviewMarkdownLink(param));

            EnqueueSubtitleReviewCommand = new RelayCommand(
                execute: param => EnqueueSubtitleAndReviewFromLibrary(param as MediaFileItem),
                canExecute: param => CanEnqueueSubtitleAndReviewFromLibrary(param as MediaFileItem));

            GenerateSpeechSubtitleCommand = new RelayCommand(
                execute: _ => GenerateSpeechSubtitle(),
                canExecute: _ => CanGenerateSpeechSubtitle());

            CancelSpeechSubtitleCommand = new RelayCommand(
                execute: _ => CancelSpeechSubtitle(),
                canExecute: _ => IsSpeechSubtitleGenerating);

            GenerateBatchSpeechSubtitleCommand = new RelayCommand(
                execute: _ => GenerateBatchSpeechSubtitle(),
                canExecute: _ => CanGenerateBatchSpeechSubtitle());
        }

        // ── Properties ──

        public ObservableCollection<BatchTaskItem> BatchTasks => _batchTasks;
        public ObservableCollection<BatchQueueItem> BatchQueueItems => _batchQueueItems;

        public int BatchConcurrencyLimit
        {
            get => _batchConcurrencyLimit;
            set => SetProperty(ref _batchConcurrencyLimit, Math.Clamp(value, 1, 10));
        }

        public bool IsBatchRunning
        {
            get => _isBatchRunning;
            private set
            {
                if (SetProperty(ref _isBatchRunning, value))
                {
                    StartBatchCommand.RaiseCanExecuteChanged();
                    StopBatchCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string BatchStatusMessage
        {
            get => _batchStatusMessage;
            set => SetProperty(ref _batchStatusMessage, value);
        }

        public string BatchQueueStatusText
        {
            get => _batchQueueStatusText;
            private set => SetProperty(ref _batchQueueStatusText, value);
        }

        public bool IsSpeechSubtitleGenerating
        {
            get => _isSpeechSubtitleGenerating;
            private set
            {
                if (SetProperty(ref _isSpeechSubtitleGenerating, value))
                {
                    GenerateSpeechSubtitleCommand.RaiseCanExecuteChanged();
                    GenerateBatchSpeechSubtitleCommand.RaiseCanExecuteChanged();
                    CancelSpeechSubtitleCommand.RaiseCanExecuteChanged();
                    GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
                    GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string SpeechSubtitleStatusMessage
        {
            get => _speechSubtitleStatusMessage;
            private set => SetProperty(ref _speechSubtitleStatusMessage, value);
        }

        public bool IsSpeechSubtitleOptionEnabled
        {
            get
            {
                var config = _configProvider();
                return config.BatchStorageIsValid
                    && !string.IsNullOrWhiteSpace(config.BatchStorageConnectionString);
            }
        }

        public bool UseSpeechSubtitleForReview
        {
            get => _configProvider().UseSpeechSubtitleForReview;
            set
            {
                var config = _configProvider();
                if (config.UseSpeechSubtitleForReview == value)
                {
                    return;
                }

                config.UseSpeechSubtitleForReview = value;
                OnPropertyChanged(nameof(UseSpeechSubtitleForReview));
                OnPropertyChanged(nameof(BatchStartButtonText));
                GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
                GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
                StartBatchCommand.RaiseCanExecuteChanged();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _configService.SaveConfigAsync(config);
                    }
                    catch
                    {
                    }
                });
            }
        }

        public string SpeechSubtitleOptionStatusText => IsSpeechSubtitleOptionEnabled
            ? "存储账号已验证，允许生成 speech 字幕"
            : "未验证存储账号，已禁用该选项";

        public string BatchStartButtonText => GetBatchStartButtonText();

        public IBrush ReviewSummaryLampFill
        {
            get
            {
                if (IsReviewSummaryLoading)
                {
                    return Brushes.Orange;
                }

                return string.IsNullOrWhiteSpace(ReviewSummaryMarkdown)
                    ? Brushes.Gray
                    : Brushes.LimeGreen;
            }
        }

        public IBrush ReviewSummaryLampStroke
        {
            get
            {
                if (IsReviewSummaryLoading)
                {
                    return Brushes.DarkOrange;
                }

                return string.IsNullOrWhiteSpace(ReviewSummaryMarkdown)
                    ? Brushes.DarkGray
                    : Brushes.Green;
            }
        }

        public ObservableCollection<ReviewSheetState> ReviewSheets => _reviewSheets;

        public ReviewSheetState? SelectedReviewSheet
        {
            get => _selectedReviewSheet;
            set
            {
                if (ReferenceEquals(_selectedReviewSheet, value))
                {
                    return;
                }

                if (_selectedReviewSheet != null)
                {
                    _selectedReviewSheet.PropertyChanged -= OnSelectedReviewSheetPropertyChanged;
                }

                _selectedReviewSheet = value;
                OnPropertyChanged(nameof(SelectedReviewSheet));

                OnPropertyChanged(nameof(ReviewSummaryMarkdown));
                OnPropertyChanged(nameof(ReviewSummaryStatusMessage));
                OnPropertyChanged(nameof(IsReviewSummaryLoading));
                OnPropertyChanged(nameof(IsReviewSummaryEmpty));
                OnPropertyChanged(nameof(ReviewSummaryLampFill));
                OnPropertyChanged(nameof(ReviewSummaryLampStroke));
                _notifyReviewLampChanged();

                if (_selectedReviewSheet != null)
                {
                    _selectedReviewSheet.PropertyChanged += OnSelectedReviewSheetPropertyChanged;
                }

                LoadReviewSheetForAudio(_fileLibrary.SelectedAudioFile, _selectedReviewSheet);
            }
        }

        public string ReviewSummaryMarkdown => SelectedReviewSheet?.Markdown ?? "";
        public string ReviewSummaryStatusMessage => SelectedReviewSheet?.StatusMessage ?? "";
        public bool IsReviewSummaryLoading => SelectedReviewSheet?.IsLoading ?? false;
        public bool IsReviewSummaryEmpty => string.IsNullOrWhiteSpace(ReviewSummaryMarkdown) && !IsReviewSummaryLoading;

        // ── Called by MainWindowViewModel when audio selection changes ──

        public void OnAudioFileSelected(MediaFileItem? audioFile)
        {
            CancelAllReviewSheetGeneration();
            LoadReviewSheetForAudio(audioFile, SelectedReviewSheet);
            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
            GenerateSpeechSubtitleCommand.RaiseCanExecuteChanged();
            GenerateBatchSpeechSubtitleCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Called when subtitle cues finish loading to refresh command states.</summary>
        public void OnSubtitleCuesLoaded()
        {
            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
        }

        // ── Called when config changes ──

        public void RebuildReviewSheets()
        {
            var config = _configProvider();
            var currentTag = SelectedReviewSheet?.FileTag;
            _reviewSheets.Clear();

            var presets = config.AiConfig?.ReviewSheets;
            if (presets == null || presets.Count == 0)
            {
                presets = new AiConfig().ReviewSheets;
            }

            foreach (var preset in presets)
            {
                _reviewSheets.Add(ReviewSheetState.FromPreset(preset));
            }

            SelectedReviewSheet = _reviewSheets.FirstOrDefault(s => s.FileTag == currentTag)
                                   ?? _reviewSheets.FirstOrDefault();
        }

        public void NormalizeSpeechSubtitleOption()
        {
            var config = _configProvider();
            if (!IsSpeechSubtitleOptionEnabled && config.UseSpeechSubtitleForReview)
            {
                config.UseSpeechSubtitleForReview = false;
            }
        }

        public void RefreshCommandStates()
        {
            OnPropertyChanged(nameof(IsSpeechSubtitleOptionEnabled));
            OnPropertyChanged(nameof(UseSpeechSubtitleForReview));
            OnPropertyChanged(nameof(SpeechSubtitleOptionStatusText));
            OnPropertyChanged(nameof(BatchStartButtonText));
            GenerateSpeechSubtitleCommand.RaiseCanExecuteChanged();
            GenerateBatchSpeechSubtitleCommand.RaiseCanExecuteChanged();
            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
            StartBatchCommand.RaiseCanExecuteChanged();
        }

        // ── Called from MainWindow.axaml.cs ──

        public void EnqueueSubtitleAndReviewFromLibraryUi(MediaFileItem? audioFile)
        {
            EnqueueSubtitleAndReviewFromLibrary(audioFile);
        }

        public void AuditUiEvent(string eventName, string message, bool isSuccess = true)
            => AppLogService.Instance.LogAudit(eventName, message, isSuccess);

        // ── RefreshAudioLibrary supplement: clear review sheets ──

        public void OnAudioLibraryRefreshed()
        {
            foreach (var sheet in _reviewSheets)
            {
                sheet.Markdown = "";
                sheet.StatusMessage = "";
            }
        }

        // ── Private helpers ──

        private bool ShouldGenerateSpeechSubtitleForReview
        {
            get
            {
                var config = _configProvider();
                return IsSpeechSubtitleOptionEnabled && config.UseSpeechSubtitleForReview;
            }
        }

        private bool ShouldWriteBatchLogSuccess => AppLogService.Instance.ShouldLogSuccess;
        private bool ShouldWriteBatchLogFailure => AppLogService.Instance.ShouldLogFailure;
        private void EnsureBatchLogFile() => AppLogService.Instance.EnsureBatchFile();

        private void AppendBatchLog(string eventName, string fileName, string status, string message)
            => AppLogService.Instance.LogBatch(eventName, fileName, status, message);

        private void AppendBatchDebugLog(string eventName, string message, bool isSuccess = true)
            => AppLogService.Instance.LogAudit(eventName, message, isSuccess);

        private static string FormatBatchExceptionForLog(Exception ex)
            => AppLogService.FormatException(ex);

        private void OnSelectedReviewSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ReviewSummaryMarkdown));
            OnPropertyChanged(nameof(ReviewSummaryStatusMessage));
            OnPropertyChanged(nameof(IsReviewSummaryLoading));
            OnPropertyChanged(nameof(IsReviewSummaryEmpty));
            OnPropertyChanged(nameof(ReviewSummaryLampFill));
            OnPropertyChanged(nameof(ReviewSummaryLampStroke));
            _notifyReviewLampChanged();
            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
        }

        private void UpdateBatchQueueStatusText()
        {
            var total = _batchQueueItems.Count;
            var completed = _batchQueueItems.Count(item => item.Status == BatchTaskStatus.Completed);
            var running = _batchQueueItems.Count(item => item.Status == BatchTaskStatus.Running);
            var failed = _batchQueueItems.Count(item => item.Status == BatchTaskStatus.Failed);
            var pending = Math.Max(total - completed - running - failed, 0);

            BatchQueueStatusText = total == 0
                ? "队列为空"
                : $"队列 {completed}/{total} 完成，运行 {running}，等待 {pending}，失败 {failed}";
        }

        // ── Batch task loading ──

        private void LoadBatchTasksFromLibrary()
        {
            if (_fileLibrary.AudioFiles.Count == 0)
            {
                _fileLibrary.RefreshAudioLibrary();
                OnAudioLibraryRefreshed();
            }

            var batchSheets = GetBatchReviewSheets();
            _batchTasks.Clear();
            foreach (var audio in _fileLibrary.AudioFiles)
            {
                var totalSheets = batchSheets.Count;
                var completedSheets = 0;
                foreach (var sheet in batchSheets)
                {
                    if (File.Exists(GetReviewSheetPath(audio.FullPath, sheet.FileTag)))
                    {
                        completedSheets++;
                    }
                }
                var hasAiSummary = totalSheets > 0 && completedSheets >= totalSheets;
                var requireSpeech = ShouldGenerateSpeechSubtitleForReview;
                var hasSpeechSubtitle = FileLibraryViewModel.HasSpeechSubtitle(audio.FullPath);
                var subtitlePath = requireSpeech
                    ? (hasSpeechSubtitle ? FileLibraryViewModel.GetSpeechSubtitlePath(audio.FullPath) : "")
                    : FileLibraryViewModel.GetPreferredSubtitlePath(audio.FullPath);
                var hasSubtitle = requireSpeech
                    ? hasSpeechSubtitle
                    : !string.IsNullOrWhiteSpace(subtitlePath);
                var hasAiSubtitle = FileLibraryViewModel.HasAiSubtitle(audio.FullPath);
                var pendingSheets = Math.Max(totalSheets - completedSheets, 0);
                var statusMessage = hasSubtitle
                    ? "待处理"
                    : (requireSpeech ? "待生成 speech 字幕" : "缺少字幕");
                var reviewStatusText = totalSheets == 0
                    ? "复盘:未勾选"
                    : $"复盘 {completedSheets}/{totalSheets}";
                if (requireSpeech && !hasSubtitle && totalSheets > 0)
                {
                    reviewStatusText = "复盘:等待字幕";
                }

                _batchTasks.Add(new BatchTaskItem
                {
                    FileName = audio.Name,
                    FullPath = audio.FullPath,
                    Status = BatchTaskStatus.Pending,
                    Progress = 0,
                    HasAiSubtitle = hasAiSubtitle,
                    HasAiSummary = hasAiSummary,
                    StatusMessage = statusMessage,
                    ReviewTotal = totalSheets,
                    ReviewCompleted = completedSheets,
                    ReviewFailed = 0,
                    ReviewPending = pendingSheets,
                    ReviewStatusText = reviewStatusText
                });
            }

            _statusSetter(_batchTasks.Count == 0
                ? "未找到可批处理的音频文件"
                : $"已载入 {_batchTasks.Count} 条批处理任务");
            BatchStatusMessage = "";
        }

        private List<ReviewSheetPreset> GetBatchReviewSheets()
        {
            var config = _configProvider();
            var sheets = config.AiConfig?.ReviewSheets
                ?.Where(s => s.IncludeInBatch)
                .ToList();

            return sheets ?? new List<ReviewSheetPreset>();
        }

        private void ClearBatchTasks()
        {
            _batchTasks.Clear();
            _statusSetter("批处理任务已清空");
            BatchStatusMessage = "";
        }

        private bool CanStartBatchProcessing()
        {
            if (IsBatchRunning || BatchTasks.Count == 0)
            {
                return false;
            }

            var batchSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && batchSheets.Count > 0;
            var enableSpeech = ShouldGenerateSpeechSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                return false;
            }

            var needsSpeech = enableSpeech && BatchTasks.Any(task => !FileLibraryViewModel.HasSpeechSubtitle(task.FullPath));
            if (needsSpeech && !CanGenerateSpeechSubtitleFromStorage())
            {
                return false;
            }

            return true;
        }

        private string GetBatchStartButtonText()
        {
            var batchSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && batchSheets.Count > 0;
            var enableSpeech = ShouldGenerateSpeechSubtitleForReview;

            if (enableSpeech && enableReview)
            {
                return "开始生成字幕+复盘";
            }

            if (enableSpeech)
            {
                return "开始生成字幕";
            }

            if (enableReview)
            {
                return "开始生成复盘";
            }

            return "开始处理";
        }

        private bool CanGenerateSpeechSubtitleFromStorage()
        {
            if (!IsSpeechSubtitleOptionEnabled)
            {
                return false;
            }

            var config = _configProvider();
            var subscription = config.GetActiveSubscription();
            return subscription?.IsValid() == true && !string.IsNullOrWhiteSpace(config.SourceLanguage);
        }

        // ── Batch processing ──

        private void StartBatchProcessing()
        {
            if (BatchTasks.Count == 0)
            {
                BatchStatusMessage = "没有可处理的任务";
                return;
            }

            var config = _configProvider();
            var batchSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && batchSheets.Count > 0;
            var enableSpeech = ShouldGenerateSpeechSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                BatchStatusMessage = "未启用 speech 字幕或复盘生成";
                return;
            }

            var needsSpeech = enableSpeech && BatchTasks.Any(task => !FileLibraryViewModel.HasSpeechSubtitle(task.FullPath));
            if (needsSpeech && !CanGenerateSpeechSubtitleFromStorage())
            {
                BatchStatusMessage = "speech 字幕需要有效的存储账号与语音订阅";
                return;
            }

            _batchCts?.Cancel();
            IsBatchRunning = true;
            BatchStatusMessage = "批处理已开始";
            AppLogService.Instance.ResetBatchFile();
            EnsureBatchLogFile();
            if (ShouldWriteBatchLogSuccess)
            {
                AppendBatchLog("BatchStart", "-", "Success", "批处理开始");
            }

            _batchQueueItems.Clear();
            _batchReviewSheetSnapshot = batchSheets.ToList();
            foreach (var batchItem in BatchTasks)
            {
                PrepareAndEnqueueSingleItem(batchItem, batchSheets, enableSpeech, enableReview,
                    config.BatchForceRegeneration);
            }

            if (_batchQueueItems.Count == 0)
            {
                IsBatchRunning = false;
                BatchStatusMessage = "批处理完成";
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("BatchComplete", "-", "Success", "无待处理任务");
                }
                return;
            }

            StartBatchQueueRunner("批处理已开始");
        }

        private void StopBatchProcessing()
        {
            _batchCts?.Cancel();
            foreach (var item in _batchQueueItems)
            {
                item.Cts?.Cancel();
            }
            BatchStatusMessage = "正在停止批处理...";
            if (ShouldWriteBatchLogFailure)
            {
                AppendBatchLog("BatchStop", "-", "Failed", "批处理停止");
            }
        }

        private void RefreshBatchQueue()
        {
            var completedItems = _batchQueueItems
                .Where(item => item.Status == BatchTaskStatus.Completed)
                .ToList();

            if (completedItems.Count == 0)
            {
                BatchStatusMessage = "队列中暂无可清理的已完成项";
                return;
            }

            foreach (var item in completedItems)
            {
                _batchQueueItems.Remove(item);
            }

            UpdateBatchQueueStatusText();
            BatchStatusMessage = $"已清理 {completedItems.Count} 条已完成队列项";
        }

        private void CancelBatchQueueItem(BatchQueueItem? item)
        {
            if (item == null)
            {
                return;
            }

            item.Cts?.Cancel();
            UpdateQueueItem(item, BatchTaskStatus.Failed, item.Progress, "已取消");
            if (ShouldWriteBatchLogFailure)
            {
                var eventName = item.QueueType == BatchQueueItemType.SpeechSubtitle
                    ? "SpeechCanceled"
                    : "ReviewCanceled";
                AppendBatchLog(eventName, item.FileName, "Failed", "已取消");
            }

            var parent = BatchTasks.FirstOrDefault(x => x.FullPath == item.FullPath);
            if (parent != null)
            {
                if (item.QueueType == BatchQueueItemType.SpeechSubtitle)
                {
                    parent.ReviewStatusText = "复盘:字幕取消";
                    UpdateBatchItem(parent, BatchTaskStatus.Failed, 0, "字幕已取消");
                }
                else
                {
                    UpdateBatchReviewProgress(parent, BatchTaskStatus.Failed);
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _batchQueueItems.Remove(item);
            });
        }

        private void EnqueueSpeechSubtitleForBatch(BatchTaskItem batchItem)
        {
            var existsInQueue = _batchQueueItems.Any(item =>
                item.QueueType == BatchQueueItemType.SpeechSubtitle
                && string.Equals(item.FullPath, batchItem.FullPath, StringComparison.OrdinalIgnoreCase)
                && item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running);

            if (existsInQueue)
            {
                return;
            }

            _batchQueueItems.Add(new BatchQueueItem
            {
                FileName = batchItem.FileName,
                FullPath = batchItem.FullPath,
                SheetName = "speech 字幕",
                SheetTag = "speech",
                Prompt = "",
                QueueType = BatchQueueItemType.SpeechSubtitle,
                Status = BatchTaskStatus.Pending,
                Progress = 0,
                StatusMessage = "待处理"
            });
            if (ShouldWriteBatchLogSuccess)
            {
                AppendBatchLog("SpeechEnqueue", batchItem.FileName, "Success", "入队 speech 字幕");
            }
        }

        private int EnqueueReviewQueueItemsForAudioInternal(
            BatchTaskItem parentItem,
            IEnumerable<ReviewSheetPreset> sheets,
            bool ignoreExistingFiles = false)
        {
            var added = 0;
            foreach (var sheet in sheets)
            {
                if (!ignoreExistingFiles && File.Exists(GetReviewSheetPath(parentItem.FullPath, sheet.FileTag)))
                {
                    continue;
                }

                var existsInQueue = _batchQueueItems.Any(item =>
                    item.QueueType == BatchQueueItemType.ReviewSheet
                    && string.Equals(item.FullPath, parentItem.FullPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.SheetTag, sheet.FileTag, StringComparison.OrdinalIgnoreCase)
                    && item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running);

                if (existsInQueue)
                {
                    continue;
                }

                _batchQueueItems.Add(new BatchQueueItem
                {
                    FileName = parentItem.FileName,
                    FullPath = parentItem.FullPath,
                    SheetName = sheet.Name,
                    SheetTag = sheet.FileTag,
                    Prompt = sheet.Prompt,
                    QueueType = BatchQueueItemType.ReviewSheet,
                    Status = BatchTaskStatus.Pending,
                    Progress = 0,
                    StatusMessage = "待处理"
                });
                added++;
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("ReviewEnqueue", parentItem.FileName, "Success", $"入队 {sheet.Name}");
                }
            }

            return added;
        }

        private int PrepareAndEnqueueSingleItem(
            BatchTaskItem batchItem,
            List<ReviewSheetPreset> reviewSheets,
            bool enableSpeech,
            bool enableReview,
            bool forceRegeneration)
        {
            var speechExists = FileLibraryViewModel.HasSpeechSubtitle(batchItem.FullPath);
            batchItem.HasAiSubtitle = enableSpeech ? speechExists : FileLibraryViewModel.HasAiSubtitle(batchItem.FullPath);

            if (enableSpeech && (forceRegeneration || !speechExists))
            {
                batchItem.HasAiSubtitle = false;
                batchItem.ForceReviewRegeneration = forceRegeneration && enableReview;
                batchItem.ReviewTotal = enableReview ? reviewSheets.Count : 0;
                batchItem.ReviewCompleted = 0;
                batchItem.ReviewFailed = 0;
                batchItem.ReviewPending = enableReview ? reviewSheets.Count : 0;
                batchItem.ReviewStatusText = enableReview ? "复盘:等待字幕" : "复盘:未启用";
                batchItem.HasAiSummary = false;
                UpdateBatchItem(batchItem, BatchTaskStatus.Pending, 0, "待生成 speech 字幕");
                EnqueueSpeechSubtitleForBatch(batchItem);
                return 1;
            }

            if (!enableReview)
            {
                batchItem.ReviewTotal = 0;
                batchItem.ReviewCompleted = 0;
                batchItem.ReviewFailed = 0;
                batchItem.ReviewPending = 0;
                batchItem.ReviewStatusText = "复盘:未启用";
                batchItem.HasAiSubtitle = speechExists || FileLibraryViewModel.HasAiSubtitle(batchItem.FullPath);
                UpdateBatchItem(batchItem, BatchTaskStatus.Completed, 1, "字幕已存在");
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("SpeechSkip", batchItem.FileName, "Success", "speech 字幕已存在");
                }
                return 0;
            }

            if (!enableSpeech)
            {
                var subtitlePath = FileLibraryViewModel.GetPreferredSubtitlePath(batchItem.FullPath);
                if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
                {
                    batchItem.ReviewTotal = reviewSheets.Count;
                    batchItem.ReviewCompleted = 0;
                    batchItem.ReviewFailed = 0;
                    batchItem.ReviewPending = reviewSheets.Count;
                    batchItem.ReviewStatusText = "复盘:缺少字幕";
                    UpdateBatchItem(batchItem, BatchTaskStatus.Failed, 0, "缺少字幕");
                    if (ShouldWriteBatchLogFailure)
                    {
                        AppendBatchLog("ReviewSkip", batchItem.FileName, "Failed", "缺少字幕");
                    }
                    return 0;
                }
            }

            var completed = forceRegeneration ? 0 : reviewSheets.Count(s =>
                File.Exists(GetReviewSheetPath(batchItem.FullPath, s.FileTag)));
            var pending = Math.Max(reviewSheets.Count - completed, 0);

            batchItem.ReviewTotal = reviewSheets.Count;
            batchItem.ReviewCompleted = completed;
            batchItem.ReviewFailed = 0;
            batchItem.ReviewPending = pending;
            batchItem.ReviewStatusText = reviewSheets.Count == 0
                ? "复盘:未勾选"
                : $"复盘 {completed}/{reviewSheets.Count}";
            batchItem.HasAiSummary = false;
            batchItem.ForceReviewRegeneration = forceRegeneration;

            if (pending == 0)
            {
                batchItem.HasAiSummary = true;
                UpdateBatchItem(batchItem, BatchTaskStatus.Completed, 1, "已存在");
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("ReviewSkip", batchItem.FileName, "Success", "复盘已存在");
                }
                return 0;
            }

            UpdateBatchItem(batchItem, BatchTaskStatus.Pending, 0, "待处理");
            var added = EnqueueReviewQueueItemsForAudioInternal(batchItem, reviewSheets, forceRegeneration);
            batchItem.ForceReviewRegeneration = false;
            return added;
        }

        private void StartBatchQueueRunner(string statusMessage)
        {
            if (_batchQueueRunnerTask != null && !_batchQueueRunnerTask.IsCompleted)
            {
                return;
            }

            if (_batchQueueItems.Count == 0)
            {
                return;
            }

            _batchCts?.Cancel();
            _batchCts = new CancellationTokenSource();
            var token = _batchCts.Token;

            IsBatchRunning = true;
            BatchStatusMessage = statusMessage;

            _batchQueueRunnerTask = Task.Run(async () =>
            {
                var running = new List<Task>();
                var cueCache = new Dictionary<string, List<SubtitleCue>>();
                var cueLock = new object();

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        running.RemoveAll(t => t.IsCompleted);

                        while (running.Count < BatchConcurrencyLimit)
                        {
                            var next = await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var item = _batchQueueItems.FirstOrDefault(i => i.Status == BatchTaskStatus.Pending);
                                if (item != null)
                                {
                                    item.Status = BatchTaskStatus.Running;
                                    item.StatusMessage = "调度中";
                                }
                                return item;
                            });

                            if (next == null)
                            {
                                break;
                            }

                            var parent = await Dispatcher.UIThread.InvokeAsync(() =>
                                BatchTasks.FirstOrDefault(x => x.FullPath == next.FullPath));

                            running.Add(Task.Run(() =>
                                ProcessBatchQueueItem(next, parent, token, cueCache, cueLock), token));
                        }

                        if (running.Count == 0)
                        {
                            break;
                        }

                        var finishedOrWake = await Task.WhenAny(
                            Task.WhenAny(running),
                            Task.Delay(TimeSpan.FromMilliseconds(300), token));

                        if (finishedOrWake != null && finishedOrWake.Status == TaskStatus.RanToCompletion)
                        {
                            running.RemoveAll(t => t.IsCompleted);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }

                Dispatcher.UIThread.Post(() =>
                {
                    IsBatchRunning = false;
                    BatchStatusMessage = token.IsCancellationRequested
                        ? "批处理已停止"
                        : "批处理完成";
                    if (!token.IsCancellationRequested && ShouldWriteBatchLogSuccess)
                    {
                        AppendBatchLog("BatchComplete", "-", "Success", "批处理完成");
                    }
                });
            }, token);
        }

        private async Task ProcessBatchQueueItem(
            BatchQueueItem queueItem,
            BatchTaskItem? parentItem,
            CancellationToken token,
            Dictionary<string, List<SubtitleCue>> cueCache,
            object cueLock)
        {
            if (!_batchQueueItems.Contains(queueItem))
            {
                return;
            }

            queueItem.Cts?.Cancel();
            queueItem.Cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var localToken = queueItem.Cts.Token;

            UpdateQueueItem(queueItem, BatchTaskStatus.Running, 0.1, "生成中");
            if (ShouldWriteBatchLogSuccess)
            {
                var eventName = queueItem.QueueType == BatchQueueItemType.SpeechSubtitle
                    ? "SpeechStart"
                    : "ReviewStart";
                AppendBatchLog(eventName, queueItem.FileName, "Success", queueItem.SheetName);
            }
            if (parentItem != null)
            {
                UpdateBatchItem(parentItem, BatchTaskStatus.Running, parentItem.Progress, "生成中");
            }

            if (queueItem.QueueType == BatchQueueItemType.SpeechSubtitle)
            {
                await ProcessSpeechSubtitleQueueItem(queueItem, parentItem, localToken, cueCache, cueLock);
                return;
            }

            var cues = GetBatchCues(queueItem.FullPath, cueCache, cueLock);
            if (cues.Count == 0)
            {
                UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, "字幕为空");
                if (ShouldWriteBatchLogFailure)
                {
                    AppendBatchLog("ReviewFailed", queueItem.FileName, "Failed", "字幕为空");
                }
                if (parentItem != null)
                {
                    UpdateBatchReviewProgress(parentItem, BatchTaskStatus.Failed);
                }
                return;
            }

            var config = _configProvider();
            var systemPrompt = config.AiConfig?.ReviewSystemPrompt;
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                systemPrompt = new AiConfig().ReviewSystemPrompt;
            }
            var prompt = string.IsNullOrWhiteSpace(queueItem.Prompt)
                ? "请生成复盘总结。"
                : queueItem.Prompt.Trim();
            var userTemplate = config.AiConfig?.ReviewUserContentTemplate;
            if (string.IsNullOrWhiteSpace(userTemplate))
            {
                userTemplate = new AiConfig().ReviewUserContentTemplate;
            }
            var userPrompt = userTemplate
                .Replace("{subtitle}", FormatSubtitleForSummary(cues))
                .Replace("{prompt}", prompt);

            try
            {
                var sb = new System.Text.StringBuilder();
                AiRequestOutcome? outcome = null;
                await _aiInsightService.StreamChatAsync(
                    config.AiConfig!,
                    systemPrompt,
                    userPrompt,
                    chunk =>
                    {
                        sb.Append(chunk);
                    },
                    localToken,
                    AiChatProfile.Summary,
                    enableReasoning: config.AiConfig!.SummaryEnableReasoning,
                    onOutcome: o => outcome = o);

                var markdown = TimeLinkHelper.InjectTimeLinks(sb.ToString());
                var summaryPath = GetReviewSheetPath(queueItem.FullPath, queueItem.SheetTag);
                File.WriteAllText(summaryPath, markdown);
                var note = "完成";
                if (outcome?.UsedFallback == true)
                {
                    note = "完成(非思考,已降级)";
                }
                else if (outcome?.UsedReasoning == true)
                {
                    note = "完成(思考)";
                }
                else if (config.AiConfig!.SummaryEnableReasoning)
                {
                    note = "完成(非思考)";
                }

                UpdateQueueItem(queueItem, BatchTaskStatus.Completed, 1, note);
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("ReviewSuccess", queueItem.FileName, "Success", queueItem.SheetName);
                }
                if (parentItem != null)
                {
                    UpdateBatchReviewProgress(parentItem, BatchTaskStatus.Completed);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, "已取消");
                if (ShouldWriteBatchLogFailure)
                {
                    AppendBatchLog("ReviewCanceled", queueItem.FileName, "Failed", "已取消");
                }
                if (parentItem != null)
                {
                    UpdateBatchReviewProgress(parentItem, BatchTaskStatus.Failed);
                }
            }
            catch (Exception ex)
            {
                UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, $"失败: {ex.Message}");
                if (ShouldWriteBatchLogFailure)
                {
                    AppendBatchLog("ReviewFailed", queueItem.FileName, "Failed", ex.ToString());
                }
                if (parentItem != null)
                {
                    UpdateBatchReviewProgress(parentItem, BatchTaskStatus.Failed);
                }
            }
        }

        private async Task ProcessSpeechSubtitleQueueItem(
            BatchQueueItem queueItem,
            BatchTaskItem? parentItem,
            CancellationToken token,
            Dictionary<string, List<SubtitleCue>> cueCache,
            object cueLock)
        {
            try
            {
                var success = await GenerateBatchSpeechSubtitleForFileAsync(
                    queueItem.FullPath,
                    token,
                    status => UpdateQueueItem(queueItem, BatchTaskStatus.Running, 0.2, status));

                if (!success)
                {
                    UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, "未识别到有效文本");
                    if (ShouldWriteBatchLogFailure)
                    {
                        AppendBatchLog("SpeechFailed", queueItem.FileName, "Failed", "未识别到有效文本");
                    }
                    if (parentItem != null)
                    {
                        parentItem.ReviewStatusText = "复盘:字幕失败";
                        UpdateBatchItem(parentItem, BatchTaskStatus.Failed, 0, "字幕失败");
                    }
                    return;
                }

                UpdateQueueItem(queueItem, BatchTaskStatus.Completed, 1, "完成");
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("SpeechSuccess", queueItem.FileName, "Success", "speech 字幕完成");
                }

                lock (cueLock)
                {
                    cueCache.Remove(queueItem.FullPath);
                }

                if (parentItem != null)
                {
                    parentItem.HasAiSubtitle = true;
                }

                var enableReview = IsAiConfigured && _batchReviewSheetSnapshot.Count > 0;
                if (!enableReview)
                {
                    if (parentItem != null)
                    {
                        UpdateBatchItem(parentItem, BatchTaskStatus.Completed, 1, "字幕完成");
                    }
                    return;
                }

                if (parentItem != null)
                {
                    var completed = _batchReviewSheetSnapshot.Count(sheet =>
                        File.Exists(GetReviewSheetPath(parentItem.FullPath, sheet.FileTag)));
                    parentItem.ReviewTotal = _batchReviewSheetSnapshot.Count;
                    parentItem.ReviewCompleted = completed;
                    parentItem.ReviewFailed = 0;
                    parentItem.ReviewPending = Math.Max(parentItem.ReviewTotal - completed, 0);
                    parentItem.ReviewStatusText = parentItem.ReviewTotal == 0
                        ? "复盘:未勾选"
                        : $"复盘 {completed}/{parentItem.ReviewTotal}";

                    if (parentItem.ReviewPending == 0)
                    {
                        parentItem.HasAiSummary = true;
                        UpdateBatchItem(parentItem, BatchTaskStatus.Completed, 1, "已存在");
                        return;
                    }
                }

                var added = await Dispatcher.UIThread.InvokeAsync(() =>
                    parentItem == null
                        ? 0
                        : EnqueueReviewQueueItemsForAudioInternal(
                            parentItem,
                            _batchReviewSheetSnapshot,
                            parentItem.ForceReviewRegeneration));

                if (parentItem != null)
                {
                    parentItem.ForceReviewRegeneration = false;
                }

                if (parentItem != null)
                {
                    var statusMessage = added > 0 ? "字幕完成，待复盘" : "字幕完成";
                    UpdateBatchItem(parentItem, BatchTaskStatus.Running, parentItem.Progress, statusMessage);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, "已取消");
                if (ShouldWriteBatchLogFailure)
                {
                    AppendBatchLog("SpeechCanceled", queueItem.FileName, "Failed", "已取消");
                }
                if (parentItem != null)
                {
                    UpdateBatchItem(parentItem, BatchTaskStatus.Failed, 0, "字幕已取消");
                }
            }
            catch (Exception ex)
            {
                UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, $"失败: {ex.Message}");
                if (ShouldWriteBatchLogFailure)
                {
                    AppendBatchLog("SpeechFailed", queueItem.FileName, "Failed", FormatBatchExceptionForLog(ex));
                }
                if (parentItem != null)
                {
                    UpdateBatchItem(parentItem, BatchTaskStatus.Failed, 0, "字幕失败");
                }
            }
        }

        private List<SubtitleCue> GetBatchCues(
            string audioPath,
            Dictionary<string, List<SubtitleCue>> cueCache,
            object cueLock)
        {
            lock (cueLock)
            {
                if (cueCache.TryGetValue(audioPath, out var cached))
                {
                    return cached;
                }
            }

            var subtitlePath = ShouldGenerateSpeechSubtitleForReview
                ? FileLibraryViewModel.GetSpeechSubtitlePath(audioPath)
                : FileLibraryViewModel.GetPreferredSubtitlePath(audioPath);
            if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
            {
                return new List<SubtitleCue>();
            }

            var cues = SubtitleFileParser.ParseSubtitleFileToCues(subtitlePath);
            lock (cueLock)
            {
                cueCache[audioPath] = cues;
            }

            return cues;
        }

        private void UpdateBatchReviewProgress(BatchTaskItem item, BatchTaskStatus sheetStatus)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (sheetStatus == BatchTaskStatus.Completed)
                {
                    item.ReviewCompleted++;
                }
                else if (sheetStatus == BatchTaskStatus.Failed)
                {
                    item.ReviewFailed++;
                }

                item.ReviewPending = Math.Max(item.ReviewTotal - item.ReviewCompleted - item.ReviewFailed, 0);
                if (item.ReviewTotal > 0)
                {
                    item.ReviewStatusText = $"复盘 {item.ReviewCompleted}/{item.ReviewTotal}";
                }
                else
                {
                    item.ReviewStatusText = "复盘:未勾选";
                }

                var progress = item.ReviewTotal == 0
                    ? 1
                    : (double)(item.ReviewCompleted + item.ReviewFailed) / item.ReviewTotal;
                var finished = item.ReviewPending == 0;
                var status = finished
                    ? (item.ReviewFailed > 0 ? BatchTaskStatus.Failed : BatchTaskStatus.Completed)
                    : BatchTaskStatus.Running;
                var statusMessage = finished
                    ? (item.ReviewFailed > 0 ? "完成(含失败)" : "完成")
                    : "生成中";

                item.HasAiSummary = item.ReviewTotal > 0 && item.ReviewCompleted >= item.ReviewTotal;
                UpdateBatchItem(item, status, progress, statusMessage);
            });
        }

        private void UpdateQueueItem(BatchQueueItem item, BatchTaskStatus status, double progress, string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_batchQueueItems.Contains(item))
                {
                    return;
                }

                item.Status = status;
                item.Progress = progress;
                item.StatusMessage = message;
                UpdateBatchQueueStatusText();
            });
        }

        private static void UpdateBatchItem(BatchTaskItem item, BatchTaskStatus status, double progress, string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.Status = status;
                item.Progress = progress;
                item.StatusMessage = message;
            });
        }

        // ── Review sheet loading / generation ──

        private void LoadReviewSheetForAudio(MediaFileItem? audioFile, ReviewSheetState? sheet)
        {
            if (sheet != null)
            {
                sheet.Markdown = "";
                sheet.StatusMessage = "";
                sheet.IsLoading = false;
            }

            if (audioFile == null || string.IsNullOrWhiteSpace(audioFile.FullPath) || sheet == null)
            {
                return;
            }

            var sheetPath = GetReviewSheetPath(audioFile.FullPath, sheet.FileTag);
            if (File.Exists(sheetPath))
            {
                sheet.Markdown = TimeLinkHelper.InjectTimeLinks(File.ReadAllText(sheetPath));
                sheet.StatusMessage = $"已加载: {Path.GetFileName(sheetPath)}";
            }
            else
            {
                sheet.StatusMessage = "未找到复盘内容，可生成。";
            }

            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
        }

        private static string GetReviewSheetPath(string audioFilePath, string fileTag)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            var tag = string.IsNullOrWhiteSpace(fileTag) ? "summary" : fileTag.Trim();
            return Path.Combine(directory, baseName + $".ai.{tag}.md");
        }

        private void CancelAllReviewSheetGeneration()
        {
            foreach (var sheet in _reviewSheets)
            {
                sheet.Cts?.Cancel();
                sheet.Cts = null;
                sheet.IsLoading = false;
            }
        }

        private void EnqueueReviewSheetsForAudio(MediaFileItem audioFile, IEnumerable<ReviewSheetState> sheets)
        {
            var requireSpeech = ShouldGenerateSpeechSubtitleForReview;
            var subtitlePath = requireSpeech
                ? FileLibraryViewModel.GetSpeechSubtitlePath(audioFile.FullPath)
                : FileLibraryViewModel.GetPreferredSubtitlePath(audioFile.FullPath);
            var hasSubtitle = requireSpeech
                ? File.Exists(subtitlePath)
                : !string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath);
            if (!hasSubtitle)
            {
                foreach (var sheet in sheets)
                {
                    sheet.StatusMessage = requireSpeech ? "缺少 speech 字幕" : "缺少字幕";
                }
                return;
            }

            foreach (var sheet in sheets)
            {
                var sheetPath = GetReviewSheetPath(audioFile.FullPath, sheet.FileTag);
                if (File.Exists(sheetPath))
                {
                    continue;
                }

                var existsInQueue = _batchQueueItems.Any(item =>
                    string.Equals(item.FullPath, audioFile.FullPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.SheetTag, sheet.FileTag, StringComparison.OrdinalIgnoreCase)
                    && item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running);

                if (existsInQueue)
                {
                    continue;
                }

                _batchQueueItems.Add(new BatchQueueItem
                {
                    FileName = audioFile.Name,
                    FullPath = audioFile.FullPath,
                    SheetName = sheet.Name,
                    SheetTag = sheet.FileTag,
                    Prompt = sheet.Prompt,
                    QueueType = BatchQueueItemType.ReviewSheet,
                    Status = BatchTaskStatus.Pending,
                    Progress = 0,
                    StatusMessage = "待处理"
                });

                sheet.StatusMessage = "已加入队列";
            }

            UpdateBatchQueueStatusText();
        }

        private bool CanGenerateReviewSummary()
        {
            var hasCues = _fileLibrary.SubtitleCues.Count > 0;
            var selectedAudio = _fileLibrary.SelectedAudioFile;
            var allowSpeechGeneration = ShouldGenerateSpeechSubtitleForReview
                && selectedAudio != null
                && !IsSpeechSubtitleGenerating
                && (FileLibraryViewModel.HasSpeechSubtitle(selectedAudio.FullPath) || CanGenerateSpeechSubtitleFromStorage());

            return IsAiConfigured
                   && selectedAudio != null
                   && SelectedReviewSheet != null
                   && (hasCues || allowSpeechGeneration)
                   && !IsReviewSummaryLoading;
        }

        private async void GenerateReviewSummary()
        {
            if (SelectedReviewSheet == null)
            {
                return;
            }

            var selectedAudio = _fileLibrary.SelectedAudioFile;
            if (selectedAudio == null)
            {
                SelectedReviewSheet.StatusMessage = "未选择音频文件";
                return;
            }

            var sheet = SelectedReviewSheet;
            var audioFile = selectedAudio;
            if (!await EnsureSpeechSubtitleForReviewAsync(audioFile))
            {
                sheet.StatusMessage = string.IsNullOrWhiteSpace(SpeechSubtitleStatusMessage)
                    ? "speech 字幕未就绪，无法生成复盘"
                    : SpeechSubtitleStatusMessage;
                return;
            }

            var cues = _fileLibrary.SubtitleCues.ToList();
            await GenerateReviewSheetAsync(sheet, audioFile, cues);
        }

        private bool CanGenerateAllReviewSheets()
        {
            var hasCues = _fileLibrary.SubtitleCues.Count > 0;
            var selectedAudio = _fileLibrary.SelectedAudioFile;
            var allowSpeechGeneration = ShouldGenerateSpeechSubtitleForReview
                && selectedAudio != null
                && !IsSpeechSubtitleGenerating
                && (FileLibraryViewModel.HasSpeechSubtitle(selectedAudio.FullPath) || CanGenerateSpeechSubtitleFromStorage());

            return IsAiConfigured
                   && selectedAudio != null
                   && (hasCues || allowSpeechGeneration)
                   && _reviewSheets.Count > 0
                   && _reviewSheets.Any(sheet => !sheet.IsLoading);
        }

        private async void GenerateAllReviewSheets()
        {
            var selectedAudio = _fileLibrary.SelectedAudioFile;
            if (selectedAudio == null)
            {
                SelectedReviewSheet?.StatusMessage = "未选择音频文件";
                return;
            }

            if (!await EnsureSpeechSubtitleForReviewAsync(selectedAudio))
            {
                if (SelectedReviewSheet != null)
                {
                    SelectedReviewSheet.StatusMessage = string.IsNullOrWhiteSpace(SpeechSubtitleStatusMessage)
                        ? "speech 字幕未就绪，无法生成复盘"
                        : SpeechSubtitleStatusMessage;
                }
                return;
            }

            EnqueueReviewSheetsForAudio(selectedAudio, _reviewSheets);
            StartBatchQueueRunner("复盘已加入队列");
        }

        private async Task<bool> EnsureSpeechSubtitleForReviewAsync(MediaFileItem audioFile)
        {
            if (!ShouldGenerateSpeechSubtitleForReview)
            {
                return true;
            }

            var speechPath = FileLibraryViewModel.GetSpeechSubtitlePath(audioFile.FullPath);
            if (File.Exists(speechPath))
            {
                _fileLibrary.LoadSubtitleFilesForAudio(audioFile);
                var speechFile = _fileLibrary.SubtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, speechPath, StringComparison.OrdinalIgnoreCase));
                if (speechFile != null)
                {
                    _fileLibrary.SelectedSubtitleFile = speechFile;
                }
                return true;
            }

            if (!CanGenerateSpeechSubtitleFromStorage())
            {
                SpeechSubtitleStatusMessage = "缺少有效的存储账号或语音订阅，无法生成 speech 字幕";
                return false;
            }

            _speechSubtitleCts?.Cancel();
            _speechSubtitleCts = new CancellationTokenSource();
            var token = _speechSubtitleCts.Token;

            IsSpeechSubtitleGenerating = true;
            SpeechSubtitleStatusMessage = "speech 字幕生成中...";

            try
            {
                var success = await GenerateBatchSpeechSubtitleForFileAsync(
                    audioFile.FullPath,
                    token,
                    status => SpeechSubtitleStatusMessage = status);
                if (!success)
                {
                    SpeechSubtitleStatusMessage = "未识别到有效文本";
                    return false;
                }

                SpeechSubtitleStatusMessage = $"speech 字幕已生成: {Path.GetFileName(speechPath)}";
                _fileLibrary.LoadSubtitleFilesForAudio(audioFile);
                var speechFile = _fileLibrary.SubtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, speechPath, StringComparison.OrdinalIgnoreCase));
                if (speechFile != null)
                {
                    _fileLibrary.SelectedSubtitleFile = speechFile;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                SpeechSubtitleStatusMessage = "speech 字幕生成已取消";
                return false;
            }
            catch (Exception ex)
            {
                SpeechSubtitleStatusMessage = $"speech 字幕生成失败: {ex.Message}";
                return false;
            }
            finally
            {
                IsSpeechSubtitleGenerating = false;
                _speechSubtitleCts?.Dispose();
                _speechSubtitleCts = null;
            }
        }

        private async Task GenerateReviewSheetAsync(ReviewSheetState sheet, MediaFileItem audioFile, List<SubtitleCue> cues)
        {
            var config = _configProvider();
            var aiConfig = config.AiConfig;
            if (aiConfig == null || !aiConfig.IsValid)
            {
                sheet.StatusMessage = "AI 配置无效，请先配置 AI 服务";
                return;
            }

            if (audioFile == null || string.IsNullOrWhiteSpace(audioFile.FullPath))
            {
                sheet.StatusMessage = "未选择音频文件";
                return;
            }

            if (cues.Count == 0)
            {
                sheet.StatusMessage = "未加载字幕，无法生成总结";
                return;
            }

            sheet.Cts?.Cancel();
            var localCts = new CancellationTokenSource();
            sheet.Cts = localCts;
            var token = localCts.Token;

            sheet.IsLoading = true;
            sheet.Markdown = "";
            sheet.StatusMessage = "正在生成复盘内容...";
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();

            var systemPrompt = aiConfig.ReviewSystemPrompt;
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                systemPrompt = new AiConfig().ReviewSystemPrompt;
            }
            var subtitlesText = FormatSubtitleForSummary(cues);
            var prompt = string.IsNullOrWhiteSpace(sheet.Prompt)
                ? "请生成复盘总结。"
                : sheet.Prompt.Trim();
            var userTemplate = aiConfig.ReviewUserContentTemplate;
            if (string.IsNullOrWhiteSpace(userTemplate))
            {
                userTemplate = new AiConfig().ReviewUserContentTemplate;
            }
            var userPrompt = userTemplate
                .Replace("{subtitle}", subtitlesText)
                .Replace("{prompt}", prompt);

            try
            {
                var sb = new System.Text.StringBuilder();
                AiRequestOutcome? outcome = null;
                await _aiInsightService.StreamChatAsync(
                    aiConfig,
                    systemPrompt,
                    userPrompt,
                    chunk =>
                    {
                        if (!ReferenceEquals(sheet.Cts, localCts))
                        {
                            return;
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            sb.Append(chunk);
                            sheet.Markdown = TimeLinkHelper.InjectTimeLinks(sb.ToString());
                        });
                    },
                    token,
                    AiChatProfile.Summary,
                    enableReasoning: aiConfig.SummaryEnableReasoning,
                    onOutcome: o => outcome = o);

                if (!ReferenceEquals(sheet.Cts, localCts))
                {
                    return;
                }

                var summaryPath = GetReviewSheetPath(audioFile.FullPath, sheet.FileTag);
                File.WriteAllText(summaryPath, sheet.Markdown);
                var reasoningNote = "";
                if (outcome?.UsedFallback == true)
                {
                    reasoningNote = " (已降级为非思考)";
                }
                else if (outcome?.UsedReasoning == true)
                {
                    reasoningNote = " (思考已启用)";
                }
                else if (aiConfig.SummaryEnableReasoning)
                {
                    reasoningNote = " (未启用思考)";
                }

                sheet.StatusMessage = $"复盘内容已保存: {Path.GetFileName(summaryPath)}{reasoningNote}";
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(sheet.Cts, localCts))
                {
                    sheet.StatusMessage = "复盘内容已取消";
                }
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(sheet.Cts, localCts))
                {
                    sheet.StatusMessage = $"生成失败: {ex.Message}";
                }
            }
            finally
            {
                if (ReferenceEquals(sheet.Cts, localCts))
                {
                    sheet.IsLoading = false;
                    sheet.Cts = null;
                }

                GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
            }
        }

        private void OnReviewMarkdownLink(object? param)
        {
            if (param is not string url || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (TimeLinkHelper.TryParseTimeUrl(url, out var time))
            {
                _playback.SeekToTime(time);
            }
        }

        private static string FormatSubtitleForSummary(List<SubtitleCue> cues)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var cue in cues)
            {
                var time = cue.Start.ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {cue.Text}");
            }
            return sb.ToString();
        }

        // ── Enqueue from context menu ──

        private bool CanEnqueueSubtitleAndReviewFromLibrary(MediaFileItem? audioFile)
        {
            var target = audioFile ?? _fileLibrary.SelectedAudioFile;
            var canExecute = target != null && !string.IsNullOrWhiteSpace(target.FullPath);
            {
                var reason = canExecute
                    ? "ok"
                    : (target == null ? "target-null" : "fullpath-empty");
                var selectedPath = _fileLibrary.SelectedAudioFile?.FullPath ?? "";
                var paramPath = audioFile?.FullPath ?? "";
                AppendBatchDebugLog(
                    "EnqueueSubtitleReview.CanExecute",
                    $"result={canExecute} reason={reason} selected={selectedPath} param={paramPath}");
            }

            return canExecute;
        }

        private void EnqueueSubtitleAndReviewFromLibrary(MediaFileItem? audioFile)
        {
            var target = audioFile ?? _fileLibrary.SelectedAudioFile;
            if (target == null || string.IsNullOrWhiteSpace(target.FullPath))
            {
                BatchStatusMessage = "未选择音频文件";
                return;
            }

            if (!File.Exists(target.FullPath))
            {
                BatchStatusMessage = "音频文件不存在";
                return;
            }

            var config = _configProvider();
            var batchSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && batchSheets.Count > 0;
            var enableSpeech = ShouldGenerateSpeechSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                BatchStatusMessage = "未启用 speech 字幕或复盘生成";
                return;
            }

            if (enableSpeech && !CanGenerateSpeechSubtitleFromStorage())
            {
                BatchStatusMessage = "speech 字幕需要有效的存储账号与语音订阅";
                return;
            }

            if (!enableSpeech)
            {
                var subtitlePath = FileLibraryViewModel.GetPreferredSubtitlePath(target.FullPath);
                var hasSubtitle = !string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath);
                if (!hasSubtitle)
                {
                    BatchStatusMessage = "缺少字幕";
                    return;
                }
            }

            var batchItem = _batchTasks.FirstOrDefault(item =>
                string.Equals(item.FullPath, target.FullPath, StringComparison.OrdinalIgnoreCase));
            if (batchItem == null)
            {
                batchItem = new BatchTaskItem
                {
                    FileName = target.Name,
                    FullPath = target.FullPath,
                    Status = BatchTaskStatus.Pending,
                    Progress = 0
                };
                _batchTasks.Add(batchItem);
            }

            var reviewSheets = _batchReviewSheetSnapshot.Count > 0
                ? _batchReviewSheetSnapshot
                : batchSheets.ToList();
            if (_batchReviewSheetSnapshot.Count == 0 && reviewSheets.Count > 0)
            {
                _batchReviewSheetSnapshot = reviewSheets.ToList();
            }

            EnsureBatchLogFile();

            PrepareAndEnqueueSingleItem(batchItem, reviewSheets, enableSpeech, enableReview,
                config.ContextMenuForceRegeneration);

            if (ShouldWriteBatchLogSuccess)
            {
                AppendBatchLog("QueueStart", batchItem.FileName, "Success", "右键队列启动");
            }

            UpdateBatchQueueStatusText();
            StartBatchQueueRunner("已加入队列");
        }

        // ── Speech subtitle generation ──

        private bool CanGenerateSpeechSubtitle()
        {
            if (IsSpeechSubtitleGenerating)
            {
                return false;
            }

            var selectedAudio = _fileLibrary.SelectedAudioFile;
            if (selectedAudio == null || string.IsNullOrWhiteSpace(selectedAudio.FullPath))
            {
                return false;
            }

            if (!File.Exists(selectedAudio.FullPath))
            {
                return false;
            }

            var config = _configProvider();
            var subscription = config.GetActiveSubscription();
            return subscription?.IsValid() == true && !string.IsNullOrWhiteSpace(config.SourceLanguage);
        }

        private async void GenerateSpeechSubtitle()
        {
            if (!CanGenerateSpeechSubtitle())
            {
                SpeechSubtitleStatusMessage = "订阅或音频不可用";
                return;
            }

            var audioFile = _fileLibrary.SelectedAudioFile;
            if (audioFile == null)
            {
                return;
            }

            _speechSubtitleCts?.Cancel();
            _speechSubtitleCts = new CancellationTokenSource();
            var token = _speechSubtitleCts.Token;

            IsSpeechSubtitleGenerating = true;
            SpeechSubtitleStatusMessage = "正在转写...";

            try
            {
                var cues = await TranscribeSpeechToCuesAsync(audioFile.FullPath, token);
                if (cues.Count == 0)
                {
                    SpeechSubtitleStatusMessage = "未识别到有效文本";
                    return;
                }

                var outputPath = FileLibraryViewModel.GetSpeechSubtitlePath(audioFile.FullPath);
                BlobStorageService.WriteVttFile(outputPath, cues);

                SpeechSubtitleStatusMessage = $"speech 字幕已生成: {Path.GetFileName(outputPath)}";
                _fileLibrary.LoadSubtitleFilesForAudio(audioFile);
            }
            catch (OperationCanceledException)
            {
                SpeechSubtitleStatusMessage = "转写已取消";
            }
            catch (Exception ex)
            {
                SpeechSubtitleStatusMessage = $"转写失败: {ex.Message}";
            }
            finally
            {
                IsSpeechSubtitleGenerating = false;
                _speechSubtitleCts?.Dispose();
                _speechSubtitleCts = null;
            }
        }

        private bool CanGenerateBatchSpeechSubtitle()
        {
            if (IsSpeechSubtitleGenerating)
            {
                return false;
            }

            var selectedAudio = _fileLibrary.SelectedAudioFile;
            if (selectedAudio == null || string.IsNullOrWhiteSpace(selectedAudio.FullPath))
            {
                return false;
            }

            if (!File.Exists(selectedAudio.FullPath))
            {
                return false;
            }

            if (!IsSpeechSubtitleOptionEnabled)
            {
                return false;
            }

            return CanGenerateSpeechSubtitleFromStorage();
        }

        private async void GenerateBatchSpeechSubtitle()
        {
            if (!CanGenerateBatchSpeechSubtitle())
            {
                SpeechSubtitleStatusMessage = "请先验证存储账号与语音订阅";
                return;
            }

            var audioFile = _fileLibrary.SelectedAudioFile;
            if (audioFile == null)
            {
                return;
            }

            _speechSubtitleCts?.Cancel();
            _speechSubtitleCts = new CancellationTokenSource();
            var token = _speechSubtitleCts.Token;

            IsSpeechSubtitleGenerating = true;
            try
            {
                var success = await GenerateBatchSpeechSubtitleForFileAsync(
                    audioFile.FullPath,
                    token,
                    status => SpeechSubtitleStatusMessage = status);

                if (!success)
                {
                    SpeechSubtitleStatusMessage = "未识别到有效文本";
                    return;
                }

                var outputPath = FileLibraryViewModel.GetSpeechSubtitlePath(audioFile.FullPath);
                SpeechSubtitleStatusMessage = $"speech 字幕已生成: {Path.GetFileName(outputPath)}";
                _fileLibrary.LoadSubtitleFilesForAudio(audioFile);
            }
            catch (OperationCanceledException)
            {
                SpeechSubtitleStatusMessage = "批量转写已取消";
            }
            catch (Exception ex)
            {
                SpeechSubtitleStatusMessage = $"批量转写失败: {ex.Message}";
            }
            finally
            {
                IsSpeechSubtitleGenerating = false;
                _speechSubtitleCts?.Dispose();
                _speechSubtitleCts = null;
            }
        }

        private void CancelSpeechSubtitle()
        {
            _speechSubtitleCts?.Cancel();
        }

        private Task<List<SubtitleCue>> TranscribeSpeechToCuesAsync(string audioPath, CancellationToken token)
        {
            var config = _configProvider();
            var subscription = config.GetActiveSubscription()
                ?? throw new InvalidOperationException("语音订阅未配置");
            return RealtimeSpeechTranscriber.TranscribeSpeechToCuesAsync(
                audioPath, subscription, config.SourceLanguage, token);
        }

        private async Task<bool> GenerateBatchSpeechSubtitleForFileAsync(
            string audioPath,
            CancellationToken token,
            Action<string>? onStatus)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return false;
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException("未找到音频文件", audioPath);
            }

            var config = _configProvider();
            var subscription = config.GetActiveSubscription();
            if (subscription == null || !subscription.IsValid())
            {
                throw new InvalidOperationException("语音订阅未配置");
            }

            if (!IsSpeechSubtitleOptionEnabled)
            {
                throw new InvalidOperationException("存储账号未验证");
            }

            var uploadPath = audioPath;
            string? tempUploadPath = null;
            var converted = false;

            try
            {
                uploadPath = AudioFormatConverter.PrepareBatchUploadAudioPath(
                    audioPath,
                    onStatus,
                    token,
                    out tempUploadPath,
                    out converted);

                var audioFileName = Path.GetFileName(audioPath);
                if (converted)
                {
                    if (ShouldWriteBatchLogSuccess)
                    {
                        AppendBatchLog("AudioConvert", audioFileName, "Success",
                            $"pcm16k16bitmono temp={Path.GetFileName(uploadPath)}");
                    }
                }
                else
                {
                    if (ShouldWriteBatchLogSuccess)
                    {
                        AppendBatchLog("AudioReuse", audioFileName, "Success",
                            $"pcm16k16bitmono file={Path.GetFileName(uploadPath)}");
                    }
                }

                onStatus?.Invoke("批量转写：上传音频...");

                var (audioContainer, outputContainer) = await BlobStorageService.GetBatchContainersAsync(
                    config.BatchStorageConnectionString,
                    config.BatchAudioContainerName,
                    config.BatchResultContainerName,
                    token);

                BlobClient uploadedBlob;
                try
                {
                    uploadedBlob = await BlobStorageService.UploadAudioToBlobAsync(
                        uploadPath,
                        audioContainer,
                        token);

                    if (ShouldWriteBatchLogSuccess)
                    {
                        var blobProps = await uploadedBlob.GetPropertiesAsync(cancellationToken: token);
                        var eTag = blobProps.Value.ETag.ToString();
                        var requestId = blobProps.GetRawResponse().Headers.TryGetValue("x-ms-request-id", out var rid)
                            ? rid
                            : "";
                        AppendBatchLog("BlobUpload", audioFileName, "Success",
                            $"container={audioContainer.Name} blob={uploadedBlob.Name} etag={eTag} requestId={requestId}");
                    }
                }
                catch (Exception ex)
                {
                    if (ShouldWriteBatchLogFailure)
                    {
                        AppendBatchLog("BlobUpload", audioFileName, "Failed",
                            $"container={audioContainer.Name} file={Path.GetFileName(uploadPath)} error={ex.Message}");
                    }
                    throw;
                }

                if (!string.IsNullOrWhiteSpace(tempUploadPath))
                {
                    try
                    {
                        File.Delete(tempUploadPath);
                    }
                    catch
                    {
                    }
                    tempUploadPath = null;
                }

                var contentUrl = BlobStorageService.CreateBlobReadSasUri(uploadedBlob, TimeSpan.FromHours(24));

                onStatus?.Invoke("批量转写：提交任务...");

                var splitOptions = GetBatchSubtitleSplitOptions();
                Action<string, string>? batchLog = ShouldWriteBatchLogSuccess
                    ? (evt, msg) => AppendBatchLog(evt, audioFileName, "Success", msg)
                    : null;
                var (cues, transcriptionJson) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                    contentUrl,
                    config.SourceLanguage,
                    subscription,
                    token,
                    status => onStatus?.Invoke(status),
                    splitOptions,
                    batchLog);

                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("TranscribeResult", audioFileName, "Success", $"cues={cues.Count}");
                }

                if (cues.Count == 0)
                {
                    return false;
                }

                var outputPath = FileLibraryViewModel.GetSpeechSubtitlePath(audioPath);
                BlobStorageService.WriteVttFile(outputPath, cues);

                var baseName = Path.GetFileNameWithoutExtension(audioPath);
                await BlobStorageService.UploadTextToBlobAsync(outputContainer, baseName + ".speech.vtt", File.ReadAllText(outputPath), "text/vtt", token);
                await BlobStorageService.UploadTextToBlobAsync(outputContainer, baseName + ".speech.json", transcriptionJson, "application/json", token);

                return true;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempUploadPath))
                {
                    try
                    {
                        File.Delete(tempUploadPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private BatchSubtitleSplitOptions GetBatchSubtitleSplitOptions()
        {
            var config = _configProvider();
            return new BatchSubtitleSplitOptions
            {
                EnableSentenceSplit = config.EnableBatchSubtitleSentenceSplit,
                SplitOnComma = config.BatchSubtitleSplitOnComma,
                MaxChars = Math.Clamp(config.BatchSubtitleMaxChars, 6, 80),
                MaxDurationSeconds = Math.Clamp(config.BatchSubtitleMaxDurationSeconds, 1, 15),
                PauseSplitMs = Math.Clamp(config.BatchSubtitlePauseSplitMs, 100, 2000)
            };
        }
    }
}
