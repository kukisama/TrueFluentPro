using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Services;

namespace TrueFluentPro.Views;

/// <summary>独立只读用量窗口；可由主 UI 或原生浮条启动的 Avalonia 入口直接 Show。</summary>
public sealed class VoiceImeUsageWindow : Window
{
    private static readonly IBrush Ink = Brush("#132B49");
    private static readonly IBrush Muted = Brush("#62758B");
    private static readonly IBrush Blue = Brush("#246BEB");
    private static readonly IBrush Line = Brush("#E3EAF3");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private readonly VoiceImeUsageReportService _service;
    private readonly Button _refresh = new() { Content = "刷新报表", Name = "UsageRefresh" };
    private readonly Button[] _ranges;
    private readonly TextBlock _rangeCaption = Text("", 13, Muted);
    private readonly TextBlock _statusText = Text("正在读取本地统计…", 14, Ink);
    private readonly Border _status;
    private readonly StackPanel _reportContent = new() { Spacing = 18, IsVisible = false };
    private readonly TextBlock _updated = Text("只读查询 · 不影响正在进行的语音输入", 12, Muted);
    private CancellationTokenSource? _loadCts;
    private int _days = 7;
    private long _request;
    private bool _closed;
    private CancellationTokenSource? _textCts;
    private long _textRequest;
    private string? _selectedSessionId;
    private long _textNextSequence;
    private ListBox? _sessionList;
    private Border? _textArea;
    private StackPanel? _textRows;
    private TextBlock? _textStatus;
    private TextBlock? _textCaption;
    private Button? _viewText;
    private Button? _nextText;
    private Button? _cancelText;

    public VoiceImeUsageWindow() : this(new VoiceImeUsageReportService()) { }

    // 隔离 fixture 使用自己的数据库；生产入口始终保留无参构造。
    internal VoiceImeUsageWindow(VoiceImeUsageReportService service)
    {
        _service = service;
        Title = "译见pro · 语音输入用量";
        Width = 1040;
        Height = 820;
        MinWidth = 760;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Light;
        Background = Brush("#F3F7FC");
        FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, sans-serif");
        _updated.TextWrapping = TextWrapping.Wrap;

        Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters =
            {
                new Setter(Button.MinHeightProperty, 38d),
                new Setter(Button.PaddingProperty, new Thickness(16, 8)),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });

        var titleStack = new StackPanel { Spacing = 8 };
        titleStack.Children.Add(Text("VOICE INPUT  /  本地用量", 12, Brush("#98BAE6")));
        titleStack.Children.Add(Text("让每一次表达，都看得见", 27, Brushes.White, FontWeight.SemiBold));
        titleStack.Children.Add(Text("语音输入统计  ·  识别定稿产出，不代表成功插入字数或费用", 13, Brush("#CDDCF0")));
        var header = new Border
        {
            Background = Ink, Padding = new Thickness(28, 22), Child = titleStack
        };

        _ranges = new[] { RangeButton("今日", 1), RangeButton("7 天", 7), RangeButton("30 天", 30) };
        var rangeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var button in _ranges) rangeButtons.Children.Add(button);
        SelectRangeVisuals();
        _refresh.Background = Blue;
        _refresh.Foreground = Brushes.White;
        _refresh.Click += async (_, _) => await RefreshAsync();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        actions.Children.Add(rangeButtons);
        actions.Children.Add(_refresh);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        _rangeCaption.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_rangeCaption);
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);

        _statusText.TextWrapping = TextWrapping.Wrap;
        _status = new Border
        {
            Background = Brush("#E7F0FE"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18), Child = _statusText
        };
        var page = new StackPanel { Margin = new Thickness(28, 22), Spacing = 18 };
        page.Children.Add(toolbar);
        page.Children.Add(_status);
        page.Children.Add(_reportContent);
        var scroll = new ScrollViewer
        {
            Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var footer = new Border
        {
            Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(28, 13), Child = _updated
        };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(header);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _loadCts?.Cancel(); InvalidateSessionText(); };
    }

    private Button RangeButton(string label, int days)
    {
        var button = new Button { Content = label, Tag = days, Name = $"UsageRange{days}" };
        button.Click += async (_, _) =>
        {
            if (_days == days) return;
            _days = days;
            SelectRangeVisuals();
            await RefreshAsync();
        };
        return button;
    }

    private void SelectRangeVisuals()
    {
        foreach (var button in _ranges)
        {
            var selected = Equals(button.Tag, _days);
            button.Background = selected ? Brush("#DFEBFF") : Brushes.White;
            button.Foreground = selected ? Blue : Muted;
            button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
            AutomationProperties.SetName(button, $"{button.Content}{(selected ? "，已选中" : "")}");
        }
    }

    private async Task RefreshAsync()
    {
        if (_closed) return;
        InvalidateSessionText();
        _selectedSessionId = null;
        _textRows?.Children.Clear();
        if (_textArea != null) _textArea.IsVisible = false;
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var request = ++_request;
        _refresh.IsEnabled = false;
        _reportContent.IsVisible = false; // 切换范围期间不把旧范围数据标成新范围。
        _rangeCaption.Text = _days == 1 ? "今日 · 本地自然日" : $"最近 {_days} 天 · 含今日";
        SetStatus("正在读取本地统计…", false);
        try
        {
            var report = await _service.ReadAsync(_days, cts.Token);
            if (_closed || request != _request || cts.IsCancellationRequested) return;
            ShowReport(report);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closed || request != _request) return;
            var message = ex is SqliteException { SqliteErrorCode: 5 or 6 }
                ? "统计数据库暂时繁忙，请稍后点击“刷新报表”。语音输入不受影响。"
                : "暂时无法读取本地统计，请稍后刷新；若持续出现，请在主程序中检查数据存储权限与版本。";
            SetStatus(message, true); // 不显示路径、SQL、原始异常或正文。
            _updated.Text = "读取失败 · 未修改数据，也未中断语音会话";
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            if (!_closed && request == _request) _refresh.IsEnabled = true;
        }
    }

    private void SetStatus(string message, bool error)
    {
        _status.IsVisible = true;
        _status.Background = Brush(error ? "#FFF1EC" : "#E7F0FE");
        _statusText.Foreground = error ? Brush("#A54429") : Ink;
        _statusText.Text = message;
    }

    private void ShowReport(VoiceImeUsageReport report)
    {
        _rangeCaption.Text = $"{report.StartDate:yyyy.MM.dd} — {report.EndDate:yyyy.MM.dd}";
        _updated.Text = $"更新于 {report.CapturedAt:HH:mm:ss} · 汇总不读取正文 · 原文仅在点击查看时读取";
        _reportContent.Children.Clear();
        if (report.Availability != VoiceImeUsageReportAvailability.Ready)
        {
            SetStatus(report.Availability == VoiceImeUsageReportAvailability.DatabaseMissing
                ? "还没有本地用量数据库。使用语音输入后，点击刷新即可查看；报表不会主动创建数据库。"
                : "当前数据库尚未包含输入法用量表。请使用支持用量统计的输入法版本后刷新；报表不会迁移数据库。", false);
            return;
        }

        _status.IsVisible = report.MatchingSessions == 0 && report.Utterances == 0;
        if (_status.IsVisible) SetStatus("这段时间还没有语音输入记录。开始一次语音输入，让表达留下刻度。", false);

        var metrics = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 12 };
        AddMetric(0, "识别定稿字数", Number(report.Characters), "字", "按定稿当天统计", "UsageCharacters");
        AddMetric(1, "录音时长", Duration(report.RecordingMilliseconds), "", "按会话创建日归属", "UsageRecordingTime");
        AddMetric(2, "启动会话", Number(report.SessionCount), "次", "含未开始录音的连接", "UsageSessions");
        AddMetric(3, "识别定稿句数", Number(report.Utterances), "句", "不含临时识别结果", "UsageUtterances");
        _reportContent.Children.Add(metrics);
        if (report.PreviousPeriod is { } previous)
        {
            var comparisonCaption = Wrapped($"对比上期 {previous.StartDate:yyyy.MM.dd} — {previous.EndDate:yyyy.MM.dd} · 等长完整自然日；本期含今日截至快照的记录，非同期时刻对比。", 11, Muted);
            comparisonCaption.Name = "UsageComparisonCaption";
            _reportContent.Children.Add(comparisonCaption);
        }
        _reportContent.Children.Add(BuildChart(report));
        _reportContent.Children.Add(BuildHours(report));
        var distributions = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
        distributions.Children.Add(BuildDistribution("表达的长度", "所选日期定稿 · 按保存的字素数分组",
            report.SentenceLengths, report.Utterances, "句", "UsageLengthDistribution"));
        var outcomes = BuildDistribution("会话如何结束", "按创建日 · 未完成与异常中断独立保留",
            report.Outcomes, report.SessionCount, "次", "UsageOutcomeDistribution");
        Grid.SetColumn(outcomes, 1);
        distributions.Children.Add(outcomes);
        _reportContent.Children.Add(distributions);
        _reportContent.Children.Add(BuildSessions(report));

        var definitions = new StackPanel { Spacing = 5 };
        definitions.Children.Add(Text("统计口径", 13, Ink, FontWeight.SemiBold));
        definitions.Children.Add(Wrapped("定稿包含识别确认的句子及手动停止收尾时保存的末句，不含临时识别结果。字数沿用保存的可见字素计数，不含空白和标点；不代表 TSF 或目标应用插入成功，也不用于费用计算。", 12, Muted));
        definitions.Children.Add(Wrapped("字数、句数使用定稿时记录的本地日期；整段录音时长和会话数归会话创建日，跨午夜不拆分，因此同一天的字数与时长不一定来自同一批会话。", 12, Muted));
        definitions.Children.Add(Wrapped("关闭原文保存仍会统计全部维度；汇总不读取正文，已保存的历史原文仍可在会话详情查看。所有分布和上期对比基于完整范围，不受 200 条会话明细上限影响。", 12, Muted));
        definitions.Children.Add(Wrapped("活跃日指有定稿字数（> 0）的本地日期；连续活跃段只在选区内接续，日均产出除以所选全部自然日（含零产出日）。小时按每条定稿保存的 UTC 偏移还原，0 字定稿单列句长。", 12, Muted));
        definitions.Children.Add(Wrapped("进行中或异常中断会话只显示已保存的检查点时长，不按当前时间补算。数据不会自动轮询，点击刷新查看最新快照。", 12, Muted));
        _reportContent.Children.Add(definitions);
        _reportContent.IsVisible = true;

        void AddMetric(int column, string title, string value, string unit, string note, string name)
        {
            var stack = new StackPanel { Spacing = 7 };
            stack.Children.Add(Wrapped(title, 12, Muted));
            var valueLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            var valueText = Text(value, 26, column == 0 ? Blue : Ink, FontWeight.SemiBold);
            valueText.Name = name;
            valueLine.Children.Add(valueText);
            valueLine.Children.Add(new TextBlock
            {
                Text = unit, FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(valueLine);
            stack.Children.Add(Wrapped(note, 11, Muted));
            if (report.PreviousPeriod is { } prior && column < 3)
            {
                var (current, baseline, baselineDisplay) = column switch
                {
                    0 => (report.Characters, prior.Characters, Number(prior.Characters) + " 字"),
                    1 => (report.RecordingMilliseconds, prior.RecordingMilliseconds, Duration(prior.RecordingMilliseconds)),
                    _ => (report.SessionCount, prior.Sessions, Number(prior.Sessions) + " 次")
                };
                var change = new VoiceImeUsageReportChange(current, baseline);
                var comparison = Wrapped(change.Label, 11, current > baseline ? Blue : Muted);
                comparison.Name = column switch
                {
                    0 => "UsageCharactersComparison", 1 => "UsageRecordingComparison", _ => "UsageSessionsComparison"
                };
                ToolTip.SetTip(comparison, $"上期 {prior.StartDate:yyyy-MM-dd} — {prior.EndDate:yyyy-MM-dd}：{baselineDisplay}\n" +
                    "口径与本期相同；上期为 0 时不计算增长率。");
                stack.Children.Add(comparison);
            }
            var card = Card(stack, 16);
            Grid.SetColumn(card, column);
            metrics.Children.Add(card);
        }
    }

    private static Border BuildChart(VoiceImeUsageReport report)
    {
        var stack = new StackPanel { Spacing = 16 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto") };
        heading.Children.Add(Text("每日定稿产出", 16, Ink, FontWeight.SemiBold));
        var peak = report.Days.Max(day => day.Characters);
        var peakLabel = Text($"最高 {Number(peak)} 字 / 天", 12, Muted);
        Grid.SetColumn(peakLabel, 1);
        heading.Children.Add(peakLabel);
        stack.Children.Add(heading);
        var activity = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 12 };
        var runs = report.ActiveRuns;
        AddActivity(0, "有字的日子", $"{report.ActiveDays} / {report.Days.Count} 天", "UsageActiveDays");
        AddActivity(1, "选区最长连续", $"{report.LongestActiveStreak} 天 · {runs.Count} 段", "UsageActiveStreak");
        AddActivity(2, "日均定稿产出", $"{report.DailyAverageCharacters.ToString("N1", Chinese)} 字", "UsageDailyAverage");
        ToolTip.SetTip(activity, runs.Count == 0 ? "选区内暂无有字的活跃日" : "选区内全部连续活跃段：\n" +
            string.Join("\n", runs.Select(run => $"{run.StartDate:MM/dd} — {run.EndDate:MM/dd} · {run.Days} 天")));
        stack.Children.Add(new Border
        {
            Background = Brush("#F3F7FC"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12), Child = activity
        });
        var columns = new UniformGrid { Columns = report.Days.Count, Name = "UsageDailyChart" };
        for (var index = 0; index < report.Days.Count; index++)
        {
            var day = report.Days[index];
            var cell = new Grid { RowDefinitions = new("20,132,26"), Margin = new Thickness(3, 0) };
            var showLabel = report.Days.Count <= 7 || index % 5 == 0 || index == report.Days.Count - 1;
            if (report.Days.Count <= 7)
                cell.Children.Add(new TextBlock
                {
                    Text = Number(day.Characters), FontSize = 11, Foreground = day.Characters > 0 ? Blue : Muted,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            var track = new Border
            {
                Background = Brush("#F1F5FB"), CornerRadius = new CornerRadius(5, 5, 0, 0),
                Margin = new Thickness(report.Days.Count <= 7 ? 12 : 1, 0)
            };
            var bar = new Border
            {
                Background = day.Characters > 0 ? Blue : Brush("#DCE5F0"),
                Height = peak == 0 ? 2 : Math.Max(2, 132d * day.Characters / peak),
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            track.Child = bar;
            Grid.SetRow(track, 1);
            cell.Children.Add(track);
            var label = new TextBlock
            {
                Text = showLabel ? day.Date.ToString("MM/dd", Chinese) : "", FontSize = 10,
                Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(label, 2);
            cell.Children.Add(label);
            var description = $"{day.Date:yyyy-MM-dd} · 定稿 {Number(day.Characters)} 字 / {Number(day.Utterances)} 句\n" +
                $"创建 {Number(day.Sessions)} 次会话 · 归属录音 {Duration(day.RecordingMilliseconds)}";
            ToolTip.SetTip(cell, description);
            AutomationProperties.SetName(cell, description);
            columns.Children.Add(cell);
        }
        stack.Children.Add(columns);
        stack.Children.Add(Text("按定稿时记录的本地日期 · 无记录日期以零展示 · 悬停查看每日数据", 11, Muted));
        return Card(stack);

        void AddActivity(int column, string title, string value, string name)
        {
            var item = new StackPanel { Spacing = 5 };
            item.Children.Add(Text(title, 11, Muted));
            var label = Wrapped(value, 16, Ink, FontWeight.SemiBold);
            label.Name = name;
            item.Children.Add(label);
            Grid.SetColumn(item, column);
            activity.Children.Add(item);
        }
    }

    private static Border BuildHours(VoiceImeUsageReport report)
    {
        var stack = new StackPanel { Spacing = 14 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var titles = new StackPanel { Spacing = 5 };
        titles.Children.Add(Text("一天里，何时表达最多", 16, Ink, FontWeight.SemiBold));
        titles.Children.Add(Wrapped("24 小时定稿分布 · 每条记录自己的本地时间", 11, Muted));
        heading.Children.Add(titles);
        var selector = new ComboBox
        {
            Name = "UsageHourlyMetric", ItemsSource = new[] { "按字数", "按句数" }, SelectedIndex = 0,
            Width = 112, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(selector, "小时分布统计口径");
        Grid.SetColumn(selector, 1);
        heading.Children.Add(selector);
        stack.Children.Add(heading);
        var chart = new UniformGrid { Columns = 24, Name = "UsageHourlyChart" };
        stack.Children.Add(chart);
        var summary = Text("", 11, Muted);
        summary.Name = "UsageHourlySummary";
        stack.Children.Add(summary);
        selector.SelectionChanged += (_, _) => RenderHours();
        RenderHours();
        return Card(stack);

        void RenderHours()
        {
            chart.Children.Clear();
            var bySentences = selector.SelectedIndex == 1;
            var peak = report.Hours.Select(hour => bySentences ? hour.Utterances : hour.Characters).DefaultIfEmpty().Max();
            foreach (var hour in report.Hours)
            {
                var value = bySentences ? hour.Utterances : hour.Characters;
                var cell = new Grid { RowDefinitions = new("104,24"), Margin = new Thickness(2, 0) };
                var track = new Border
                {
                    Background = Brush("#F1F5FB"), CornerRadius = new CornerRadius(4, 4, 0, 0),
                    Child = new Border
                    {
                        Height = peak == 0 ? 2 : Math.Max(2, 104d * value / peak),
                        Background = value > 0 ? Blue : Brush("#DCE5F0"),
                        VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(4, 4, 0, 0)
                    }
                };
                cell.Children.Add(track);
                var label = Text(hour.Hour % 3 == 0 || hour.Hour == 23 ? $"{hour.Hour:00}" : "", 10, Muted);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Bottom;
                Grid.SetRow(label, 1);
                cell.Children.Add(label);
                var description = $"{hour.Hour:00}:00–{hour.Hour:00}:59 · {Number(hour.Characters)} 字 / {Number(hour.Utterances)} 句";
                ToolTip.SetTip(cell, description);
                AutomationProperties.SetName(cell, description);
                chart.Children.Add(cell);
            }
            summary.Text = $"最高 {Number(peak)} {(bySentences ? "句" : "字")} / 小时 · 悬停同时查看字数与句数";
        }
    }

    private static Border BuildDistribution(string title, string note, IReadOnlyList<VoiceImeUsageReportBucket> buckets,
        long total, string unit, string name)
    {
        var stack = new StackPanel { Spacing = 12, Name = name };
        stack.Children.Add(Text(title, 16, Ink, FontWeight.SemiBold));
        stack.Children.Add(Wrapped(note, 11, Muted));
        foreach (var bucket in buckets)
        {
            // 标准六类结束原因总是显示，额外异常/未知及零字句仅在存在时显示。
            if (bucket.Count == 0 && bucket.Key is "zero" or "interrupted" or "other") continue;
            var row = new Grid { ColumnDefinitions = new("86,*,84"), ColumnSpacing = 8, MinHeight = 24 };
            var label = Text(bucket.Label, 12, Ink);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var meter = new ProgressBar
            {
                Minimum = 0, Maximum = Math.Max(1, total), Value = bucket.Count,
                Height = 6, MinHeight = 0, Foreground = Blue, Background = Brush("#EDF2FA"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(meter, 1);
            row.Children.Add(meter);
            var percentage = total == 0 ? 0 : (decimal)bucket.Count * 100 / total;
            var value = Text($"{Number(bucket.Count)} · {percentage:0}%", 11, Muted);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            var description = $"{bucket.Label}：{Number(bucket.Count)} {unit} / 共 {Number(total)} {unit}（{percentage:0.0}%）";
            ToolTip.SetTip(row, description);
            AutomationProperties.SetName(row, description);
            stack.Children.Add(row);
        }
        if (total == 0) stack.Children.Add(Text("这段时间暂无记录", 12, Muted));
        return Card(stack, 18);
    }

    private Border BuildSessions(VoiceImeUsageReport report)
    {
        var stack = new StackPanel { Spacing = 12 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto") };
        heading.Children.Add(Text("会话明细", 16, Ink, FontWeight.SemiBold));
        var count = Text(report.MatchingSessions > report.Sessions.Count
            ? $"最近 {report.Sessions.Count} / 共 {Number(report.MatchingSessions)} 条"
            : $"共 {Number(report.MatchingSessions)} 条", 12, Muted);
        Grid.SetColumn(count, 1);
        heading.Children.Add(count);
        stack.Children.Add(heading);
        _viewText = new Button { Content = "查看原文", Name = "UsageViewSessionText", IsEnabled = false,
            Background = Blue, Foreground = Brushes.White };
        _viewText.Click += async (_, _) => await LoadSessionTextAsync(0);
        var openRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        openRow.Children.Add(_viewText);
        var selectionHint = Text("选择会话后查看识别原文；选择本身不会读取正文", 11, Muted);
        selectionHint.VerticalAlignment = VerticalAlignment.Center;
        openRow.Children.Add(selectionHint);
        stack.Children.Add(openRow);
        stack.Children.Add(SessionRow("会话创建时间", "定稿字数", "定稿句数", "归属录音时长", "状态", true));
        if (report.Sessions.Count == 0)
            stack.Children.Add(new Border { Padding = new Thickness(12, 24), Child = Text("暂无会话明细", 13, Muted) });
        else
        {
            var list = new ListBox
            {
                Name = "UsageSessionList", ItemsSource = report.Sessions,
                MaxHeight = 330, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                ItemTemplate = new FuncDataTemplate<VoiceImeUsageReportSession>((session, _) =>
                {
                    if (session == null) return new Border();
                    var row = SessionRow(session.CreatedAt.ToString("MM-dd HH:mm:ss", Chinese),
                        Number(session.Characters), Number(session.Utterances),
                        session.DurationBelongsToRange ? Duration(session.RecordingMilliseconds) : "—（范围外）",
                        StateLabel(session), false);
                    ToolTip.SetTip(row, $"创建于 {session.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}\n" +
                        $"录音时长 {Duration(session.RecordingMilliseconds)} 全部归属 {session.CreatedLocalDate:yyyy-MM-dd}；字数和句数仅统计所选日期。");
                    return row;
                })
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            _sessionList = list;
            list.SelectionChanged += (_, _) => SelectSession(list.SelectedItem as VoiceImeUsageReportSession);
            stack.Children.Add(list);
        }
        stack.Children.Add(Wrapped("含范围内创建或有定稿的会话；跨日旧会话的录音时长标为范围外。列表截取不影响指标与每日趋势汇总。", 11, Muted));
        _textCaption = Wrapped("", 15, Ink, FontWeight.SemiBold);
        _textCaption.Name = "UsageSessionTextCaption";
        _textStatus = Wrapped("", 12, Muted);
        _textStatus.Name = "UsageSessionTextStatus";
        _textRows = new StackPanel { Spacing = 12, Name = "UsageSessionTextRows" };
        _nextText = new Button { Content = "下一批（最多 100 句）", Name = "UsageSessionTextNext", IsVisible = false };
        _nextText.Click += async (_, _) => await LoadSessionTextAsync(_textNextSequence);
        _cancelText = new Button { Content = "取消读取", Name = "UsageSessionTextCancel", IsVisible = false };
        _cancelText.Click += (_, _) =>
        {
            InvalidateSessionText();
            _textRows.Children.Clear();
            _textStatus.Text = "已取消读取。点击“查看原文”重新加载。";
            _viewText.IsEnabled = _selectedSessionId != null;
            _nextText.IsVisible = _cancelText.IsVisible = false;
        };
        var close = new Button { Content = "关闭详情", Name = "UsageSessionTextClose" };
        close.Click += (_, _) => { if (_sessionList != null) _sessionList.SelectedItem = null; };
        var detailActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        detailActions.Children.Add(_nextText);
        detailActions.Children.Add(_cancelText);
        detailActions.Children.Add(close);
        var detail = new StackPanel { Spacing = 12 };
        detail.Children.Add(_textCaption);
        detail.Children.Add(Wrapped("识别原文 · 会话全部日期，不限当前统计范围。翻译模式保存的也是翻译前的识别文字，不是译文，也不是目标应用实际输入全文。", 12, Muted));
        detail.Children.Add(_textStatus);
        detail.Children.Add(new ScrollViewer
        {
            Content = _textRows, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        detail.Children.Add(detailActions);
        _textArea = new Border
        {
            Child = detail, Padding = new Thickness(18), Background = Brush("#F3F7FC"),
            BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), IsVisible = false
        };
        stack.Children.Add(_textArea);
        return Card(stack);
    }

    private void InvalidateSessionText()
    {
        ++_textRequest;
        _textCts?.Cancel();
    }

    private void SelectSession(VoiceImeUsageReportSession? session)
    {
        InvalidateSessionText();
        _selectedSessionId = session?.SessionId;
        _textNextSequence = 0;
        _textRows!.Children.Clear();
        _textArea!.IsVisible = session != null;
        _viewText!.IsEnabled = session != null;
        _nextText!.IsVisible = _cancelText!.IsVisible = false;
        _textCaption!.Text = session == null ? "" : $"识别原文 · 创建于 {session.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}";
        _textStatus!.Text = "尚未读取正文。点击“查看原文”加载此会话的历史文字。";
    }

    private async Task LoadSessionTextAsync(long afterSequence)
    {
        if (_closed || _selectedSessionId is not { } sessionId) return;
        InvalidateSessionText();
        var cts = new CancellationTokenSource();
        _textCts = cts;
        var request = _textRequest;
        _textRows!.Children.Clear();
        _textStatus!.Foreground = Muted;
        _textStatus.Text = "正在读取此会话的识别原文…";
        _viewText!.IsEnabled = false;
        _nextText!.IsVisible = false;
        _cancelText!.IsVisible = true;
        try
        {
            var page = await _service.ReadSessionTextAsync(sessionId, afterSequence,
                cancellationToken: cts.Token);
            if (!IsCurrent()) return;
            _textNextSequence = page.NextSequence;
            _nextText.IsVisible = page.HasMore;
            _textStatus.Text = page.Availability switch
            {
                VoiceImeSessionTextAvailability.DatabaseMissing => "本地用量数据库不存在；未创建任何文件。",
                VoiceImeSessionTextAvailability.SchemaMissing => "当前数据库尚无用量表；详情不会迁移数据库。",
                VoiceImeSessionTextAvailability.SessionMissing => "此会话已不存在，请刷新报表后重新选择。",
                _ when page.Entries.Count == 0 => afterSequence == 0
                    ? "此会话暂无识别定稿记录。" : "此会话已没有更多识别记录。",
                _ => $"当前批 {page.Entries.Count} 句 · 序号 {page.Entries[0].Sequence}–{page.NextSequence} · " +
                    (page.HasMore ? "还有记录，可读取下一批" : "已到会话末尾")
            };
            foreach (var entry in page.Entries)
            {
                var row = new StackPanel { Spacing = 7 };
                row.Children.Add(Wrapped($"第 {entry.Sequence} 句 · {entry.FinalizedAt:yyyy-MM-dd HH:mm:ss zzz} · 定稿 {Number(entry.Characters)} 字", 11, Muted));
                var body = Wrapped(entry.OriginalText ?? "当时未保存识别原文；现在开启保存也无法补回。",
                    14, entry.OriginalText == null ? Muted : Ink);
                body.Name = entry.OriginalText == null ? "UsageSessionTextMissing" : "UsageSessionOriginalText";
                row.Children.Add(body);
                if (entry.OriginalText == "") row.Children.Add(Text("该条已保存的识别原文为空。", 12, Muted));
                _textRows.Children.Add(Card(row, 14));
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            _textStatus.Foreground = Brush("#A54429");
            _textStatus.Text = ex is SqliteException { SqliteErrorCode: 5 or 6 }
                ? "数据库暂时繁忙，请稍后点击“查看原文”重试。"
                : "暂时无法读取识别原文，请重试或检查存储权限与版本。未修改任何数据。";
        }
        finally
        {
            if (IsCurrent())
            {
                _viewText.IsEnabled = true;
                _cancelText.IsVisible = false;
            }
            if (ReferenceEquals(_textCts, cts)) _textCts = null;
            cts.Dispose();
        }

        bool IsCurrent() => !_closed && request == _textRequest && _selectedSessionId == sessionId
            && !cts.IsCancellationRequested;
    }

    private static Border SessionRow(string time, string characters, string utterances, string duration, string state, bool header)
    {
        var grid = new Grid { ColumnDefinitions = new("1.6*,*,*,1.4*,1.5*"), ColumnSpacing = 8 };
        var values = new[] { time, characters, utterances, duration, state };
        for (var index = 0; index < values.Length; index++)
        {
            var label = Text(values[index], header ? 11 : 12, header ? Muted : Ink);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, index);
            grid.Children.Add(label);
        }
        return new Border
        {
            Child = grid, Padding = new Thickness(12, header ? 10 : 14),
            Background = header ? Brush("#F3F7FC") : Brushes.Transparent,
            CornerRadius = new CornerRadius(header ? 6 : 0), BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, header ? 0 : 1)
        };
    }

    private static string StateLabel(VoiceImeUsageReportSession session) => session.State switch
    {
        "connecting" => "连接中 · 检查点",
        "recording" => "录音中 · 检查点",
        "interrupted" => "异常中断 · 检查点",
        "ended" => session.StopReason switch
        {
            "manual_stop" => "手动停止",
            "inactivity_timeout" => "无字超时",
            "other_ime" => "切换输入法",
            "failed" => "语音失败",
            "owner_exit" => "后台退出",
            _ => "已结束"
        },
        _ => "其他状态"
    };

    private static string Number(long value) => value.ToString("N0", Chinese);

    private static string Duration(long milliseconds)
    {
        var seconds = milliseconds / 1000;
        if (milliseconds > 0 && seconds == 0) return "< 1 秒";
        if (seconds < 60) return $"{seconds} 秒";
        if (seconds < 3600) return $"{seconds / 60} 分 {seconds % 60:00} 秒";
        return $"{seconds / 3600} 时 {seconds % 3600 / 60:00} 分";
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private static TextBlock Text(string text, double size, IBrush color, FontWeight? weight = null)
        => new() { Text = text, FontSize = size, Foreground = color, FontWeight = weight ?? FontWeight.Normal };
    private static TextBlock Wrapped(string text, double size, IBrush color, FontWeight? weight = null)
        => new() { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap,
            FontWeight = weight ?? FontWeight.Normal };
    private static Border Card(Control child, double padding = 22)
        => new()
        {
            Child = child, Padding = new Thickness(padding), Background = Brushes.White,
            CornerRadius = new CornerRadius(12), BorderBrush = Line, BorderThickness = new Thickness(1)
        };
}