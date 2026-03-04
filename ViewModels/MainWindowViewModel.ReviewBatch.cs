using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public partial class MainWindowViewModel
    {
        private const double SubtitleCueRowHeight = 56;

        public ObservableCollection<MediaFileItem> AudioFiles => _audioFiles;

        public ObservableCollection<MediaFileItem> SubtitleFiles => _subtitleFiles;

        public ObservableCollection<SubtitleCue> SubtitleCues => _subtitleCues;

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
                    if (StartBatchCommand is RelayCommand startCmd)
                    {
                        startCmd.RaiseCanExecuteChanged();
                    }

                    if (StopBatchCommand is RelayCommand stopCmd)
                    {
                        stopCmd.RaiseCanExecuteChanged();
                    }
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
                    if (GenerateSpeechSubtitleCommand is RelayCommand cmd)
                    {
                        cmd.RaiseCanExecuteChanged();
                    }
                    if (GenerateBatchSpeechSubtitleCommand is RelayCommand batchCmd)
                    {
                        batchCmd.RaiseCanExecuteChanged();
                    }
                    if (CancelSpeechSubtitleCommand is RelayCommand cancelCmd)
                    {
                        cancelCmd.RaiseCanExecuteChanged();
                    }
                    if (GenerateReviewSummaryCommand is RelayCommand genCmd)
                    {
                        genCmd.RaiseCanExecuteChanged();
                    }
                    if (GenerateAllReviewSheetsCommand is RelayCommand allCmd)
                    {
                        allCmd.RaiseCanExecuteChanged();
                    }
                }
            }
        }

        public string SpeechSubtitleStatusMessage
        {
            get => _speechSubtitleStatusMessage;
            private set => SetProperty(ref _speechSubtitleStatusMessage, value);
        }

        public bool IsSpeechSubtitleOptionEnabled => _config.BatchStorageIsValid
            && !string.IsNullOrWhiteSpace(_config.BatchStorageConnectionString);

        public bool UseSpeechSubtitleForReview
        {
            get => _config.UseSpeechSubtitleForReview;
            set
            {
                if (_config.UseSpeechSubtitleForReview == value)
                {
                    return;
                }

                _config.UseSpeechSubtitleForReview = value;
                OnPropertyChanged(nameof(UseSpeechSubtitleForReview));
                OnPropertyChanged(nameof(BatchStartButtonText));
                if (GenerateReviewSummaryCommand is RelayCommand genCmd)
                {
                    genCmd.RaiseCanExecuteChanged();
                }
                if (GenerateAllReviewSheetsCommand is RelayCommand allCmd)
                {
                    allCmd.RaiseCanExecuteChanged();
                }
                if (StartBatchCommand is RelayCommand startCmd)
                {
                    startCmd.RaiseCanExecuteChanged();
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _configService.SaveConfigAsync(_config);
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

        public double SubtitleListHeight
        {
            get => _subtitleListHeight;
            private set => SetProperty(ref _subtitleListHeight, value);
        }

        public MediaFileItem? SelectedAudioFile
        {
            get => _selectedAudioFile;
            set
            {
                if (!SetProperty(ref _selectedAudioFile, value))
                {
                    return;
                }

                CancelAllReviewSheetGeneration();
                LoadSubtitleFilesForAudio(value);
                Playback.LoadAudioForPlayback(value);
                LoadReviewSheetForAudio(value, SelectedReviewSheet);
                ((RelayCommand)GenerateReviewSummaryCommand).RaiseCanExecuteChanged();
                ((RelayCommand)GenerateAllReviewSheetsCommand).RaiseCanExecuteChanged();
                if (GenerateSpeechSubtitleCommand is RelayCommand speechCmd)
                {
                    speechCmd.RaiseCanExecuteChanged();
                }
                if (GenerateBatchSpeechSubtitleCommand is RelayCommand batchCmd)
                {
                    batchCmd.RaiseCanExecuteChanged();
                }
            }
        }

        public MediaFileItem? SelectedSubtitleFile
        {
            get => _selectedSubtitleFile;
            set
            {
                if (!SetProperty(ref _selectedSubtitleFile, value))
                {
                    return;
                }

                LoadSubtitleCues(value);
            }
        }

        public SubtitleCue? SelectedSubtitleCue
        {
            get => _selectedSubtitleCue;
            set
            {
                if (!SetProperty(ref _selectedSubtitleCue, value))
                {
                    return;
                }

                if (Playback.SuppressSubtitleSeek)
                {
                    return;
                }

                if (value != null)
                {
                    Playback.SeekToTime(value.Start);
                }
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
                ConfigVM.NotifyReviewLampChanged();

                if (_selectedReviewSheet != null)
                {
                    _selectedReviewSheet.PropertyChanged += OnSelectedReviewSheetPropertyChanged;
                }

                LoadReviewSheetForAudio(SelectedAudioFile, _selectedReviewSheet);
            }
        }

        public string ReviewSummaryMarkdown => SelectedReviewSheet?.Markdown ?? "";

        public string ReviewSummaryStatusMessage => SelectedReviewSheet?.StatusMessage ?? "";

        public bool IsReviewSummaryLoading => SelectedReviewSheet?.IsLoading ?? false;

        public bool IsReviewSummaryEmpty => string.IsNullOrWhiteSpace(ReviewSummaryMarkdown) && !IsReviewSummaryLoading;

        private void RefreshAudioLibrary()
        {
            _audioFiles.Clear();
            _subtitleFiles.Clear();
            _subtitleCues.Clear();
            foreach (var sheet in _reviewSheets)
            {
                sheet.Markdown = "";
                sheet.StatusMessage = "";
            }

            var sessionsPath = PathManager.Instance.SessionsPath;
            if (!Directory.Exists(sessionsPath))
            {
                return;
            }

            var files = Directory.GetFiles(sessionsPath, "*.mp3")
                .Concat(Directory.GetFiles(sessionsPath, "*.wav"))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path));

            foreach (var file in files)
            {
                _audioFiles.Add(new MediaFileItem
                {
                    Name = Path.GetFileName(file),
                    FullPath = file
                });
            }

            if (_selectedAudioFile != null && !_audioFiles.Any(item => item.FullPath == _selectedAudioFile.FullPath))
            {
                SelectedAudioFile = null;
            }
        }

        private void LoadBatchTasksFromLibrary()
        {
            if (_audioFiles.Count == 0)
            {
                RefreshAudioLibrary();
            }

            var batchSheets = GetBatchReviewSheets();
            _batchTasks.Clear();
            foreach (var audio in _audioFiles)
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
                var hasSpeechSubtitle = HasSpeechSubtitle(audio.FullPath);
                var subtitlePath = requireSpeech
                    ? (hasSpeechSubtitle ? GetSpeechSubtitlePath(audio.FullPath) : "")
                    : GetPreferredSubtitlePath(audio.FullPath);
                var hasSubtitle = requireSpeech
                    ? hasSpeechSubtitle
                    : !string.IsNullOrWhiteSpace(subtitlePath);
                var hasAiSubtitle = HasAiSubtitle(audio.FullPath);
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

            StatusMessage = _batchTasks.Count == 0
                ? "未找到可批处理的音频文件"
                : $"已载入 {_batchTasks.Count} 条批处理任务";
            BatchStatusMessage = "";
        }

        private List<ReviewSheetPreset> GetBatchReviewSheets()
        {
            var sheets = _config.AiConfig?.ReviewSheets
                ?.Where(s => s.IncludeInBatch)
                .ToList();

            return sheets ?? new List<ReviewSheetPreset>();
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

        private void NormalizeSpeechSubtitleOption()
        {
            if (!IsSpeechSubtitleOptionEnabled && _config.UseSpeechSubtitleForReview)
            {
                _config.UseSpeechSubtitleForReview = false;
            }
        }

        private bool ShouldGenerateSpeechSubtitleForReview => IsSpeechSubtitleOptionEnabled
            && _config.UseSpeechSubtitleForReview;

        private bool CanGenerateSpeechSubtitleFromStorage()
        {
            if (!IsSpeechSubtitleOptionEnabled)
            {
                return false;
            }

            var subscription = _config.GetActiveSubscription();
            return subscription?.IsValid() == true && !string.IsNullOrWhiteSpace(_config.SourceLanguage);
        }

        private void EnqueueReviewSheetsForAudio(MediaFileItem audioFile, IEnumerable<ReviewSheetState> sheets)
        {
            var requireSpeech = ShouldGenerateSpeechSubtitleForReview;
            var subtitlePath = requireSpeech
                ? GetSpeechSubtitlePath(audioFile.FullPath)
                : GetPreferredSubtitlePath(audioFile.FullPath);
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
                            var next = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
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

                            var parent = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                BatchTasks.FirstOrDefault(x => x.FullPath == next.FullPath));

                            running.Add(Task.Run(() =>
                                ProcessBatchQueueItem(next, parent, token, cueCache, cueLock), token));
                        }

                        if (running.Count == 0)
                        {
                            break;
                        }

                        // 关键修复：运行中可能会有新任务入队（例如右键连续加入队列）。
                        // 旧逻辑只等待“已有运行任务完成”才进入下一轮，导致新入队任务长时间停留在待处理。
                        // 新逻辑：任务完成或短周期唤醒（300ms）任一发生即继续下一轮，及时按并发上限补位。
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
                    // ignore cancellation
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

        private void ClearBatchTasks()
        {
            _batchTasks.Clear();
            StatusMessage = "批处理任务已清空";
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

            var needsSpeech = enableSpeech && BatchTasks.Any(task => !HasSpeechSubtitle(task.FullPath));
            if (needsSpeech && !CanGenerateSpeechSubtitleFromStorage())
            {
                return false;
            }

            return true;
        }

        private bool ShouldWriteBatchLogSuccess => AppLogService.Instance.ShouldLogSuccess;

        private bool ShouldWriteBatchLogFailure => AppLogService.Instance.ShouldLogFailure;

        private void EnsureBatchLogFile() => AppLogService.Instance.EnsureBatchFile();

        private void AppendBatchLog(string eventName, string fileName, string status, string message)
            => AppLogService.Instance.LogBatch(eventName, fileName, status, message);

        private void AppendBatchDebugLog(string eventName, string message, bool isSuccess = true)
            => AppLogService.Instance.LogAudit(eventName, message, isSuccess);

        public void AuditUiEvent(string eventName, string message, bool isSuccess = true)
            => AppLogService.Instance.LogAudit(eventName, message, isSuccess);

        public void EnqueueSubtitleAndReviewFromLibraryUi(MediaFileItem? audioFile)
        {
            EnqueueSubtitleAndReviewFromLibrary(audioFile);
        }

        private static string FormatBatchExceptionForLog(Exception ex)
            => AppLogService.FormatException(ex);

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

        private void StartBatchProcessing()
        {
            if (BatchTasks.Count == 0)
            {
                BatchStatusMessage = "没有可处理的任务";
                return;
            }

            var batchSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && batchSheets.Count > 0;
            var enableSpeech = ShouldGenerateSpeechSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                BatchStatusMessage = "未启用 speech 字幕或复盘生成";
                return;
            }

            var needsSpeech = enableSpeech && BatchTasks.Any(task => !HasSpeechSubtitle(task.FullPath));
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
                    _config.BatchForceRegeneration);
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

        /// <summary>
        /// 单个 BatchTaskItem 的决策与入队逻辑（共享核心）。
        /// 批处理按钮和右键菜单共同调用此方法。
        /// </summary>
        /// <param name="forceRegeneration">true: 强制重新生成字幕和复盘（忽略已有文件）</param>
        /// <returns>添加到队列的项数</returns>
        private int PrepareAndEnqueueSingleItem(
            BatchTaskItem batchItem,
            List<ReviewSheetPreset> reviewSheets,
            bool enableSpeech,
            bool enableReview,
            bool forceRegeneration)
        {
            var speechExists = HasSpeechSubtitle(batchItem.FullPath);
            batchItem.HasAiSubtitle = enableSpeech ? speechExists : HasAiSubtitle(batchItem.FullPath);

            // ── 需要生成 speech 字幕 ──
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

            // ── 字幕已有 / 未启用，且无需复盘 ──
            if (!enableReview)
            {
                batchItem.ReviewTotal = 0;
                batchItem.ReviewCompleted = 0;
                batchItem.ReviewFailed = 0;
                batchItem.ReviewPending = 0;
                batchItem.ReviewStatusText = "复盘:未启用";
                batchItem.HasAiSubtitle = speechExists || HasAiSubtitle(batchItem.FullPath);
                UpdateBatchItem(batchItem, BatchTaskStatus.Completed, 1, "字幕已存在");
                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("SpeechSkip", batchItem.FileName, "Success", "speech 字幕已存在");
                }
                return 0;
            }

            // ── 未启用 speech，检查是否有字幕可供复盘 ──
            if (!enableSpeech)
            {
                var subtitlePath = GetPreferredSubtitlePath(batchItem.FullPath);
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

            // ── 入队复盘项 ──
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

            var systemPrompt = _config.AiConfig?.ReviewSystemPrompt;
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                systemPrompt = new AiConfig().ReviewSystemPrompt;
            }
            var prompt = string.IsNullOrWhiteSpace(queueItem.Prompt)
                ? "请生成复盘总结。"
                : queueItem.Prompt.Trim();
            var userTemplate = _config.AiConfig?.ReviewUserContentTemplate;
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
                    _config.AiConfig!,
                    systemPrompt,
                    userPrompt,
                    chunk =>
                    {
                        sb.Append(chunk);
                    },
                    localToken,
                    AiChatProfile.Summary,
                    enableReasoning: _config.AiConfig!.SummaryEnableReasoning,
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
                else if (_config.AiConfig!.SummaryEnableReasoning)
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

                var added = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
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
                ? GetSpeechSubtitlePath(audioPath)
                : GetPreferredSubtitlePath(audioPath);
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _batchQueueItems.Remove(item);
            });
        }

        private void UpdateBatchItem(BatchTaskItem item, BatchTaskStatus status, double progress, string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                item.Status = status;
                item.Progress = progress;
                item.StatusMessage = message;
            });
        }

        private static string? GetPreferredSubtitlePath(string audioFilePath)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);

            var candidates = new[]
            {
                Path.Combine(directory, baseName + ".speech.vtt"),
                Path.Combine(directory, baseName + ".ai.srt"),
                Path.Combine(directory, baseName + ".ai.vtt"),
                Path.Combine(directory, baseName + ".srt"),
                Path.Combine(directory, baseName + ".vtt")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static bool HasAiSubtitle(string audioFilePath)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            var speechVtt = Path.Combine(directory, baseName + ".speech.vtt");
            var aiSrt = Path.Combine(directory, baseName + ".ai.srt");
            var aiVtt = Path.Combine(directory, baseName + ".ai.vtt");
            return File.Exists(speechVtt) || File.Exists(aiSrt) || File.Exists(aiVtt);
        }

        private static bool HasSpeechSubtitle(string audioFilePath)
        {
            var speechPath = GetSpeechSubtitlePath(audioFilePath);
            return File.Exists(speechPath);
        }

        private void RebuildReviewSheets()
        {
            var currentTag = SelectedReviewSheet?.FileTag;
            _reviewSheets.Clear();

            var presets = _config.AiConfig?.ReviewSheets;
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

        private ReviewSheetState? GetPrimaryReviewSheet()
        {
            return _reviewSheets.FirstOrDefault(s => string.Equals(s.FileTag, "summary", StringComparison.OrdinalIgnoreCase))
                   ?? _reviewSheets.FirstOrDefault();
        }

        private void OnSelectedReviewSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ReviewSummaryMarkdown));
            OnPropertyChanged(nameof(ReviewSummaryStatusMessage));
            OnPropertyChanged(nameof(IsReviewSummaryLoading));
            OnPropertyChanged(nameof(IsReviewSummaryEmpty));
            OnPropertyChanged(nameof(ReviewSummaryLampFill));
            OnPropertyChanged(nameof(ReviewSummaryLampStroke));
            ConfigVM.NotifyReviewLampChanged();
            if (GenerateReviewSummaryCommand is RelayCommand genCmd)
            {
                genCmd.RaiseCanExecuteChanged();
            }
            if (GenerateAllReviewSheetsCommand is RelayCommand allCmd)
            {
                allCmd.RaiseCanExecuteChanged();
            }
        }

        private void LoadSubtitleFilesForAudio(MediaFileItem? audioFile)
        {
            _subtitleFiles.Clear();
            _subtitleCues.Clear();
            SelectedSubtitleFile = null;

            if (audioFile == null || string.IsNullOrWhiteSpace(audioFile.FullPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(audioFile.FullPath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFile.FullPath);
            var candidateBases = new[] { baseName };

            foreach (var candidate in candidateBases)
            {
                var speechVtt = Path.Combine(directory, candidate + ".speech.vtt");
                if (File.Exists(speechVtt))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(speechVtt),
                        FullPath = speechVtt
                    });
                }

                var srtPath = Path.Combine(directory, candidate + ".srt");
                if (File.Exists(srtPath))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(srtPath),
                        FullPath = srtPath
                    });
                }

                var vttPath = Path.Combine(directory, candidate + ".vtt");
                if (File.Exists(vttPath))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(vttPath),
                        FullPath = vttPath
                    });
                }
            }

            if (_subtitleFiles.Count > 0)
            {
                var speechPath = GetSpeechSubtitlePath(audioFile.FullPath);
                var speechFile = _subtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, speechPath, StringComparison.OrdinalIgnoreCase));
                SelectedSubtitleFile = ShouldGenerateSpeechSubtitleForReview && speechFile != null
                    ? speechFile
                    : _subtitleFiles[0];
            }
        }

        private void LoadSubtitleCues(MediaFileItem? subtitleFile)
        {
            _subtitleCues.Clear();
            SelectedSubtitleCue = null;

            if (subtitleFile == null || string.IsNullOrWhiteSpace(subtitleFile.FullPath))
            {
                return;
            }

            if (!File.Exists(subtitleFile.FullPath))
            {
                return;
            }

            var extension = Path.GetExtension(subtitleFile.FullPath).ToLowerInvariant();
            if (extension == ".srt")
            {
                ParseSrt(subtitleFile.FullPath);
            }
            else if (extension == ".vtt")
            {
                ParseVtt(subtitleFile.FullPath);
            }

            UpdateSubtitleListHeight();
            ((RelayCommand)GenerateReviewSummaryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GenerateAllReviewSheetsCommand).RaiseCanExecuteChanged();
        }

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

            ((RelayCommand)GenerateReviewSummaryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GenerateAllReviewSheetsCommand).RaiseCanExecuteChanged();
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

        private bool CanGenerateReviewSummary()
        {
            var hasCues = SubtitleCues.Count > 0;
            var allowSpeechGeneration = ShouldGenerateSpeechSubtitleForReview
                && SelectedAudioFile != null
                && !IsSpeechSubtitleGenerating
                && (HasSpeechSubtitle(SelectedAudioFile.FullPath) || CanGenerateSpeechSubtitleFromStorage());

            return IsAiConfigured
                   && SelectedAudioFile != null
                   && SelectedReviewSheet != null
                   && (hasCues || allowSpeechGeneration)
                   && !IsReviewSummaryLoading;
        }

        private async void GenerateReviewSummary()
        {
            if (SelectedReviewSheet == null)
            {
                SelectedReviewSheet?.StatusMessage = "未选择复盘模板";
                return;
            }

            if (SelectedAudioFile == null)
            {
                SelectedReviewSheet.StatusMessage = "未选择音频文件";
                return;
            }

            var sheet = SelectedReviewSheet;
            var audioFile = SelectedAudioFile;
            if (!await EnsureSpeechSubtitleForReviewAsync(audioFile))
            {
                sheet.StatusMessage = string.IsNullOrWhiteSpace(SpeechSubtitleStatusMessage)
                    ? "speech 字幕未就绪，无法生成复盘"
                    : SpeechSubtitleStatusMessage;
                return;
            }

            var cues = SubtitleCues.ToList();
            await GenerateReviewSheetAsync(sheet, audioFile, cues);
        }

        private bool CanGenerateAllReviewSheets()
        {
            var hasCues = SubtitleCues.Count > 0;
            var allowSpeechGeneration = ShouldGenerateSpeechSubtitleForReview
                && SelectedAudioFile != null
                && !IsSpeechSubtitleGenerating
                && (HasSpeechSubtitle(SelectedAudioFile.FullPath) || CanGenerateSpeechSubtitleFromStorage());

            return IsAiConfigured
                   && SelectedAudioFile != null
                   && (hasCues || allowSpeechGeneration)
                   && _reviewSheets.Count > 0
                   && _reviewSheets.Any(sheet => !sheet.IsLoading);
        }

        private async Task<bool> EnsureSpeechSubtitleForReviewAsync(MediaFileItem audioFile)
        {
            if (!ShouldGenerateSpeechSubtitleForReview)
            {
                return true;
            }

            var speechPath = GetSpeechSubtitlePath(audioFile.FullPath);
            if (File.Exists(speechPath))
            {
                LoadSubtitleFilesForAudio(audioFile);
                var speechFile = _subtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, speechPath, StringComparison.OrdinalIgnoreCase));
                if (speechFile != null)
                {
                    SelectedSubtitleFile = speechFile;
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
                LoadSubtitleFilesForAudio(audioFile);
                var speechFile = _subtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, speechPath, StringComparison.OrdinalIgnoreCase));
                if (speechFile != null)
                {
                    SelectedSubtitleFile = speechFile;
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

        private async void GenerateAllReviewSheets()
        {
            if (SelectedAudioFile == null)
            {
                SelectedReviewSheet?.StatusMessage = "未选择音频文件";
                return;
            }

            if (!await EnsureSpeechSubtitleForReviewAsync(SelectedAudioFile))
            {
                if (SelectedReviewSheet != null)
                {
                    SelectedReviewSheet.StatusMessage = string.IsNullOrWhiteSpace(SpeechSubtitleStatusMessage)
                        ? "speech 字幕未就绪，无法生成复盘"
                        : SpeechSubtitleStatusMessage;
                }
                return;
            }

            EnqueueReviewSheetsForAudio(SelectedAudioFile, _reviewSheets);
            StartBatchQueueRunner("复盘已加入队列");
        }

        private bool CanEnqueueSubtitleAndReviewFromLibrary(MediaFileItem? audioFile)
        {
            var target = audioFile ?? SelectedAudioFile;
            var canExecute = target != null && !string.IsNullOrWhiteSpace(target.FullPath);
            {
                var reason = canExecute
                    ? "ok"
                    : (target == null ? "target-null" : "fullpath-empty");
                var selectedPath = SelectedAudioFile?.FullPath ?? "";
                var paramPath = audioFile?.FullPath ?? "";
                AppendBatchDebugLog(
                    "EnqueueSubtitleReview.CanExecute",
                    $"result={canExecute} reason={reason} selected={selectedPath} param={paramPath}");
            }

            return canExecute;
        }

        private void EnqueueSubtitleAndReviewFromLibrary(MediaFileItem? audioFile)
        {
            var target = audioFile ?? SelectedAudioFile;
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
                var subtitlePath = GetPreferredSubtitlePath(target.FullPath);
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
                _config.ContextMenuForceRegeneration);

            if (ShouldWriteBatchLogSuccess)
            {
                AppendBatchLog("QueueStart", batchItem.FileName, "Success", "右键队列启动");
            }

            UpdateBatchQueueStatusText();
            StartBatchQueueRunner("已加入队列");
        }

        private bool CanGenerateSpeechSubtitle()
        {
            if (IsSpeechSubtitleGenerating)
            {
                return false;
            }

            if (SelectedAudioFile == null || string.IsNullOrWhiteSpace(SelectedAudioFile.FullPath))
            {
                return false;
            }

            if (!File.Exists(SelectedAudioFile.FullPath))
            {
                return false;
            }

            var subscription = _config.GetActiveSubscription();
            return subscription?.IsValid() == true && !string.IsNullOrWhiteSpace(_config.SourceLanguage);
        }

        private async void GenerateSpeechSubtitle()
        {
            if (!CanGenerateSpeechSubtitle())
            {
                SpeechSubtitleStatusMessage = "订阅或音频不可用";
                return;
            }

            var audioFile = SelectedAudioFile;
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

                var outputPath = GetSpeechSubtitlePath(audioFile.FullPath);
                BlobStorageService.WriteVttFile(outputPath, cues);

                SpeechSubtitleStatusMessage = $"speech 字幕已生成: {Path.GetFileName(outputPath)}";
                LoadSubtitleFilesForAudio(audioFile);
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

            if (SelectedAudioFile == null || string.IsNullOrWhiteSpace(SelectedAudioFile.FullPath))
            {
                return false;
            }

            if (!File.Exists(SelectedAudioFile.FullPath))
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

            var audioFile = SelectedAudioFile;
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

                var outputPath = GetSpeechSubtitlePath(audioFile.FullPath);
                SpeechSubtitleStatusMessage = $"speech 字幕已生成: {Path.GetFileName(outputPath)}";
                LoadSubtitleFilesForAudio(audioFile);
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

            var subscription = _config.GetActiveSubscription();
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
                    _config.BatchStorageConnectionString,
                    _config.BatchAudioContainerName,
                    _config.BatchResultContainerName,
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
                        // ignore temp cleanup failures
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
                    _config.SourceLanguage,
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

                var outputPath = GetSpeechSubtitlePath(audioPath);
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
                        // ignore temp cleanup failures
                    }
                }
            }
        }

        private BatchSubtitleSplitOptions GetBatchSubtitleSplitOptions()
        {
            return new BatchSubtitleSplitOptions
            {
                EnableSentenceSplit = _config.EnableBatchSubtitleSentenceSplit,
                SplitOnComma = _config.BatchSubtitleSplitOnComma,
                MaxChars = Math.Clamp(_config.BatchSubtitleMaxChars, 6, 80),
                MaxDurationSeconds = Math.Clamp(_config.BatchSubtitleMaxDurationSeconds, 1, 15),
                PauseSplitMs = Math.Clamp(_config.BatchSubtitlePauseSplitMs, 100, 2000)
            };
        }

        private void CancelSpeechSubtitle()
        {
            _speechSubtitleCts?.Cancel();
        }

        private static string GetSpeechSubtitlePath(string audioFilePath)
            => RealtimeSpeechTranscriber.GetSpeechSubtitlePath(audioFilePath);

        private Task<List<SubtitleCue>> TranscribeSpeechToCuesAsync(string audioPath, CancellationToken token)
        {
            var subscription = _config.GetActiveSubscription()
                ?? throw new InvalidOperationException("语音订阅未配置");
            return RealtimeSpeechTranscriber.TranscribeSpeechToCuesAsync(
                audioPath, subscription, _config.SourceLanguage, token);
        }

        private async Task GenerateReviewSheetAsync(ReviewSheetState sheet, MediaFileItem audioFile, List<SubtitleCue> cues)
        {
            var aiConfig = _config.AiConfig;
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
            if (GenerateAllReviewSheetsCommand is RelayCommand allStartCmd)
            {
                allStartCmd.RaiseCanExecuteChanged();
            }

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

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

                if (GenerateAllReviewSheetsCommand is RelayCommand allCmd)
                {
                    allCmd.RaiseCanExecuteChanged();
                }
            }
        }

        private string FormatSubtitleForSummary()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var cue in SubtitleCues)
            {
                var time = cue.Start.ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {cue.Text}");
            }
            return sb.ToString();
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

        private void OnReviewMarkdownLink(object? param)
        {
            if (param is not string url || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (TimeLinkHelper.TryParseTimeUrl(url, out var time))
            {
                Playback.SeekToTime(time);
            }
        }

        private void ParseSrt(string path)
        {
            var lines = File.ReadAllLines(path);
            ParseSubtitleLines(lines, expectsHeader: false);
        }

        private void ParseVtt(string path)
        {
            var lines = File.ReadAllLines(path);
            ParseSubtitleLines(lines, expectsHeader: true);
        }

        private void ParseSubtitleLines(string[] lines, bool expectsHeader)
        {
            var index = 0;
            if (expectsHeader && index < lines.Length && lines[index].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }

            while (index < lines.Length)
            {
                var line = lines[index].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    index++;
                    continue;
                }

                if (int.TryParse(line, out _))
                {
                    index++;
                    if (index >= lines.Length)
                    {
                        break;
                    }
                    line = lines[index].Trim();
                }

                if (!SubtitleFileParser.TryParseTimeRange(line, out var start, out var end))
                {
                    index++;
                    continue;
                }

                index++;
                var textLines = new List<string>();
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    textLines.Add(lines[index].Trim());
                    index++;
                }

                var text = string.Join(" ", textLines).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _subtitleCues.Add(new SubtitleCue
                    {
                        Start = start,
                        End = end,
                        Text = text
                    });
                }
            }

            UpdateSubtitleListHeight();
        }

        private void UpdateSubtitleListHeight()
        {
            var visible = Math.Min(_subtitleCues.Count, 6);
            SubtitleListHeight = visible * SubtitleCueRowHeight;
        }
    }
}
