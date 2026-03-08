using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using TrueFluentPro.Services.EndpointTesting;

namespace TrueFluentPro.ViewModels.EndpointTesting;

public sealed class EndpointBatchTestDialogViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<int, EndpointBatchTestItemViewModel> _itemLookup = new();
    private string _endpointName;
    private string _summaryText;
    private string _generatedAtText;
    private string _statusText;
    private bool _isRunning;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _completedAt;
    private EndpointBatchTestProgressSnapshot? _latestSnapshot;

    public EndpointBatchTestDialogViewModel(string endpointName)
    {
        _endpointName = string.IsNullOrWhiteSpace(endpointName) ? "当前终结点" : endpointName;
        _summaryText = "正在准备测试项...";
        _generatedAtText = string.Empty;
        _statusText = "等待开始";
        Items = new ObservableCollection<EndpointBatchTestItemViewModel>();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshClockText());
        RefreshClockText();
    }

    public string WindowTitle => $"终结点实时测试 · {_endpointName}";
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }
    public string GeneratedAtText { get => _generatedAtText; private set => SetProperty(ref _generatedAtText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanClose));
            }
        }
    }

    public bool CanCancel => IsRunning;
    public bool CanClose => !IsRunning;
    public ObservableCollection<EndpointBatchTestItemViewModel> Items { get; }

    public void MarkStarted()
    {
        _startedAt = DateTimeOffset.Now;
        _completedAt = null;
        IsRunning = true;
        StatusText = "测试进行中";
        SummaryText = "正在收集当前选中终结点的模型测试项...";
        _refreshTimer.Start();
        RefreshClockText();
    }

    public void MarkFinished()
    {
        if (_completedAt == null)
        {
            _completedAt = DateTimeOffset.Now;
        }

        IsRunning = false;
        StatusText = "测试已完成";
        _refreshTimer.Stop();
        RefreshClockText();
    }

    public void MarkCanceled()
    {
        _completedAt = DateTimeOffset.Now;
        IsRunning = false;
        StatusText = "测试已取消";
        SummaryText = string.IsNullOrWhiteSpace(SummaryText)
            ? "测试已取消。"
            : $"{SummaryText} · 已取消";
        _refreshTimer.Stop();
        RefreshClockText();
    }

    public void MarkFailed(Exception ex)
    {
        _completedAt = DateTimeOffset.Now;
        IsRunning = false;
        StatusText = "测试异常结束";
        SummaryText = "测试未能正常完成。";

        var item = new EndpointBatchTestProgressItem
        {
            Order = -1,
            EndpointId = string.Empty,
            EndpointName = _endpointName,
            EndpointTypeName = "整体",
            CapabilityName = "整体",
            ModelId = string.Empty,
            State = EndpointBatchTestLiveState.Failed,
            Summary = "测试过程发生未处理异常。",
            Details = ex.Message,
            RequestSummary = string.Empty,
            Duration = TimeSpan.Zero
        };

        ApplySnapshot(new EndpointBatchTestProgressSnapshot
        {
            StartedAt = _startedAt == default ? DateTimeOffset.Now : _startedAt,
            CompletedAt = _completedAt,
            EndpointId = string.Empty,
            EndpointName = _endpointName,
            IsCompleted = true,
            Items = new[] { item }
        });

        _refreshTimer.Stop();
        RefreshClockText();
    }

    public IProgress<EndpointBatchTestProgressSnapshot> CreateProgressReporter()
    {
        return new Progress<EndpointBatchTestProgressSnapshot>(snapshot =>
        {
            Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
        });
    }

    public void ApplySnapshot(EndpointBatchTestProgressSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        _startedAt = snapshot.StartedAt;
        _completedAt = snapshot.CompletedAt;
        _endpointName = string.IsNullOrWhiteSpace(snapshot.EndpointName) ? _endpointName : snapshot.EndpointName;
        OnPropertyChanged(nameof(WindowTitle));

        foreach (var item in snapshot.Items.OrderBy(item => item.Order))
        {
            if (_itemLookup.TryGetValue(item.Order, out var existing))
            {
                existing.Apply(item);
                continue;
            }

            var vm = new EndpointBatchTestItemViewModel(item);
            _itemLookup[item.Order] = vm;
        }

        Items.Clear();
        foreach (var item in snapshot.Items.OrderBy(item => item.Order))
        {
            if (_itemLookup.TryGetValue(item.Order, out var vm))
            {
                Items.Add(vm);
            }
        }

        SummaryText = $"共 {snapshot.TotalCount} 项 · 排队 {snapshot.PendingCount} · 进行中 {snapshot.RunningCount} · 成功 {snapshot.SuccessCount} · 失败 {snapshot.FailedCount} · 跳过 {snapshot.SkippedCount}";
        StatusText = snapshot.IsCompleted ? "测试已完成" : snapshot.RunningCount > 0 ? "测试进行中" : "等待执行";

        if (snapshot.IsCompleted)
        {
            IsRunning = false;
            _refreshTimer.Stop();
        }

        RefreshClockText();
    }

    private void RefreshClockText()
    {
        var effectiveStart = _startedAt == default ? DateTimeOffset.Now : _startedAt;
        var effectiveEnd = _completedAt ?? DateTimeOffset.Now;
        var elapsed = effectiveEnd - effectiveStart;

        GeneratedAtText = $"开始时间：{effectiveStart:yyyy-MM-dd HH:mm:ss} · 已运行 {elapsed:mm\\:ss} · {StatusText} · 界面每 1 秒刷新一次";
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
    }

    public void ToggleExpanded(EndpointBatchTestItemViewModel target)
    {
        if (target == null)
            return;

        var shouldExpand = !target.IsExpanded;
        foreach (var item in Items)
        {
            item.IsExpanded = shouldExpand && ReferenceEquals(item, target);
        }
    }
}

public sealed class EndpointBatchTestItemViewModel : ViewModelBase
{
    private string _statusText = string.Empty;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _summary = string.Empty;
    private string _requestUrlText = string.Empty;
    private string _requestSummary = string.Empty;
    private string _details = string.Empty;
    private string _durationText = string.Empty;
    private string _statusForeground = "#107C10";
    private string _borderBrush = "#33107C10";
    private string _cardBackground = "#08107C10";
    private string _detailBackground = "#06107C10";
    private bool _isExpanded;

    public EndpointBatchTestItemViewModel(EndpointBatchTestProgressItem item)
    {
        Apply(item);
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string RequestUrlText { get => _requestUrlText; private set => SetProperty(ref _requestUrlText, value); }
    public string RequestSummary { get => _requestSummary; private set => SetProperty(ref _requestSummary, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }
    public string StatusForeground { get => _statusForeground; private set => SetProperty(ref _statusForeground, value); }
    public string BorderBrush { get => _borderBrush; private set => SetProperty(ref _borderBrush, value); }
    public string CardBackground { get => _cardBackground; private set => SetProperty(ref _cardBackground, value); }
    public string DetailBackground { get => _detailBackground; private set => SetProperty(ref _detailBackground, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public string ExpandGlyph => IsExpanded ? "▼" : "▶";
    public bool HasRequestUrl => !string.IsNullOrWhiteSpace(RequestUrlText);
    public bool HasRequestSummary => !string.IsNullOrWhiteSpace(RequestSummary);
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool HasDuration => !string.IsNullOrWhiteSpace(DurationText);

    public void Apply(EndpointBatchTestProgressItem item)
    {
        StatusText = item.State switch
        {
            EndpointBatchTestLiveState.Pending => "排队中",
            EndpointBatchTestLiveState.Running => "测试中",
            EndpointBatchTestLiveState.Success => "通过",
            EndpointBatchTestLiveState.Failed => "失败",
            EndpointBatchTestLiveState.Skipped => "跳过",
            EndpointBatchTestLiveState.Canceled => "已取消",
            _ => "未知"
        };

        Title = string.IsNullOrWhiteSpace(item.CapabilityName)
            ? item.EndpointName
            : $"{item.EndpointName} · {item.CapabilityName}";
        Subtitle = string.IsNullOrWhiteSpace(item.ModelId)
            ? item.EndpointTypeName
            : $"{item.EndpointTypeName} · 模型 {item.ModelId}";
        Summary = item.Summary;
        RequestUrlText = item.RequestUrlText;
        RequestSummary = item.RequestSummary;
        Details = item.Details;
        DurationText = item.Duration > TimeSpan.Zero
            ? $"耗时 {item.Duration.TotalSeconds:F1}s"
            : string.Empty;

        (StatusForeground, BorderBrush, CardBackground, DetailBackground) = item.State switch
        {
            EndpointBatchTestLiveState.Pending => ("#8A8886", "#338A8886", "#068A8886", "#068A8886"),
            EndpointBatchTestLiveState.Running => ("#005FB8", "#66005FB8", "#10005FB8", "#0A005FB8"),
            EndpointBatchTestLiveState.Success => ("#107C10", "#33107C10", "#08107C10", "#06107C10"),
            EndpointBatchTestLiveState.Failed => ("#D13438", "#66D13438", "#10D13438", "#0FD13438"),
            EndpointBatchTestLiveState.Canceled => ("#CA5010", "#66CA5010", "#10CA5010", "#0FCA5010"),
            _ => ("#8A8886", "#338A8886", "#068A8886", "#068A8886")
        };

        OnPropertyChanged(nameof(HasRequestUrl));
        OnPropertyChanged(nameof(HasRequestSummary));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(HasDuration));
        OnPropertyChanged(nameof(ExpandGlyph));
    }
}
