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
        private readonly ObservableCollection<BatchBucketNavItem> _packageBuckets = new();
        private readonly ObservableCollection<BatchPackageItem> _pendingPackages = new();
        private readonly ObservableCollection<BatchPackageItem> _runningPackages = new();
        private readonly ObservableCollection<BatchPackageItem> _failedPackages = new();
        private readonly ObservableCollection<BatchPackageItem> _completedPackages = new();
        private readonly ObservableCollection<BatchPackageItem> _removedPackages = new();
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
        private BatchPackageItem? _selectedPackage;
        private BatchBucketNavItem? _selectedPackageBucket;

        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly Action<string> _statusSetter;
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly IAiAudioTranscriptionService _aiAudioTranscriptionService;
        private readonly AiInsightService _aiInsightService;
        private readonly FileLibraryViewModel _fileLibrary;
        private readonly PlaybackViewModel _playback;
        private readonly ConfigurationService _configService;
        private readonly IBatchPackageStateService _batchPackageStateService;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;
        private readonly Action _notifyReviewLampChanged;
        private bool _isPackageProjectionRefreshPending;
        private bool _hasInitializedBatchCenter;

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
        public RelayCommand RemovePackageCommand { get; }
        public RelayCommand RestorePackageCommand { get; }
        public RelayCommand PausePackageCommand { get; }
        public RelayCommand ResumePackageCommand { get; }
        public RelayCommand EnqueuePackageCommand { get; }
        public RelayCommand RegeneratePackageCommand { get; }
        public RelayCommand RegenerateSubtaskCommand { get; }
        public RelayCommand StartPackageCommand { get; }
        public RelayCommand TogglePackageExpandedCommand { get; }

        private bool IsAiConfigured => TryBuildReviewRuntimeConfig(_configProvider(), out _, out _, out _);

        public BatchProcessingViewModel(
            Func<AzureSpeechConfig> configProvider,
            Action<string> statusSetter,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            AiInsightService aiInsightService,
            FileLibraryViewModel fileLibrary,
            PlaybackViewModel playback,
            ConfigurationService configService,
            IBatchPackageStateService batchPackageStateService,
            IAzureTokenProviderStore azureTokenProviderStore,
            Action notifyReviewLampChanged)
        {
            _configProvider = configProvider;
            _statusSetter = statusSetter;
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _aiAudioTranscriptionService = aiAudioTranscriptionService;
            _aiInsightService = aiInsightService;
            _fileLibrary = fileLibrary;
            _playback = playback;
            _configService = configService;
            _batchPackageStateService = batchPackageStateService;
            _azureTokenProviderStore = azureTokenProviderStore;
            _notifyReviewLampChanged = notifyReviewLampChanged;

            InitializePackageBuckets();

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
            _batchQueueItems.CollectionChanged += (_, _) =>
            {
                UpdateBatchQueueStatusText();
                RequestPackageProjectionRefresh();
            };

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

            RemovePackageCommand = new RelayCommand(
                execute: param => RemovePackage(param as BatchPackageItem),
                canExecute: param => CanRemovePackage(param as BatchPackageItem));

            RestorePackageCommand = new RelayCommand(
                execute: param => RestorePackage(param as BatchPackageItem),
                canExecute: param => CanRestorePackage(param as BatchPackageItem));

            PausePackageCommand = new RelayCommand(
                execute: param => PausePackage(param as BatchPackageItem),
                canExecute: param => CanPausePackage(param as BatchPackageItem));

            ResumePackageCommand = new RelayCommand(
                execute: param => ResumePackage(param as BatchPackageItem),
                canExecute: param => CanResumePackage(param as BatchPackageItem));

            EnqueuePackageCommand = new RelayCommand(
                execute: param => EnqueuePackage(param as BatchPackageItem),
                canExecute: param => CanEnqueuePackage(param as BatchPackageItem));

            StartPackageCommand = new RelayCommand(
                execute: param => StartPackage(param as BatchPackageItem),
                canExecute: param => CanStartPackage(param as BatchPackageItem));

            RegeneratePackageCommand = new RelayCommand(
                execute: param => RegeneratePackage(param as BatchPackageItem),
                canExecute: param => CanRegeneratePackage(param as BatchPackageItem));

            RegenerateSubtaskCommand = new RelayCommand(
                execute: param => RegenerateSubtask(param as BatchSubtaskItem),
                canExecute: param => CanRegenerateSubtask(param as BatchSubtaskItem));

            TogglePackageExpandedCommand = new RelayCommand(
                execute: param => TogglePackageExpanded(param as BatchPackageItem),
                canExecute: param => param is BatchPackageItem);
        }

        // ── Properties ──

        public ObservableCollection<BatchTaskItem> BatchTasks => _batchTasks;
        public ObservableCollection<BatchQueueItem> BatchQueueItems => _batchQueueItems;
        public ObservableCollection<BatchBucketNavItem> PackageBuckets => _packageBuckets;
        public ObservableCollection<BatchPackageItem> PendingPackages => _pendingPackages;
        public ObservableCollection<BatchPackageItem> RunningPackages => _runningPackages;
        public ObservableCollection<BatchPackageItem> FailedPackages => _failedPackages;
        public ObservableCollection<BatchPackageItem> CompletedPackages => _completedPackages;
        public ObservableCollection<BatchPackageItem> RemovedPackages => _removedPackages;
        public string CurrentBucketTitle => SelectedPackageBucket?.Title ?? "待处理";
        public bool IsCurrentBucketPending => string.Equals(SelectedPackageBucket?.Key, "pending", StringComparison.OrdinalIgnoreCase);
        public bool IsCurrentBucketRunning => string.Equals(SelectedPackageBucket?.Key, "running", StringComparison.OrdinalIgnoreCase);
        public bool IsCurrentBucketCompleted => string.Equals(SelectedPackageBucket?.Key, "completed", StringComparison.OrdinalIgnoreCase);
        public bool IsCurrentBucketFailed => string.Equals(SelectedPackageBucket?.Key, "failed", StringComparison.OrdinalIgnoreCase);
        public bool IsCurrentBucketRemoved => string.Equals(SelectedPackageBucket?.Key, "removed", StringComparison.OrdinalIgnoreCase);

        public BatchBucketNavItem? SelectedPackageBucket
        {
            get => _selectedPackageBucket;
            set
            {
                if (SetProperty(ref _selectedPackageBucket, value))
                {
                    OnPropertyChanged(nameof(CurrentBucketPackages));
                    OnPropertyChanged(nameof(CurrentBucketTitle));
                    OnPropertyChanged(nameof(IsCurrentBucketPending));
                    OnPropertyChanged(nameof(IsCurrentBucketRunning));
                    OnPropertyChanged(nameof(IsCurrentBucketCompleted));
                    OnPropertyChanged(nameof(IsCurrentBucketFailed));
                    OnPropertyChanged(nameof(IsCurrentBucketRemoved));
                    if (value == null)
                    {
                        SelectedPackage = null;
                        return;
                    }

                    var current = CurrentBucketPackages;
                    var selectedPath = SelectedPackage?.FullPath;
                    SelectedPackage = string.IsNullOrWhiteSpace(selectedPath)
                        ? null
                        : current.FirstOrDefault(package =>
                            string.Equals(package.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        public ObservableCollection<BatchPackageItem> CurrentBucketPackages
            => GetBucketCollection(SelectedPackageBucket?.Key);

        public BatchPackageItem? SelectedPackage
        {
            get => _selectedPackage;
            set
            {
                if (SetProperty(ref _selectedPackage, value))
                {
                    RemovePackageCommand.RaiseCanExecuteChanged();
                    RestorePackageCommand.RaiseCanExecuteChanged();
                    PausePackageCommand.RaiseCanExecuteChanged();
                    ResumePackageCommand.RaiseCanExecuteChanged();
                    EnqueuePackageCommand.RaiseCanExecuteChanged();
                    StartPackageCommand.RaiseCanExecuteChanged();
                    RegeneratePackageCommand.RaiseCanExecuteChanged();
                }
            }
        }

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
            set
            {
                if (SetProperty(ref _batchStatusMessage, value))
                {
                    OnPropertyChanged(nameof(ReviewPageFooterStatusText));
                }
            }
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
            private set
            {
                if (SetProperty(ref _speechSubtitleStatusMessage, value))
                {
                    OnPropertyChanged(nameof(ReviewPageFooterStatusText));
                }
            }
        }

        public bool IsSpeechSubtitleOptionEnabled
        {
            get
            {
                var config = _configProvider();
                return HasAvailableConfiguredSubtitlePath(config);
            }
        }

        public bool UseSpeechSubtitleForReview
        {
            get => GetReviewSubtitleSourceMode() == ReviewSubtitleSourceMode.SpeechSubtitle;
            set
            {
                var config = _configProvider();
                var nextMode = value
                    ? ReviewSubtitleSourceMode.SpeechSubtitle
                    : ReviewSubtitleSourceMode.DefaultSubtitle;
                if (config.GetEffectiveReviewSubtitleSourceMode() == nextMode)
                {
                    return;
                }

                config.ReviewSubtitleSourceMode = nextMode;
                config.UseSpeechSubtitleForReview = nextMode == ReviewSubtitleSourceMode.SpeechSubtitle;
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
            ? BuildSubtitleSourceEnabledStatusText()
            : BuildSubtitleSourceUnavailableStatusText();

        public string GenerateSelectedSubtitleMenuText => GetReviewSubtitleSourceMode() switch
        {
            ReviewSubtitleSourceMode.SpeechSubtitle => "生成 Speech 字幕",
            ReviewSubtitleSourceMode.AiTranscriptionSubtitle => "生成 AI 转写字幕",
            _ => "生成字幕"
        };

        public string ReviewPageFooterStatusText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ReviewSummaryStatusMessage))
                {
                    return ReviewSummaryStatusMessage;
                }

                if (!string.IsNullOrWhiteSpace(SpeechSubtitleStatusMessage))
                {
                    return SpeechSubtitleStatusMessage;
                }

                if (!string.IsNullOrWhiteSpace(BatchStatusMessage))
                {
                    return BatchStatusMessage;
                }

                return SpeechSubtitleOptionStatusText;
            }
        }

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
                OnPropertyChanged(nameof(ReviewPageFooterStatusText));
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

            // 顺序策略：由 MainWindow 在“字幕加载完成”后调用本方法。
            // 本方法再以后台优先级投递复盘加载，避免与同一帧其它 UI 更新竞争。
            if (SelectedReviewSheet != null)
            {
                SelectedReviewSheet.StatusMessage = "字幕已就绪，正在加载复盘...";
                SelectedReviewSheet.IsLoading = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                var currentAudio = _fileLibrary.SelectedAudioFile;
                var isStale = !string.Equals(currentAudio?.FullPath, audioFile?.FullPath, StringComparison.OrdinalIgnoreCase);
                if (isStale)
                {
                    AppLogService.Instance.LogAudit("ReviewLoadSkipped", "stale audio selection", isSuccess: true);
                    return;
                }

                LoadReviewSheetForAudio(audioFile, SelectedReviewSheet);
            }, DispatcherPriority.Background);

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
            config.UseSpeechSubtitleForReview = config.GetEffectiveReviewSubtitleSourceMode() == ReviewSubtitleSourceMode.SpeechSubtitle;
        }

        public void RefreshCommandStates()
        {
            OnPropertyChanged(nameof(IsSpeechSubtitleOptionEnabled));
            OnPropertyChanged(nameof(UseSpeechSubtitleForReview));
            OnPropertyChanged(nameof(SpeechSubtitleOptionStatusText));
            OnPropertyChanged(nameof(GenerateSelectedSubtitleMenuText));
            OnPropertyChanged(nameof(ReviewPageFooterStatusText));
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

            if (!_hasInitializedBatchCenter)
            {
                return;
            }

            LoadBatchTasksFromLibrary();
        }

        public void EnsureBatchCenterInitialized()
        {
            if (_hasInitializedBatchCenter)
            {
                return;
            }

            _hasInitializedBatchCenter = true;
            LoadBatchTasksFromLibrary();
        }

        // ── Private helpers ──

        private bool ShouldGenerateSpeechSubtitleForReview
            => GetReviewSubtitleSourceMode() == ReviewSubtitleSourceMode.SpeechSubtitle;

        private bool ShouldGenerateAiSubtitleForReview
            => GetReviewSubtitleSourceMode() == ReviewSubtitleSourceMode.AiTranscriptionSubtitle;

        private bool ShouldGenerateGeneratedSubtitleForReview
            => GetReviewSubtitleSourceMode() != ReviewSubtitleSourceMode.DefaultSubtitle;

        private bool ShouldWriteBatchLogSuccess => AppLogService.Instance.ShouldLogSuccess;
        private bool ShouldWriteBatchLogFailure => AppLogService.Instance.ShouldLogFailure;
        private void EnsureBatchLogFile() => AppLogService.Instance.EnsureBatchFile();

        private void AppendBatchLog(string eventName, string fileName, string status, string message)
            => AppLogService.Instance.LogBatch(eventName, fileName, status, message);

        private void AppendBatchDebugLog(string eventName, string message, bool isSuccess = true)
            => AppLogService.Instance.LogAudit(eventName, message, isSuccess);

            private static string CreateDebugTraceId(string prefix)
                => $"{prefix}-{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";

            private static string FormatLogValue(string? value, int maxLength = 180)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "(empty)";
                }

                var normalized = value
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal);

                return normalized.Length <= maxLength
                    ? normalized
                    : normalized[..maxLength] + "...";
            }

            private static List<ReviewSheetPreset> CloneReviewSheets(IEnumerable<ReviewSheetPreset> reviewSheets)
            {
                return reviewSheets.Select(sheet => new ReviewSheetPreset
                {
                    Name = sheet.Name,
                    FileTag = sheet.FileTag,
                    Prompt = sheet.Prompt,
                    IncludeInBatch = sheet.IncludeInBatch
                }).ToList();
            }

            private void SetReviewSheetSnapshot(BatchTaskItem batchItem, IEnumerable<ReviewSheetPreset> reviewSheets)
            {
                batchItem.ReviewSheetSnapshot = CloneReviewSheets(reviewSheets);
            }

            private List<ReviewSheetPreset> GetReviewSheetsForTask(BatchTaskItem? batchItem)
            {
                if (batchItem?.ReviewSheetSnapshot.Count > 0)
                {
                    return CloneReviewSheets(batchItem.ReviewSheetSnapshot);
                }

                if (_batchReviewSheetSnapshot.Count > 0)
                {
                    return CloneReviewSheets(_batchReviewSheetSnapshot);
                }

                return GetBatchReviewSheets();
            }

            private void ResetBatchReviewSheetSnapshotIfIdle()
            {
                var hasActiveItems = _batchQueueItems.Any(item => item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running or BatchTaskStatus.Responding);
                if (!IsBatchRunning && !hasActiveItems)
                {
                    _batchReviewSheetSnapshot.Clear();
                }
            }

            private void LogReviewSubtaskDebug(
                string stage,
                string traceId,
                BatchSubtaskItem subtask,
                BatchTaskItem? batchItem,
                ReviewSheetPreset? preset,
                string? extra = null,
                bool isSuccess = true)
            {
                var fileName = Path.GetFileName(subtask.AudioPath);
                var snapshotTags = batchItem?.ReviewSheetSnapshot.Count > 0
                    ? string.Join(",", batchItem.ReviewSheetSnapshot.Select(sheet => sheet.FileTag))
                    : "(none)";
                var currentTags = string.Join(",", GetBatchReviewSheets().Select(sheet => sheet.FileTag));
                var summary = $"trace={traceId} stage={stage} title='{FormatLogValue(subtask.Title, 80)}' tag='{FormatLogValue(subtask.Tag, 80)}' audio='{FormatLogValue(subtask.AudioPath)}' presetTag='{FormatLogValue(preset?.FileTag, 80)}' presetName='{FormatLogValue(preset?.Name, 80)}' promptLen={preset?.Prompt?.Length ?? 0} snapshotTags='{FormatLogValue(snapshotTags)}' currentTags='{FormatLogValue(currentTags)}'";
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    summary += $" extra='{FormatLogValue(extra, 220)}'";
                }

                if (AppLogService.Instance.ShouldLog(isSuccess))
                {
                    AppendBatchLog("ReviewRegenerateDebug", fileName, isSuccess ? "Success" : "Failed", summary);
                    AppendBatchDebugLog("ReviewRegenerateDebug", summary, isSuccess);
                }

                if (!AppLogService.IsInitialized || !AppLogService.Instance.ShouldLogSuccess)
                {
                    return;
                }

                var detail = new System.Text.StringBuilder();
                detail.AppendLine($"traceId: {traceId}");
                detail.AppendLine($"stage: {stage}");
                detail.AppendLine($"audioPath: {subtask.AudioPath}");
                detail.AppendLine($"subtaskTitle: {subtask.Title}");
                detail.AppendLine($"subtaskTag: {subtask.Tag}");
                detail.AppendLine($"snapshotTags: {snapshotTags}");
                detail.AppendLine($"currentTags: {currentTags}");
                detail.AppendLine($"resolvedPresetName: {preset?.Name ?? ""}");
                detail.AppendLine($"resolvedPresetTag: {preset?.FileTag ?? ""}");
                detail.AppendLine("resolvedPrompt:");
                detail.AppendLine(preset?.Prompt ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    detail.AppendLine();
                    detail.AppendLine("extra:");
                    detail.AppendLine(extra);
                }

                _ = AppLogService.Instance.LogHttpDebugAsync("review", $"RegenerateSubtask-{stage} trace={traceId}", detail.ToString());
            }

            private void LogReviewRequestDebug(
                BatchQueueItem queueItem,
                string systemPrompt,
                string userTemplate,
                string userPrompt,
                string subtitlePath,
                int cueCount,
                string outputPath,
                AiChatRequestConfig runtimeRequest)
            {
                if (!AppLogService.IsInitialized || !AppLogService.Instance.ShouldLogSuccess)
                {
                    return;
                }

                var detail = new System.Text.StringBuilder();
                detail.AppendLine($"traceId: {queueItem.TraceId}");
                detail.AppendLine($"enqueueSource: {queueItem.EnqueueSource}");
                detail.AppendLine($"fileName: {queueItem.FileName}");
                detail.AppendLine($"audioPath: {queueItem.FullPath}");
                detail.AppendLine($"sheetName: {queueItem.SheetName}");
                detail.AppendLine($"sheetTag: {queueItem.SheetTag}");
                detail.AppendLine($"subtitlePath: {subtitlePath}");
                detail.AppendLine($"cueCount: {cueCount}");
                detail.AppendLine($"outputPath: {outputPath}");
                detail.AppendLine($"chatProfile: {AiChatProfile.Summary}");
                detail.AppendLine($"reasoningEnabled: {runtimeRequest.SummaryEnableReasoning}");
                detail.AppendLine();
                detail.AppendLine("queuePrompt:");
                detail.AppendLine(queueItem.Prompt ?? string.Empty);
                detail.AppendLine();
                detail.AppendLine("systemPrompt:");
                detail.AppendLine(systemPrompt);
                detail.AppendLine();
                detail.AppendLine("userTemplate:");
                detail.AppendLine(userTemplate);
                detail.AppendLine();
                detail.AppendLine("userPrompt:");
                detail.AppendLine(userPrompt);

                _ = AppLogService.Instance.LogHttpDebugAsync("review", $"ReviewRequest trace={queueItem.TraceId} tag={queueItem.SheetTag}", detail.ToString());
            }

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
            OnPropertyChanged(nameof(ReviewPageFooterStatusText));
            _notifyReviewLampChanged();
            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
        }

        private void UpdateBatchQueueStatusText()
        {
            var total = _batchQueueItems.Count;
            var completed = _batchQueueItems.Count(item => item.Status == BatchTaskStatus.Completed);
            var running = _batchQueueItems.Count(item => item.Status is BatchTaskStatus.Running or BatchTaskStatus.Responding);
            var failed = _batchQueueItems.Count(item => item.Status == BatchTaskStatus.Failed);
            var pending = Math.Max(total - completed - running - failed, 0);

            BatchQueueStatusText = total == 0
                ? "队列为空"
                : $"队列 {completed}/{total} 完成，运行 {running}，等待 {pending}，失败 {failed}";
        }

        private void RequestPackageProjectionRefresh()
        {
            if (_isPackageProjectionRefreshPending)
            {
                return;
            }

            _isPackageProjectionRefreshPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _isPackageProjectionRefreshPending = false;
                RefreshPackageProjections();
            }, DispatcherPriority.Background);
        }

        private void InitializePackageBuckets()
        {
            _packageBuckets.Clear();
            _packageBuckets.Add(new BatchBucketNavItem { Key = "pending", Title = "待处理", IconValue = "fa-regular fa-clock" });
            _packageBuckets.Add(new BatchBucketNavItem { Key = "running", Title = "处理中", IconValue = "fa-solid fa-arrows-rotate" });
            _packageBuckets.Add(new BatchBucketNavItem { Key = "completed", Title = "处理完成", IconValue = "fa-solid fa-check" });
            _packageBuckets.Add(new BatchBucketNavItem { Key = "failed", Title = "失败", IconValue = "fa-solid fa-triangle-exclamation" });
            _packageBuckets.Add(new BatchBucketNavItem { Key = "removed", Title = "删除", IconValue = "fa-regular fa-trash-can" });
            SelectedPackageBucket = _packageBuckets.FirstOrDefault();
        }

        private ObservableCollection<BatchPackageItem> GetBucketCollection(string? key)
        {
            return key switch
            {
                "running" => _runningPackages,
                "completed" => _completedPackages,
                "failed" => _failedPackages,
                "removed" => _removedPackages,
                _ => _pendingPackages
            };
        }

        private bool CanStartPackage(BatchPackageItem? package)
            => package != null && !package.IsRemoved && package.State is ProcessingDisplayState.Pending or ProcessingDisplayState.Partial or ProcessingDisplayState.Failed;

        private void StartPackage(BatchPackageItem? package)
        {
            EnqueuePackage(package);
        }

        private void RefreshBucketCounts()
        {
            foreach (var bucket in _packageBuckets)
            {
                bucket.Count = bucket.Key switch
                {
                    "pending" => _pendingPackages.Count,
                    "running" => _runningPackages.Count,
                    "completed" => _completedPackages.Count,
                    "failed" => _failedPackages.Count,
                    "removed" => _removedPackages.Count,
                    _ => 0
                };
            }

            OnPropertyChanged(nameof(CurrentBucketPackages));
        }

        public void RefreshPackageProjections()
        {
            _batchPackageStateService.EnsurePackages(_fileLibrary.AudioFiles);

            var batchSheets = GetBatchReviewSheets();
            var packages = _fileLibrary.AudioFiles
                .Select(audioFile => BuildPackageItem(audioFile, batchSheets))
                .ToList();

            NormalizeExpandedPackages(packages);

            ReplacePackageCollection(_pendingPackages, packages.Where(package =>
                package.State is ProcessingDisplayState.Pending or ProcessingDisplayState.Partial));
            ReplacePackageCollection(_runningPackages, packages.Where(package =>
                package.State == ProcessingDisplayState.Running));
            ReplacePackageCollection(_failedPackages, packages.Where(package =>
                package.State == ProcessingDisplayState.Failed));
            ReplacePackageCollection(_completedPackages, packages.Where(package => package.State == ProcessingDisplayState.Completed));
            ReplacePackageCollection(_removedPackages, packages.Where(package => package.State == ProcessingDisplayState.Removed));
            RefreshBucketCounts();

            var selectedPath = SelectedPackage?.FullPath;
            SelectedPackage = string.IsNullOrWhiteSpace(selectedPath)
                ? null
                : CurrentBucketPackages.FirstOrDefault(package =>
                    string.Equals(package.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));

            _fileLibrary.ApplyAudioProcessingSnapshots(packages.Select(CreateAudioSnapshot).ToList());
        }

        private void NormalizeExpandedPackages(IReadOnlyList<BatchPackageItem> packages)
        {
            var expandedPackages = packages.Where(package => package.IsExpanded).ToList();
            if (expandedPackages.Count <= 1)
            {
                return;
            }

            var keepExpanded = expandedPackages.FirstOrDefault(package =>
                                   string.Equals(package.FullPath, SelectedPackage?.FullPath, StringComparison.OrdinalIgnoreCase))
                               ?? expandedPackages.First();

            foreach (var package in expandedPackages)
            {
                var shouldExpand = ReferenceEquals(package, keepExpanded);
                if (package.IsExpanded == shouldExpand)
                {
                    continue;
                }

                package.IsExpanded = shouldExpand;
                _batchPackageStateService.SetExpanded(package.FullPath, shouldExpand);
            }
        }

        private BatchPackageItem BuildPackageItem(MediaFileItem audioFile, IReadOnlyCollection<ReviewSheetPreset> batchSheets)
        {
            var batchTask = _batchTasks.FirstOrDefault(item =>
                string.Equals(item.FullPath, audioFile.FullPath, StringComparison.OrdinalIgnoreCase));
            var queueItems = _batchQueueItems
                .Where(item => string.Equals(item.FullPath, audioFile.FullPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var package = new BatchPackageItem
            {
                DisplayName = audioFile.Name,
                FullPath = audioFile.FullPath,
                IsExpanded = _batchPackageStateService.IsExpanded(audioFile.FullPath),
                IsPaused = _batchPackageStateService.IsPaused(audioFile.FullPath),
                IsRemoved = _batchPackageStateService.IsRemoved(audioFile.FullPath)
            };

            var subtaskProgressSum = 0d;
            var completedCount = 0;
            var failedCount = 0;
            var activeCount = 0;

            var hasSpeechLayer = ShouldGenerateGeneratedSubtitleForReview || HasGeneratedSubtitleForSelectedMode(audioFile.FullPath);

            if (hasSpeechLayer)
            {
                var speechQueueItem = SelectPreferredQueueItem(queueItems,
                    item => item.QueueType == BatchQueueItemType.SpeechSubtitle);
                var speechState = BuildSpeechSubtask(audioFile.FullPath, speechQueueItem);
                speechState.IndentMargin = new Avalonia.Thickness(24, 6, 0, 0);
                package.Subtasks.Add(speechState);
            }

            foreach (var sheet in batchSheets)
            {
                var queueItem = SelectPreferredQueueItem(queueItems, item =>
                    item.QueueType == BatchQueueItemType.ReviewSheet
                    && string.Equals(item.SheetTag, sheet.FileTag, StringComparison.OrdinalIgnoreCase));
                var reviewSubtask = BuildReviewSubtask(audioFile.FullPath, sheet, queueItem);
                reviewSubtask.IndentMargin = new Avalonia.Thickness(hasSpeechLayer ? 48 : 24, 6, 0, 0);
                package.Subtasks.Add(reviewSubtask);
            }

            foreach (var subtask in package.Subtasks)
            {
                subtaskProgressSum += Math.Clamp(subtask.Progress, 0, 1);
                if (subtask.State == ProcessingDisplayState.Completed)
                {
                    completedCount++;
                }
                else if (subtask.State == ProcessingDisplayState.Failed)
                {
                    failedCount++;
                }
                else if (subtask.State is ProcessingDisplayState.Pending or ProcessingDisplayState.Running)
                {
                    activeCount++;
                }
            }

            package.TotalCount = package.Subtasks.Count;
            package.CompletedCount = completedCount;
            package.FailedCount = failedCount;
            package.Progress = package.TotalCount == 0 ? 0 : subtaskProgressSum / package.TotalCount;

            if (package.IsRemoved)
            {
                package.State = ProcessingDisplayState.Removed;
                package.StateText = "已删除";
                package.SummaryText = package.TotalCount == 0
                    ? "已从批处理中心移除"
                    : $"已移除 · 完成 {completedCount}/{package.TotalCount}";
                return package;
            }

            if (package.IsPaused)
            {
                package.State = ProcessingDisplayState.Pending;
                package.StateText = "已暂停";
                package.SummaryText = package.TotalCount == 0
                    ? "已暂停，等待重新加入队列"
                    : $"已暂停 · 完成 {completedCount}/{package.TotalCount}";
                return package;
            }

            if (package.TotalCount > 0 && completedCount >= package.TotalCount)
            {
                package.State = ProcessingDisplayState.Completed;
                package.StateText = "已完成";
                package.SummaryText = $"完成 {completedCount}/{package.TotalCount}";
                return package;
            }

            if (activeCount > 0)
            {
                var hasRespondingItem = queueItems.Any(item => item.Status == BatchTaskStatus.Responding);
                package.State = queueItems.Any(item => item.Status is BatchTaskStatus.Running or BatchTaskStatus.Responding)
                    ? ProcessingDisplayState.Running
                    : ProcessingDisplayState.Pending;
                package.StateText = package.State == ProcessingDisplayState.Running
                    ? (hasRespondingItem ? "AI已响应" : "处理中")
                    : "待处理";
                package.SummaryText = package.TotalCount == 0
                    ? (batchTask?.StatusMessage ?? "待处理")
                    : $"完成 {completedCount}/{package.TotalCount} · 运行 {activeCount}";
                return package;
            }

            if (completedCount > 0)
            {
                package.State = failedCount > 0 ? ProcessingDisplayState.Failed : ProcessingDisplayState.Partial;
                package.StateText = failedCount > 0 ? "部分失败" : "部分完成";
                package.SummaryText = failedCount > 0
                    ? $"完成 {completedCount}/{package.TotalCount} · 失败 {failedCount}"
                    : $"完成 {completedCount}/{package.TotalCount}";
                return package;
            }

            if (failedCount > 0 || batchTask?.Status == BatchTaskStatus.Failed)
            {
                package.State = ProcessingDisplayState.Failed;
                package.StateText = "失败";
                package.SummaryText = batchTask?.StatusMessage ?? "有子任务失败";
                return package;
            }

            package.State = ProcessingDisplayState.Pending;
            package.StateText = "未处理";
            package.SummaryText = package.TotalCount == 0 ? "当前未配置批处理子任务" : $"待处理 {package.TotalCount} 项";
            return package;
        }

        private static BatchQueueItem? SelectPreferredQueueItem(
            IEnumerable<BatchQueueItem> queueItems,
            Func<BatchQueueItem, bool> predicate)
        {
            return queueItems
                .Where(predicate)
                .OrderByDescending(item => item.Status switch
                {
                    BatchTaskStatus.Responding => 5,
                    BatchTaskStatus.Running => 4,
                    BatchTaskStatus.Pending => 3,
                    BatchTaskStatus.Failed => 2,
                    BatchTaskStatus.Completed => 1,
                    _ => 0
                })
                .FirstOrDefault();
        }

        private BatchSubtaskItem BuildSpeechSubtask(string audioPath, BatchQueueItem? queueItem)
        {
            var completed = HasGeneratedSubtitleForSelectedMode(audioPath);
            var subtitleTitle = GetGeneratedSubtitleDisplayName();
            if (queueItem != null && queueItem.Status != BatchTaskStatus.Completed)
            {
                return new BatchSubtaskItem
                {
                    Title = subtitleTitle,
                    AudioPath = audioPath,
                    Tag = "speech",
                    IconValue = "fa-solid fa-closed-captioning",
                    IsSpeechSubtask = true,
                    CanRegenerate = queueItem.Status is not (BatchTaskStatus.Running or BatchTaskStatus.Responding),
                    State = MapQueueStatus(queueItem.Status),
                    StatusText = queueItem.StatusMessage,
                    Progress = queueItem.Progress,
                    IsActive = queueItem.CanCancel
                };
            }

            return new BatchSubtaskItem
            {
                Title = subtitleTitle,
                AudioPath = audioPath,
                Tag = "speech",
                IconValue = "fa-solid fa-closed-captioning",
                IsSpeechSubtask = true,
                CanRegenerate = true,
                State = completed ? ProcessingDisplayState.Completed : ProcessingDisplayState.Pending,
                StatusText = completed ? "已完成" : "待处理",
                Progress = completed ? 1 : 0,
                IsActive = false
            };
        }

        private BatchSubtaskItem BuildReviewSubtask(string audioPath, ReviewSheetPreset sheet, BatchQueueItem? queueItem)
        {
            var reviewPath = GetReviewSheetPath(audioPath, sheet.FileTag);
            var completed = File.Exists(reviewPath);
            if (queueItem != null && queueItem.Status != BatchTaskStatus.Completed)
            {
                return new BatchSubtaskItem
                {
                    Title = sheet.Name,
                    AudioPath = audioPath,
                    Tag = sheet.FileTag,
                    IconValue = "fa-solid fa-brain",
                    IsSpeechSubtask = false,
                    CanRegenerate = queueItem.Status is not (BatchTaskStatus.Running or BatchTaskStatus.Responding),
                    State = MapQueueStatus(queueItem.Status),
                    StatusText = queueItem.StatusMessage,
                    Progress = queueItem.Progress,
                    IsActive = queueItem.CanCancel
                };
            }

            return new BatchSubtaskItem
            {
                Title = sheet.Name,
                AudioPath = audioPath,
                Tag = sheet.FileTag,
                IconValue = "fa-solid fa-brain",
                IsSpeechSubtask = false,
                CanRegenerate = true,
                State = completed ? ProcessingDisplayState.Completed : ProcessingDisplayState.Pending,
                StatusText = completed ? "已完成" : "待处理",
                Progress = completed ? 1 : 0,
                IsActive = false
            };
        }

        private static ProcessingDisplayState MapQueueStatus(BatchTaskStatus status)
        {
            return status switch
            {
                BatchTaskStatus.Completed => ProcessingDisplayState.Completed,
                BatchTaskStatus.Responding => ProcessingDisplayState.Running,
                BatchTaskStatus.Running => ProcessingDisplayState.Running,
                BatchTaskStatus.Failed => ProcessingDisplayState.Failed,
                _ => ProcessingDisplayState.Pending
            };
        }

        private static AudioFileProcessingSnapshot CreateAudioSnapshot(BatchPackageItem package)
        {
            return new AudioFileProcessingSnapshot
            {
                AudioPath = package.FullPath,
                State = package.State,
                BadgeText = package.StateText,
                DetailText = package.SummaryText
            };
        }

        private static void ReplacePackageCollection(ObservableCollection<BatchPackageItem> target, IEnumerable<BatchPackageItem> items)
        {
            target.Clear();
            foreach (var item in items)
            {
                target.Add(item);
            }
        }

        private bool CanRemovePackage(BatchPackageItem? package)
            => package?.CanDelete == true;

        private bool CanRestorePackage(BatchPackageItem? package)
            => package?.IsRemoved == true;

        private bool CanPausePackage(BatchPackageItem? package)
            => package?.CanPause == true;

        private bool CanResumePackage(BatchPackageItem? package)
            => package?.CanResume == true;

        private bool CanEnqueuePackage(BatchPackageItem? package)
            => package?.CanEnqueue == true;

        private bool CanRegeneratePackage(BatchPackageItem? package)
            => package != null && !package.IsRemoved && package.State != ProcessingDisplayState.Running;

        private bool CanRegenerateSubtask(BatchSubtaskItem? subtask)
            => subtask?.CanRegenerate == true && subtask.State != ProcessingDisplayState.Running;

        private void RemovePackage(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            _batchPackageStateService.SetRemoved(package.FullPath, true);
            LoadBatchTasksFromLibrary();
            RefreshPackageProjections();
        }

        private void RestorePackage(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            _batchPackageStateService.SetRemoved(package.FullPath, false);
            LoadBatchTasksFromLibrary();
            RefreshPackageProjections();
        }

        private void PausePackage(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            _batchPackageStateService.SetPaused(package.FullPath, true);

            var parent = BatchTasks.FirstOrDefault(item =>
                string.Equals(item.FullPath, package.FullPath, StringComparison.OrdinalIgnoreCase));

            foreach (var queueItem in _batchQueueItems.Where(item =>
                         string.Equals(item.FullPath, package.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                queueItem.PauseRequested = true;
                if (queueItem.Status is BatchTaskStatus.Running or BatchTaskStatus.Responding)
                {
                    queueItem.Cts?.Cancel();
                }
                else if (queueItem.Status == BatchTaskStatus.Pending)
                {
                    UpdateQueueItem(queueItem, BatchTaskStatus.Pending, queueItem.Progress, "已暂停");
                }
            }

            if (parent != null)
            {
                UpdateBatchItem(parent, BatchTaskStatus.Pending, parent.Progress, "已暂停");
            }

            BatchStatusMessage = $"已暂停：{package.DisplayName}";
            RefreshPackageProjections();
        }

        private void ResumePackage(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            _batchPackageStateService.SetPaused(package.FullPath, false);

            foreach (var queueItem in _batchQueueItems.Where(item =>
                         string.Equals(item.FullPath, package.FullPath, StringComparison.OrdinalIgnoreCase)
                         && item.Status == BatchTaskStatus.Pending))
            {
                queueItem.PauseRequested = false;
                UpdateQueueItem(queueItem, BatchTaskStatus.Pending, queueItem.Progress, "待处理");
            }

            BatchStatusMessage = $"已继续：{package.DisplayName}";
            RefreshPackageProjections();

            if (_batchQueueItems.Any(item => item.Status == BatchTaskStatus.Pending && !_batchPackageStateService.IsPaused(item.FullPath)))
            {
                StartBatchQueueRunner("任务已继续");
            }
        }

        private void EnqueuePackage(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            _batchPackageStateService.SetRemoved(package.FullPath, false);
            _batchPackageStateService.SetPaused(package.FullPath, false);
            RemoveQueueItemsForPackage(package.FullPath, item => item.Status == BatchTaskStatus.Failed);

            var batchItem = BatchTasks.FirstOrDefault(item =>
                string.Equals(item.FullPath, package.FullPath, StringComparison.OrdinalIgnoreCase));

            if (batchItem == null)
            {
                LoadBatchTasksFromLibrary();
                batchItem = BatchTasks.FirstOrDefault(item =>
                    string.Equals(item.FullPath, package.FullPath, StringComparison.OrdinalIgnoreCase));
            }

            if (batchItem == null)
            {
                BatchStatusMessage = $"未找到可入队的文件：{package.DisplayName}";
                RefreshPackageProjections();
                return;
            }

            var reviewSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && reviewSheets.Count > 0;
            var enableSpeech = ShouldGenerateGeneratedSubtitleForReview;
            var added = PrepareAndEnqueueSingleItem(batchItem, reviewSheets, enableSpeech, enableReview, false);

            BatchStatusMessage = added > 0
                ? $"已加入任务队列：{package.DisplayName}"
                : $"{package.DisplayName} 当前没有需要重新加入的子任务";

            RefreshPackageProjections();

            if (added > 0)
            {
                StartBatchQueueRunner("任务已加入队列");
            }
        }

        private void RegeneratePackage(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            _batchPackageStateService.SetRemoved(package.FullPath, false);
            _batchPackageStateService.SetPaused(package.FullPath, false);
            RemoveQueueItemsForPackage(package.FullPath, _ => true);

            var batchItem = GetOrCreateBatchTaskForAudio(package.FullPath, package.DisplayName);
            if (batchItem == null)
            {
                BatchStatusMessage = $"未找到可重新生成的文件包：{package.DisplayName}";
                return;
            }

            var reviewSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && reviewSheets.Count > 0;
            var enableSpeech = ShouldGenerateGeneratedSubtitleForReview;
            var added = PrepareAndEnqueueSingleItem(batchItem, reviewSheets, enableSpeech, enableReview, true);

            BatchStatusMessage = added > 0
                ? $"已重新生成整个文件包：{package.DisplayName}"
                : $"{package.DisplayName} 当前没有可重新生成的任务";

            RefreshPackageProjections();

            if (added > 0)
            {
                StartBatchQueueRunner("整包重新生成已加入队列");
            }
        }

        private void RegenerateSubtask(BatchSubtaskItem? subtask)
        {
            if (subtask == null || string.IsNullOrWhiteSpace(subtask.AudioPath))
            {
                return;
            }

            _batchPackageStateService.SetRemoved(subtask.AudioPath, false);
            _batchPackageStateService.SetPaused(subtask.AudioPath, false);

            var batchItem = GetOrCreateBatchTaskForAudio(subtask.AudioPath, Path.GetFileName(subtask.AudioPath));
            if (batchItem == null)
            {
                BatchStatusMessage = "未找到对应文件包，无法重新生成该任务";
                return;
            }

            var traceId = CreateDebugTraceId("review-regen");

            if (subtask.IsSpeechSubtask)
            {
                var reviewSheets = GetReviewSheetsForTask(batchItem);
                var enableReview = IsAiConfigured && reviewSheets.Count > 0;
                var forceReviewAfterSpeech = _configProvider().ContextMenuForceRegeneration && enableReview;

                if (forceReviewAfterSpeech)
                {
                    RemoveQueueItemsForPackage(batchItem.FullPath, item => item.QueueType == BatchQueueItemType.ReviewSheet);
                    batchItem.ForceReviewRegeneration = true;
                    batchItem.ReviewTotal = reviewSheets.Count;
                    batchItem.ReviewCompleted = 0;
                    batchItem.ReviewFailed = 0;
                    batchItem.ReviewPending = reviewSheets.Count;
                    batchItem.ReviewStatusText = reviewSheets.Count == 0
                        ? "复盘:未勾选"
                        : "复盘:等待字幕";
                    batchItem.HasAiSummary = false;
                }

                ForceEnqueueSpeechSubtitle(batchItem);
                BatchStatusMessage = forceReviewAfterSpeech
                    ? $"已重新生成任务：{subtask.Title}，字幕完成后将继续重新生成复盘"
                    : $"已重新生成任务：{subtask.Title}";
                RefreshPackageProjections();
                StartBatchQueueRunner("Speech 任务已加入队列");
                return;
            }

            LogReviewSubtaskDebug("click", traceId, subtask, batchItem, null);

            var preset = GetReviewSheetsForTask(batchItem).FirstOrDefault(sheet =>
                string.Equals(sheet.FileTag, subtask.Tag, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
            {
                LogReviewSubtaskDebug("preset-missing", traceId, subtask, batchItem, null,
                    "未在任务快照或当前批处理配置中找到对应复盘分类", isSuccess: false);
                BatchStatusMessage = $"未找到复盘任务配置：{subtask.Tag}";
                return;
            }

            LogReviewSubtaskDebug("preset-resolved", traceId, subtask, batchItem, preset);

            ForceEnqueueReviewQueueItem(batchItem, preset, "RegenerateSubtask", traceId);
            BatchStatusMessage = $"已重新生成任务：{subtask.Title}";
            RefreshPackageProjections();
            StartBatchQueueRunner("复盘任务已加入队列");
        }

        private void TogglePackageExpanded(BatchPackageItem? package)
        {
            if (package == null)
            {
                return;
            }

            var isExpanded = !package.IsExpanded;

            if (isExpanded)
            {
                foreach (var other in EnumerateAllPackages())
                {
                    if (ReferenceEquals(other, package) || !other.IsExpanded)
                    {
                        continue;
                    }

                    _batchPackageStateService.SetExpanded(other.FullPath, false);
                    other.IsExpanded = false;
                }
            }

            _batchPackageStateService.SetExpanded(package.FullPath, isExpanded);
            package.IsExpanded = isExpanded;
        }

        private IEnumerable<BatchPackageItem> EnumerateAllPackages()
        {
            foreach (var item in _pendingPackages)
            {
                yield return item;
            }

            foreach (var item in _runningPackages)
            {
                yield return item;
            }

            foreach (var item in _failedPackages)
            {
                yield return item;
            }

            foreach (var item in _completedPackages)
            {
                yield return item;
            }

            foreach (var item in _removedPackages)
            {
                yield return item;
            }
        }

        private void RemoveQueueItemsForPackage(string audioPath, Func<BatchQueueItem, bool> predicate)
        {
            var toRemove = _batchQueueItems
                .Where(item => string.Equals(item.FullPath, audioPath, StringComparison.OrdinalIgnoreCase))
                .Where(predicate)
                .ToList();

            foreach (var item in toRemove)
            {
                _batchQueueItems.Remove(item);
            }
        }

        private BatchTaskItem? GetOrCreateBatchTaskForAudio(string audioPath, string fileName)
        {
            var item = BatchTasks.FirstOrDefault(task =>
                string.Equals(task.FullPath, audioPath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                return item;
            }

            LoadBatchTasksFromLibrary();
            item = BatchTasks.FirstOrDefault(task =>
                string.Equals(task.FullPath, audioPath, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                return item;
            }

            item = new BatchTaskItem
            {
                FileName = fileName,
                FullPath = audioPath,
                Status = BatchTaskStatus.Pending,
                Progress = 0,
                StatusMessage = "待处理"
            };

            _batchTasks.Add(item);
            return item;
        }

        private void ForceEnqueueSpeechSubtitle(BatchTaskItem batchItem)
        {
            RemoveQueueItemsForPackage(batchItem.FullPath, item => item.QueueType == BatchQueueItemType.SpeechSubtitle);

            _batchQueueItems.Add(new BatchQueueItem
            {
                FileName = batchItem.FileName,
                FullPath = batchItem.FullPath,
                SheetName = GetGeneratedSubtitleDisplayName(),
                SheetTag = "speech",
                Prompt = "",
                QueueType = BatchQueueItemType.SpeechSubtitle,
                Status = BatchTaskStatus.Pending,
                Progress = 0,
                StatusMessage = "待处理"
            });

            UpdateBatchItem(batchItem, BatchTaskStatus.Pending, 0, $"待生成{GetGeneratedSubtitleDisplayName()}");
        }

        private void ForceEnqueueReviewQueueItem(
            BatchTaskItem batchItem,
            ReviewSheetPreset sheet,
            string enqueueSource = "",
            string traceId = "")
        {
            RemoveQueueItemsForPackage(batchItem.FullPath, item =>
                item.QueueType == BatchQueueItemType.ReviewSheet
                && string.Equals(item.SheetTag, sheet.FileTag, StringComparison.OrdinalIgnoreCase));

            _batchQueueItems.Add(new BatchQueueItem
            {
                FileName = batchItem.FileName,
                FullPath = batchItem.FullPath,
                SheetName = sheet.Name,
                SheetTag = sheet.FileTag,
                Prompt = sheet.Prompt,
                EnqueueSource = enqueueSource,
                TraceId = traceId,
                QueueType = BatchQueueItemType.ReviewSheet,
                Status = BatchTaskStatus.Pending,
                Progress = 0,
                StatusMessage = "待处理"
            });

            UpdateBatchItem(batchItem, BatchTaskStatus.Pending, 0, $"待生成 {sheet.Name}");
        }

        // ── Batch task loading ──

        private void LoadBatchTasksFromLibrary()
        {
            if (_fileLibrary.AudioFiles.Count == 0)
            {
                _fileLibrary.RefreshAudioLibrary();
            }

            var batchSheets = GetBatchReviewSheets();
            _batchTasks.Clear();
            foreach (var audio in _fileLibrary.AudioFiles)
            {
                if (_batchPackageStateService.IsRemoved(audio.FullPath))
                {
                    continue;
                }

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
                var requireSpeech = ShouldGenerateGeneratedSubtitleForReview;
                var hasSpeechSubtitle = HasGeneratedSubtitleForSelectedMode(audio.FullPath);
                var subtitlePath = GetSubtitlePathForReview(audio.FullPath);
                var hasSubtitle = !string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath);
                var hasAiSubtitle = FileLibraryViewModel.HasAiSubtitle(audio.FullPath);
                var pendingSheets = Math.Max(totalSheets - completedSheets, 0);
                var statusMessage = hasSubtitle
                    ? "待处理"
                    : (requireSpeech ? $"待生成{GetGeneratedSubtitleDisplayName()}" : "缺少字幕");
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
            RefreshPackageProjections();
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
            RefreshPackageProjections();
        }

        private bool CanStartBatchProcessing()
        {
            if (IsBatchRunning || BatchTasks.Count == 0)
            {
                return false;
            }

            var batchSheets = GetBatchReviewSheets();
            var enableReview = IsAiConfigured && batchSheets.Count > 0;
            var enableSpeech = ShouldGenerateGeneratedSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                return false;
            }

            var needsSpeech = enableSpeech && BatchTasks.Any(task => !HasGeneratedSubtitleForSelectedMode(task.FullPath));
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
            var enableSpeech = ShouldGenerateGeneratedSubtitleForReview;

            if (enableSpeech && enableReview)
            {
                return $"开始生成{GetGeneratedSubtitleDisplayName()}+复盘";
            }

            if (enableSpeech)
            {
                return $"开始生成{GetGeneratedSubtitleDisplayName()}";
            }

            if (enableReview)
            {
                return "开始生成复盘";
            }

            return "开始处理";
        }

        private bool CanGenerateSpeechSubtitleFromStorage()
        {
            var config = _configProvider();
            return HasAvailableConfiguredSubtitlePath(config);
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
            var enableSpeech = ShouldGenerateGeneratedSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                BatchStatusMessage = "未启用所选字幕来源或复盘生成";
                return;
            }

            var needsSpeech = enableSpeech && BatchTasks.Any(task => !HasGeneratedSubtitleForSelectedMode(task.FullPath));
            if (needsSpeech && !CanGenerateSpeechSubtitleFromStorage())
            {
                BatchStatusMessage = BuildSubtitleSourceUnavailableStatusText();
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
                && item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running or BatchTaskStatus.Responding);

            if (existsInQueue)
            {
                return;
            }

            _batchQueueItems.Add(new BatchQueueItem
            {
                FileName = batchItem.FileName,
                FullPath = batchItem.FullPath,
                SheetName = GetGeneratedSubtitleDisplayName(),
                SheetTag = "speech",
                Prompt = "",
                QueueType = BatchQueueItemType.SpeechSubtitle,
                Status = BatchTaskStatus.Pending,
                Progress = 0,
                StatusMessage = "待处理"
            });
            if (ShouldWriteBatchLogSuccess)
            {
                AppendBatchLog("SpeechEnqueue", batchItem.FileName, "Success", $"入队{GetGeneratedSubtitleDisplayName()}");
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
                    && item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running or BatchTaskStatus.Responding);

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
            SetReviewSheetSnapshot(batchItem, reviewSheets);

            var speechExists = HasGeneratedSubtitleForSelectedMode(batchItem.FullPath);
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
                UpdateBatchItem(batchItem, BatchTaskStatus.Pending, 0, $"待生成{GetGeneratedSubtitleDisplayName()}");
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
                    AppendBatchLog("SpeechSkip", batchItem.FileName, "Success", $"{GetGeneratedSubtitleDisplayName()}已存在");
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
                                var item = _batchQueueItems.FirstOrDefault(i =>
                                    i.Status == BatchTaskStatus.Pending
                                    && !_batchPackageStateService.IsPaused(i.FullPath));
                                if (item != null)
                                {
                                    item.PauseRequested = false;
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

            if (_batchPackageStateService.IsPaused(queueItem.FullPath))
            {
                UpdateQueueItem(queueItem, BatchTaskStatus.Pending, queueItem.Progress, "已暂停");
                if (parentItem != null)
                {
                    UpdateBatchItem(parentItem, BatchTaskStatus.Pending, parentItem.Progress, "已暂停");
                }
                return;
            }

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
            if (!TryBuildReviewRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                var message = string.IsNullOrWhiteSpace(errorMessage) ? "AI 配置无效" : errorMessage;
                UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, message);
                if (ShouldWriteBatchLogFailure)
                {
                    AppendBatchLog("ReviewFailed", queueItem.FileName, "Failed", message);
                }
                if (parentItem != null)
                {
                    UpdateBatchReviewProgress(parentItem, BatchTaskStatus.Failed);
                }
                return;
            }

            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    endpoint != null ? GetEndpointProfileKey(endpoint) : "ai",
                    runtimeRequest.AzureTenantId,
                    runtimeRequest.AzureClientId);

                if (tokenProvider == null)
                {
                    var message = "AAD 登录已失效，请先在设置中重新登录当前复盘模型对应终结点。";
                    UpdateQueueItem(queueItem, BatchTaskStatus.Failed, 0, message);
                    if (ShouldWriteBatchLogFailure)
                    {
                        AppendBatchLog("ReviewFailed", queueItem.FileName, "Failed", message);
                    }
                    if (parentItem != null)
                    {
                        UpdateBatchReviewProgress(parentItem, BatchTaskStatus.Failed);
                    }
                    return;
                }
            }
            var runtimeInsightService = new AiInsightService(tokenProvider);

            var systemPrompt = AiConfig.GetEffectiveReviewSystemPrompt(config.AiConfig?.ReviewSystemPrompt);
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
            var summaryPath = GetReviewSheetPath(queueItem.FullPath, queueItem.SheetTag);

            if (string.Equals(queueItem.EnqueueSource, "RegenerateSubtask", StringComparison.OrdinalIgnoreCase))
            {
                var subtitlePath = GetSubtitlePathForReview(queueItem.FullPath);
                LogReviewRequestDebug(queueItem, systemPrompt, userTemplate, userPrompt, subtitlePath, cues.Count, summaryPath, runtimeRequest);
            }

            try
            {
                var sb = new System.Text.StringBuilder();
                AiRequestOutcome? outcome = null;
                var firstChunkReceived = 0;
                await runtimeInsightService.StreamChatAsync(
                    runtimeRequest,
                    systemPrompt,
                    userPrompt,
                    chunk =>
                    {
                        if (!string.IsNullOrWhiteSpace(chunk)
                            && Interlocked.Exchange(ref firstChunkReceived, 1) == 0)
                        {
                            UpdateQueueItem(queueItem, BatchTaskStatus.Responding, 0.35, "AI 已响应");
                            if (parentItem != null)
                            {
                                UpdateBatchItem(parentItem, BatchTaskStatus.Responding, Math.Max(parentItem.Progress, 0.35), "AI 已响应");
                            }
                        }

                        sb.Append(chunk);
                    },
                    localToken,
                    AiChatProfile.Summary,
                    enableReasoning: runtimeRequest.SummaryEnableReasoning,
                    onOutcome: o => outcome = o);

                var markdown = TimeLinkHelper.InjectTimeLinks(sb.ToString());
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
                else if (runtimeRequest.SummaryEnableReasoning)
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
                if (queueItem.PauseRequested || _batchPackageStateService.IsPaused(queueItem.FullPath))
                {
                    UpdateQueueItem(queueItem, BatchTaskStatus.Pending, queueItem.Progress, "已暂停");
                    if (parentItem != null)
                    {
                        UpdateBatchItem(parentItem, BatchTaskStatus.Pending, parentItem.Progress, "已暂停");
                    }
                    return;
                }

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
                    GetReviewSubtitleSourceMode(),
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
                    AppendBatchLog("SpeechSuccess", queueItem.FileName, "Success", $"{GetGeneratedSubtitleDisplayName()}完成");
                }

                lock (cueLock)
                {
                    cueCache.Remove(queueItem.FullPath);
                }

                if (parentItem != null)
                {
                    parentItem.HasAiSubtitle = true;
                }

                var reviewSheets = GetReviewSheetsForTask(parentItem);
                var enableReview = IsAiConfigured && reviewSheets.Count > 0;
                if (!enableReview)
                {
                    if (parentItem != null)
                    {
                        UpdateBatchItem(parentItem, BatchTaskStatus.Completed, 1, $"{GetGeneratedSubtitleDisplayName()}完成");
                    }
                    return;
                }

                if (parentItem != null)
                {
                    var forceReviewRegeneration = parentItem.ForceReviewRegeneration;
                    var completed = forceReviewRegeneration
                        ? 0
                        : reviewSheets.Count(sheet =>
                            File.Exists(GetReviewSheetPath(parentItem.FullPath, sheet.FileTag)));
                    parentItem.ReviewTotal = reviewSheets.Count;
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

                    if (forceReviewRegeneration)
                    {
                        parentItem.HasAiSummary = false;
                        parentItem.ReviewStatusText = parentItem.ReviewTotal == 0
                            ? "复盘:未勾选"
                            : $"复盘 0/{parentItem.ReviewTotal}";
                    }
                }

                var added = await Dispatcher.UIThread.InvokeAsync(() =>
                    parentItem == null
                        ? 0
                        : EnqueueReviewQueueItemsForAudioInternal(
                            parentItem,
                            reviewSheets,
                            parentItem.ForceReviewRegeneration));

                if (parentItem != null)
                {
                    parentItem.ForceReviewRegeneration = false;
                }

                if (parentItem != null)
                {
                    var statusMessage = added > 0 ? $"{GetGeneratedSubtitleDisplayName()}完成，待复盘" : $"{GetGeneratedSubtitleDisplayName()}完成";
                    UpdateBatchItem(parentItem, BatchTaskStatus.Running, parentItem.Progress, statusMessage);
                }
            }
            catch (OperationCanceledException)
            {
                if (queueItem.PauseRequested || _batchPackageStateService.IsPaused(queueItem.FullPath))
                {
                    UpdateQueueItem(queueItem, BatchTaskStatus.Pending, queueItem.Progress, "已暂停");
                    if (parentItem != null)
                    {
                        UpdateBatchItem(parentItem, BatchTaskStatus.Pending, parentItem.Progress, "已暂停");
                    }
                    return;
                }

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

            var subtitlePath = GetSubtitlePathForReview(audioPath);
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
                    : BatchTaskStatus.Responding;
                var statusMessage = finished
                    ? (item.ReviewFailed > 0 ? "完成(含失败)" : "完成")
                    : "AI 已响应";

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
                RequestPackageProjectionRefresh();
            });
        }

        private void UpdateBatchItem(BatchTaskItem item, BatchTaskStatus status, double progress, string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.Status = status;
                item.Progress = progress;
                item.StatusMessage = message;
                RequestPackageProjectionRefresh();
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
                var raw = File.ReadAllText(sheetPath);
                sheet.Markdown = PrepareStableReviewContent(raw);
                sheet.StatusMessage = $"已加载: {Path.GetFileName(sheetPath)}";
            }
            else
            {
                sheet.StatusMessage = "未找到复盘内容，可生成。";
            }

            GenerateReviewSummaryCommand.RaiseCanExecuteChanged();
            GenerateAllReviewSheetsCommand.RaiseCanExecuteChanged();
        }

        private static string PrepareStableReviewContent(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return markdown;
            }

            return TimeLinkHelper.InjectTimeLinks(markdown);
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
            var requireSpeech = ShouldGenerateGeneratedSubtitleForReview;
            var subtitlePath = GetSubtitlePathForReview(audioFile.FullPath);
            var hasSubtitle = !string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath);
            if (!hasSubtitle)
            {
                foreach (var sheet in sheets)
                {
                    sheet.StatusMessage = requireSpeech ? $"缺少{GetGeneratedSubtitleDisplayName()}" : "缺少字幕";
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
                    && item.Status is BatchTaskStatus.Pending or BatchTaskStatus.Running or BatchTaskStatus.Responding);

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
            var allowSpeechGeneration = ShouldGenerateGeneratedSubtitleForReview
                && selectedAudio != null
                && !IsSpeechSubtitleGenerating
                && (HasGeneratedSubtitleForSelectedMode(selectedAudio.FullPath) || CanGenerateSpeechSubtitleFromStorage());

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
                    ? $"{GetGeneratedSubtitleDisplayName()}未就绪，无法生成复盘"
                    : SpeechSubtitleStatusMessage;
                return;
            }

            EnqueueReviewSheetsForAudio(audioFile, new[] { sheet });
            StartBatchQueueRunner("复盘已加入队列");
        }

        private bool CanGenerateAllReviewSheets()
        {
            var hasCues = _fileLibrary.SubtitleCues.Count > 0;
            var selectedAudio = _fileLibrary.SelectedAudioFile;
            var allowSpeechGeneration = ShouldGenerateGeneratedSubtitleForReview
                && selectedAudio != null
                && !IsSpeechSubtitleGenerating
                && (HasGeneratedSubtitleForSelectedMode(selectedAudio.FullPath) || CanGenerateSpeechSubtitleFromStorage());

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
                        ? $"{GetGeneratedSubtitleDisplayName()}未就绪，无法生成复盘"
                        : SpeechSubtitleStatusMessage;
                }
                return;
            }

            EnqueueReviewSheetsForAudio(selectedAudio, _reviewSheets);
            StartBatchQueueRunner("复盘已加入队列");
        }

        private async Task<bool> EnsureSpeechSubtitleForReviewAsync(MediaFileItem audioFile)
        {
            if (!ShouldGenerateGeneratedSubtitleForReview)
            {
                return true;
            }

            var mode = GetReviewSubtitleSourceMode();
            var speechPath = GetGeneratedSubtitleOutputPath(audioFile.FullPath, mode);
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
                SpeechSubtitleStatusMessage = BuildSubtitleSourceUnavailableStatusText();
                return false;
            }

            _speechSubtitleCts?.Cancel();
            _speechSubtitleCts = new CancellationTokenSource();
            var token = _speechSubtitleCts.Token;

            IsSpeechSubtitleGenerating = true;
            SpeechSubtitleStatusMessage = $"{GetGeneratedSubtitleDisplayName()}生成中...";

            try
            {
                var success = await GenerateBatchSpeechSubtitleForFileAsync(
                    audioFile.FullPath,
                    mode,
                    token,
                    status => SpeechSubtitleStatusMessage = status);
                if (!success)
                {
                    SpeechSubtitleStatusMessage = "未识别到有效文本";
                    return false;
                }

                SpeechSubtitleStatusMessage = $"{GetGeneratedSubtitleDisplayName()}已生成: {Path.GetFileName(speechPath)}";
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
                SpeechSubtitleStatusMessage = $"{GetGeneratedSubtitleDisplayName()}生成已取消";
                return false;
            }
            catch (Exception ex)
            {
                SpeechSubtitleStatusMessage = $"{GetGeneratedSubtitleDisplayName()}生成失败: {ex.Message}";
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
            if (!TryBuildReviewRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                sheet.StatusMessage = string.IsNullOrWhiteSpace(errorMessage)
                    ? "AI 配置无效，请先配置 AI 服务"
                    : errorMessage;
                return;
            }

            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    endpoint != null ? GetEndpointProfileKey(endpoint) : "ai",
                    runtimeRequest.AzureTenantId,
                    runtimeRequest.AzureClientId);

                if (tokenProvider == null)
                {
                    sheet.StatusMessage = "AAD 登录已失效，请先在设置中重新登录当前复盘模型对应终结点。";
                    return;
                }
            }
            var runtimeInsightService = new AiInsightService(tokenProvider);

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

            var systemPrompt = AiConfig.GetEffectiveReviewSystemPrompt(config.AiConfig?.ReviewSystemPrompt);
            var subtitlesText = FormatSubtitleForSummary(cues);
            var prompt = string.IsNullOrWhiteSpace(sheet.Prompt)
                ? "请生成复盘总结。"
                : sheet.Prompt.Trim();
            var userTemplate = config.AiConfig?.ReviewUserContentTemplate;
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
                await runtimeInsightService.StreamChatAsync(
                    runtimeRequest,
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
                    enableReasoning: runtimeRequest.SummaryEnableReasoning,
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
                else if (runtimeRequest.SummaryEnableReasoning)
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

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        private static ModelReference? SelectReviewReference(AiConfig ai)
            => ai.ReviewModelRef ?? ai.SummaryModelRef ?? ai.QuickModelRef ?? ai.InsightModelRef;

        private bool TryBuildReviewRuntimeConfig(
            AzureSpeechConfig config,
            out AiChatRequestConfig runtimeRequest,
            out AiEndpoint? endpoint,
            out string errorMessage)
        {
            runtimeRequest = new AiChatRequestConfig();
            endpoint = null;
            errorMessage = "";

            var ai = config.AiConfig;
            if (ai == null)
            {
                errorMessage = "AI 配置不存在，请先在设置中选择复盘模型。";
                return false;
            }

            var reference = SelectReviewReference(ai);
            if (_modelRuntimeResolver.TryResolve(config, reference, ModelCapability.Text, out var runtime, out var resolveError)
                && runtime != null)
            {
                endpoint = runtime.Endpoint;
                runtimeRequest = runtime.CreateChatRequest(ai.SummaryEnableReasoning);
                return true;
            }

            errorMessage = resolveError;
            return false;
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
            var enableSpeech = ShouldGenerateGeneratedSubtitleForReview;

            if (!enableReview && !enableSpeech)
            {
                BatchStatusMessage = "未启用所选字幕来源或复盘生成";
                return;
            }

            if (enableSpeech && !CanGenerateSpeechSubtitleFromStorage())
            {
                BatchStatusMessage = BuildSubtitleSourceUnavailableStatusText();
                return;
            }

            if (!enableSpeech)
            {
                var subtitlePath = GetSubtitlePathForReview(target.FullPath);
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

            var reviewSheets = batchSheets.ToList();
            SetReviewSheetSnapshot(batchItem, reviewSheets);

            EnsureBatchLogFile();

            _batchPackageStateService.SetRemoved(batchItem.FullPath, false);

            PrepareAndEnqueueSingleItem(batchItem, reviewSheets, enableSpeech, enableReview,
                config.ContextMenuForceRegeneration);

            if (ShouldWriteBatchLogSuccess)
            {
                AppendBatchLog("QueueStart", batchItem.FileName, "Success", "右键队列启动");
            }

            UpdateBatchQueueStatusText();
            StartBatchQueueRunner("已加入队列");
            RefreshPackageProjections();
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
            return GetReviewSubtitleSourceMode(config) switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => TryResolveActiveMicrosoftSpeechResource(config, SpeechCapability.RealtimeSpeechToText, out _, out _)
                                                           && !string.IsNullOrWhiteSpace(config.SourceLanguage),
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => TryResolveActiveAiSpeechRuntime(config, SpeechCapability.RealtimeSpeechToText, out _, out _),
                _ => false
            };
        }

        private async void GenerateSpeechSubtitle()
        {
            if (!CanGenerateSpeechSubtitle())
            {
                SpeechSubtitleStatusMessage = BuildSubtitleSourceUnavailableStatusText();
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
            SpeechSubtitleStatusMessage = $"正在生成{GetGeneratedSubtitleDisplayName()}...";

            try
            {
                var cues = await TranscribeSpeechToCuesAsync(audioFile.FullPath, token);
                if (cues.Count == 0)
                {
                    SpeechSubtitleStatusMessage = "未识别到有效文本";
                    return;
                }

                var outputPath = GetGeneratedSubtitleOutputPath(audioFile.FullPath, GetReviewSubtitleSourceMode());
                BlobStorageService.WriteVttFile(outputPath, cues);

                SpeechSubtitleStatusMessage = $"{GetGeneratedSubtitleDisplayName()}已生成: {Path.GetFileName(outputPath)}";
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

            if (!ShouldGenerateGeneratedSubtitleForReview)
            {
                return false;
            }

            return CanGenerateSpeechSubtitleFromStorage();
        }

        private async void GenerateBatchSpeechSubtitle()
        {
            if (!CanGenerateBatchSpeechSubtitle())
            {
                SpeechSubtitleStatusMessage = BuildSubtitleSourceUnavailableStatusText();
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
                    GetReviewSubtitleSourceMode(),
                    token,
                    status => SpeechSubtitleStatusMessage = status);

                if (!success)
                {
                    SpeechSubtitleStatusMessage = "未识别到有效文本";
                    return;
                }

                var outputPath = GetGeneratedSubtitleOutputPath(audioFile.FullPath, GetReviewSubtitleSourceMode());
                SpeechSubtitleStatusMessage = $"{GetGeneratedSubtitleDisplayName()}已生成: {Path.GetFileName(outputPath)}";
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
            if (GetReviewSubtitleSourceMode(config) == ReviewSubtitleSourceMode.AiTranscriptionSubtitle)
            {
                if (!TryResolveActiveAiSpeechRuntime(config, SpeechCapability.RealtimeSpeechToText, out var runtime, out var errorMessage)
                    || runtime == null)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage)
                        ? "未配置可用的实时 AI 转写模型"
                        : errorMessage);
                }

                return TranscribeSpeechToCuesByAiAsync(audioPath, runtime, config.SourceLanguage, token);
            }

            if (!TryResolveActiveMicrosoftSpeechResource(config, SpeechCapability.RealtimeSpeechToText, out var subscription, out var subscriptionError)
                || subscription == null)
            {
                throw new InvalidOperationException(subscriptionError);
            }

            return RealtimeSpeechTranscriber.TranscribeSpeechToCuesAsync(
                audioPath, subscription, config.SourceLanguage, token);
        }

        private async Task<bool> GenerateBatchSpeechSubtitleForFileAsync(
            string audioPath,
            ReviewSubtitleSourceMode subtitleMode,
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
            if (subtitleMode == ReviewSubtitleSourceMode.AiTranscriptionSubtitle)
            {
                if (!TryResolveActiveAiSpeechRuntime(config, SpeechCapability.BatchSpeechToText, out var aiRuntime, out var aiResolveMessage)
                    || aiRuntime == null)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(aiResolveMessage)
                        ? "未配置可用的 AI 批量转写模型"
                        : aiResolveMessage);
                }

                onStatus?.Invoke("批量转写：调用 AI 语音转写模型...");
                var transcription = await _aiAudioTranscriptionService.TranscribeAsync(
                    aiRuntime,
                    audioPath,
                    config.SourceLanguage,
                    GetBatchSubtitleSplitOptions(),
                    token);

                if (transcription.Cues.Count == 0)
                {
                    return false;
                }

                var outputPath = FileLibraryViewModel.GetAiSubtitlePath(audioPath);
                BlobStorageService.WriteVttFile(outputPath, transcription.Cues);
                return true;
            }

            if (!TryResolveActiveMicrosoftSpeechResource(config, SpeechCapability.BatchSpeechToText, out var subscription, out var subscriptionError)
                || subscription == null)
            {
                throw new InvalidOperationException(subscriptionError);
            }

            if (!config.BatchStorageIsValid || string.IsNullOrWhiteSpace(config.BatchStorageConnectionString))
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
                var normalizedLocale = RealtimeSpeechTranscriber.GetTranscriptionSourceLanguage(config.SourceLanguage);
                Action<string, string>? batchLog = ShouldWriteBatchLogSuccess
                    ? (evt, msg) => AppendBatchLog(evt, audioFileName, "Success", msg)
                    : null;
                List<SubtitleCue> cues;
                string transcriptionJson;

                try
                {
                    (cues, transcriptionJson) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                        contentUrl,
                        normalizedLocale,
                        subscription,
                        token,
                        status => onStatus?.Invoke(status),
                        splitOptions,
                        batchLog);
                }
                catch (Exception ex) when (IsOfflineTranscriptionLocaleUnsupported(ex))
                {
                    const string fallbackLocale = "zh-CN";
                    if (string.Equals(normalizedLocale, fallbackLocale, StringComparison.OrdinalIgnoreCase))
                    {
                        throw;
                    }

                    onStatus?.Invoke($"批量转写不支持语言 {normalizedLocale}，改用批量兜底 locale={fallbackLocale} 重试...");
                    if (ShouldWriteBatchLogFailure)
                    {
                        AppendBatchLog("TranscribeBatchFallback", audioFileName, "Failed",
                            $"mode=batch-retry locale={normalizedLocale} fallback={fallbackLocale} detail={ex.Message}");
                    }

                    (cues, transcriptionJson) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                        contentUrl,
                        fallbackLocale,
                        subscription,
                        token,
                        status => onStatus?.Invoke(status),
                        splitOptions,
                        batchLog);
                }

                if (ShouldWriteBatchLogSuccess)
                {
                    AppendBatchLog("TranscribeResult", audioFileName, "Success", $"cues={cues.Count}");
                }

                if (cues.Count == 0)
                {
                    return false;
                }

                var outputPath = GetGeneratedSubtitleOutputPath(audioPath, subtitleMode);
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

        private async Task<List<SubtitleCue>> TranscribeSpeechToCuesByAiAsync(
            string audioPath,
            ModelRuntimeResolution runtime,
            string sourceLanguage,
            CancellationToken token)
        {
            var result = await _aiAudioTranscriptionService.TranscribeAsync(
                runtime,
                audioPath,
                sourceLanguage,
                GetBatchSubtitleSplitOptions(),
                token);
            return result.Cues;
        }

        private ReviewSubtitleSourceMode GetReviewSubtitleSourceMode(AzureSpeechConfig? config = null)
            => (config ?? _configProvider()).GetEffectiveReviewSubtitleSourceMode();

        private bool HasGeneratedSubtitleForSelectedMode(string audioPath)
        {
            return GetReviewSubtitleSourceMode() switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => FileLibraryViewModel.HasSpeechSubtitle(audioPath),
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => FileLibraryViewModel.HasAiSubtitle(audioPath),
                _ => false
            };
        }

        private string GetGeneratedSubtitleDisplayName(AzureSpeechConfig? config = null)
        {
            return GetReviewSubtitleSourceMode(config) switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => "Speech 字幕",
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => "AI 转写字幕",
                _ => "字幕"
            };
        }

        private string GetSubtitlePathForReview(string audioPath)
            => FileLibraryViewModel.GetPreferredSubtitlePath(audioPath, GetReviewSubtitleSourceMode()) ?? string.Empty;

        private static string GetGeneratedSubtitleOutputPath(string audioPath, ReviewSubtitleSourceMode mode)
        {
            return mode switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => FileLibraryViewModel.GetSpeechSubtitlePath(audioPath),
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => FileLibraryViewModel.GetAiSubtitlePath(audioPath),
                _ => FileLibraryViewModel.GetPreferredSubtitlePath(audioPath) ?? audioPath
            };
        }

        private bool HasAvailableConfiguredSubtitlePath(AzureSpeechConfig config)
        {
            return GetReviewSubtitleSourceMode(config) switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => CanGenerateSpeechSubtitleWithAzureSpeech(config),
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => HasAvailableBatchTranscriptionModel(config, out _),
                _ => true
            };
        }

        private bool CanGenerateSpeechSubtitleWithAzureSpeech(AzureSpeechConfig config)
        {
            return config.BatchStorageIsValid
                   && !string.IsNullOrWhiteSpace(config.BatchStorageConnectionString)
                   && TryResolveActiveMicrosoftSpeechResource(config, SpeechCapability.BatchSpeechToText, out _, out _)
                   && !string.IsNullOrWhiteSpace(config.SourceLanguage);
        }

        private bool HasAvailableBatchTranscriptionModel(AzureSpeechConfig config, out string errorMessage)
            => TryResolveActiveAiSpeechRuntime(config, SpeechCapability.BatchSpeechToText, out _, out errorMessage);

        private string BuildSubtitleSourceEnabledStatusText()
        {
            var config = _configProvider();
            return GetReviewSubtitleSourceMode(config) switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => "当前使用 Speech 字幕模式：Azure Speech 批量链路可用。",
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => "当前使用 AI 转写字幕模式：AI 批量转写模型可用。",
                _ => "当前使用普通字幕模式：直接读取现有 .srt / .vtt。"
            };
        }

        private string BuildSubtitleSourceUnavailableStatusText()
        {
            var config = _configProvider();
            return GetReviewSubtitleSourceMode(config) switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => "当前选中了 Speech 字幕模式，但缺少有效的存储账号或语音订阅。",
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => "当前选中了 AI 转写字幕模式，但还没有配置可用的批量语音转写模型。",
                _ => "当前使用普通字幕模式，无需额外生成字幕。"
            };
        }

        private bool TryResolveSpeechRuntime(
            AzureSpeechConfig config,
            ModelReference? reference,
            ModelCapability capability,
            out ModelRuntimeResolution? runtime,
            out string errorMessage)
        {
            runtime = null;
            errorMessage = string.Empty;

            if (reference == null)
            {
                return false;
            }

            return _modelRuntimeResolver.TryResolve(config, reference, capability, out runtime, out errorMessage);
        }

        private bool TryResolveActiveAiSpeechRuntime(
            AzureSpeechConfig config,
            SpeechCapability capability,
            out ModelRuntimeResolution? runtime,
            out string errorMessage)
        {
            runtime = null;
            if (!_speechResourceRuntimeResolver.TryResolveActive(config, capability, out var resolution, out errorMessage)
                || resolution == null)
            {
                return false;
            }

            if (!resolution.IsAiSpeech || resolution.AiRuntime == null)
            {
                errorMessage = $"当前语音资源“{resolution.Resource.Name}”不是可用的 AI 语音连接器。";
                return false;
            }

            runtime = resolution.AiRuntime;
            return true;
        }

        private bool TryResolveActiveMicrosoftSpeechResource(
            AzureSpeechConfig config,
            SpeechCapability capability,
            out AzureSubscription? subscription,
            out string errorMessage)
        {
            subscription = null;
            if (!_speechResourceRuntimeResolver.TryResolveActive(config, capability, out var resolution, out errorMessage)
                || resolution == null)
            {
                return false;
            }

            if (!resolution.IsMicrosoftSpeech || resolution.MicrosoftSubscription == null)
            {
                errorMessage = $"当前语音资源“{resolution.Resource.Name}”不是可用的 Microsoft Speech 连接器。";
                return false;
            }

            subscription = resolution.MicrosoftSubscription;
            return true;
        }

        private static bool IsOfflineTranscriptionLocaleUnsupported(Exception ex)
        {
            var message = ex.Message ?? string.Empty;
            return message.Contains("does not support offline transcription", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("offline transcription", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("不支持离线转写", StringComparison.OrdinalIgnoreCase);
        }
    }
}
