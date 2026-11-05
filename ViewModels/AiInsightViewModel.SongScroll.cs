using System.Windows.Input;
using System.Threading;

namespace TrueFluentPro.ViewModels;

public partial class AiInsightViewModel
{
    private long _songScrollGeneration;
    private long _songScrollWriteEpoch;
    private bool _songScrollAutoLoaded;
    private CancellationTokenSource? _songScrollAuthCts;
    private bool _songScrollPending;
    private ICommand? _scrollSend, _scrollPreset, _scrollStop;
    public ICommand SongScrollSendCommand => _scrollSend ??= new RelayCommand(
        _ => SendInsight(InsightUserInput, songScroll: true),
        _ => !IsInsightLoading && IsAiConfigured && !string.IsNullOrWhiteSpace(InsightUserInput));
    public ICommand SongScrollPresetCommand => _scrollPreset ??= new RelayCommand(
        prompt => SendInsight(prompt?.ToString() ?? "", songScroll: true), _ => !IsInsightLoading && IsAiConfigured);
    public ICommand SongScrollStopCommand => _scrollStop ??= new RelayCommand(_ => StopSongScrollInsight(), _ => IsInsightLoading);
    private long BeginSongScrollGeneration()
    {
        _songScrollPending = true;
        _songScrollAuthCts = new CancellationTokenSource();
        IsInsightLoading = true;
        NotifySongScrollCommands();
        return ++_songScrollGeneration;
    }
    private void EndSongScrollGeneration(long version)
    {
        // 不开放第二次请求，直到被取消的鉴权/生成确实退出。
        _songScrollPending = false;
        _songScrollAuthCts?.Dispose(); _songScrollAuthCts = null;
        IsInsightLoading = false;
        NotifySongScrollCommands();
    }
    public void StopSongScrollInsight()
    {
        ++_songScrollWriteEpoch;
        _songScrollAuthCts?.Cancel();
        if (_songScrollPending) ++_songScrollGeneration;
        _insightCts?.Cancel();
        _statusSetter("已请求停止生成，保留已显示的结果");
    }
    public void ClearSongScrollInsight()
    {
        StopSongScrollInsight();
        InsightMarkdown = ""; InsightUserInput = "";
        _statusSetter("洞察结果与提问已清除；当前请求收尾后可再次生成");
    }
    private void NotifySongScrollCommands()
    {
        (_scrollSend as RelayCommand)?.RaiseCanExecuteChanged();
        (_scrollPreset as RelayCommand)?.RaiseCanExecuteChanged();
        (_scrollStop as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void LoadSongScrollAutoPreferences()
    {
        if (_songScrollAutoLoaded) return;
        var ai = _configProvider().AiConfig;
        if (ai?.SongScrollAutoPrompt == null) return;
        _songScrollAutoLoaded = true;
        AutoInsightModeIndex = ai.SongScrollAutoModeIndex;
        AutoInsightIntervalSeconds = ai.SongScrollAutoIntervalSeconds;
        AutoInsightPrompt = ai.SongScrollAutoPrompt;
        // 保存启用资格，不在换主题/打开页面时自动启动云请求。
    }
}