using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

/// <summary>长卷独立听析内容树；只借用原页业务事件，不拥有共享会话。</summary>
public partial class SongScrollAudioLabView : AudioLabView, ISongScrollPage
{
    private AudioLabViewModel? _observed;
    private bool _viewReady;
    private bool _selectingFallback;

    public SongScrollShell Shell => this.FindControl<SongScrollShell>("BookShell")!;

    public SongScrollAudioLabView() : base(false)
    {
        InitializeComponent();
        _viewReady = true;
    }

    // 折叠目录只折叠内容。纸页宽度和唯一书脊的位置完全交给 Shell。
    protected override void ApplyFilePanelState(bool isOpen) { }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewReady) ObservePresentation(ViewModel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ObservePresentation(ViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        ObservePresentation(null);
        base.OnUnloaded(e);
    }

    private void ObservePresentation(AudioLabViewModel? vm)
    {
        if (!ReferenceEquals(_observed, vm))
        {
            if (_observed != null) _observed.PropertyChanged -= OnSessionChanged;
            _observed = vm;
            if (vm != null) vm.PropertyChanged += OnSessionChanged;
        }
        EnsureVisibleTab();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioLabViewModel.SelectedTab)
            or nameof(AudioLabViewModel.CustomStageKey)
            or nameof(AudioLabViewModel.CustomVisibleStages)
            or nameof(AudioLabViewModel.IsSummaryTabVisible)
            or nameof(AudioLabViewModel.IsMindMapTabVisible)
            or nameof(AudioLabViewModel.IsInsightTabVisible)
            or nameof(AudioLabViewModel.IsResearchTabVisible)
            or nameof(AudioLabViewModel.IsPodcastTabVisible)
            or nameof(AudioLabViewModel.IsTranslationTabVisible))
            EnsureVisibleTab();
    }

    private void EnsureVisibleTab()
    {
        var vm = _observed;
        if (vm == null || _selectingFallback) return;
        var visible = vm.SelectedTab switch
        {
            AudioLabTabKind.Summary => vm.IsSummaryTabVisible,
            AudioLabTabKind.MindMap => vm.IsMindMapTabVisible,
            AudioLabTabKind.Insight => vm.IsInsightTabVisible,
            AudioLabTabKind.Research => vm.IsResearchTabVisible,
            AudioLabTabKind.Podcast => vm.IsPodcastTabVisible,
            AudioLabTabKind.Translation => vm.IsTranslationTabVisible,
            AudioLabTabKind.Custom => vm.CustomVisibleStages.Any(s => s.Stage == vm.CustomStageKey),
            _ => true
        };
        if (visible) return;
        _selectingFallback = true;
        try { vm.SelectedTab = AudioLabTabKind.Transcript; }
        finally { _selectingFallback = false; }
    }

    private void JumpToTime_Click(object? sender, RoutedEventArgs e) => JumpToTime();

    private void JumpTime_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        JumpToTime();
        e.Handled = true;
    }

    private void JumpToTime()
    {
        var vm = ViewModel;
        if (vm == null || !vm.Playback.IsStopEnabled) return;
        var text = this.FindControl<TextBox>("JumpTimeInput")?.Text?.Trim() ?? "";
        if (TimeSpan.TryParseExact(text, new[] { @"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss" },
                CultureInfo.InvariantCulture, out var time)
            && time >= TimeSpan.Zero && time <= vm.Playback.Duration)
            vm.Playback.SeekToTime(time);
        else
        {
            vm.StatusMessage = "请输入音频时长范围内的时间，例如 12:36 或 1:12:36。";
            vm.HasExportFeedback = true;
        }
    }

    private async void AskTranscript_Click(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null || vm.Segments.Count == 0) return;
        try
        {
            if (OpenTranscriptChatAsync == null)
                throw new InvalidOperationException("聊天窗口尚未就绪，请稍后重试。");
            var transcript = AudioLabExportService.BuildTranscriptMarkdown(
                Path.GetFileNameWithoutExtension(vm.CurrentFilePath),
                Path.GetFileName(vm.CurrentFilePath), vm.Segments.ToList());
            var segment = vm.CurrentSegment;
            var quote = segment == null ? "" : $"[{segment.TimeText}] {segment.Speaker}: {segment.Text}";
            await OpenTranscriptChatAsync(vm.CurrentFilePath, transcript, quote);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"打开字幕问答失败：{ex.Message}";
            vm.HasExportFeedback = true;
        }
    }
}