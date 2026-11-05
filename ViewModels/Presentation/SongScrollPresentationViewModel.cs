using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels.Presentation;

/// <summary>长卷只读投影；集合顺序与句身份来自业务，不拥有服务、字号配置或人物身份。</summary>
public sealed class SongScrollPresentationViewModel : ViewModelBase, IDisposable
{
    private readonly ILiveTranslationPresentationSource _source;
    private ObservableCollection<TranslationItem> _history;
    private readonly Dictionary<TranslationItem, SongScrollCard> _cards = new(ReferenceEqualityComparer.Instance);
    private SongScrollCard? _current;
    private LiveTranslationFrame? _frame;
    private bool _fallbackCommitted, _disposed, _allHistory, _settingsExpanded;
    private string _display = "bilingual", _tab = "insight";
    private int _fontSize = 16;

    public SongScrollPresentationViewModel(ILiveTranslationPresentationSource source)
    {
        _source = source;
        _history = source.History;
        _source.PropertyChanged += SourceChanged;
        _history.CollectionChanged += HistoryChanged;
        Refresh();
    }
    public object Session => _source;
    public ObservableCollection<SongScrollCard> Conversation { get; } = new();
    public ObservableCollection<SongScrollCard> VisibleHistory { get; } = new();
    public bool HasConversation => Conversation.Count > 0;
    public bool HasHistory => VisibleHistory.Count > 0;
    public string HistorySummary => $"{(_allHistory ? "全部" : "最近 15")}条 / 共 {_history.Count} 条";
    public string HistoryToggleText => _allHistory ? "最近 15 条" : "查看全部";
    public bool AllHistory { get => _allHistory; set { if (SetProperty(ref _allHistory, value)) Refresh(); } }
    public bool SettingsExpanded { get => _settingsExpanded; set { if (SetProperty(ref _settingsExpanded, value)) OnPropertyChanged(nameof(SettingsToggleText)); } }
    public string SettingsToggleText => SettingsExpanded ? "收起 ▴" : "展开 ▾";
    public bool IsHistory => _tab == "history";
    public bool IsInsight => !IsHistory;
    public bool ShowOriginal => _display != "translated";
    public bool ShowTranslated => _display != "original";
    public bool IsOriginal => _display == "original";
    public bool IsTranslated => _display == "translated";
    public bool IsBilingual => _display == "bilingual";
    public int[] FontSizes { get; } = { 16, 20, 24 };
    public int ReadingSize
    {
        get => _fontSize;
        set
        {
            if (value is not (16 or 20 or 24) || !SetProperty(ref _fontSize, value)) return;
            OnPropertyChanged(nameof(OriginalSize)); OnPropertyChanged(nameof(TranslatedSize)); OnPropertyChanged(nameof(InsightSize));
        }
    }
    public double OriginalSize => 36 + (ReadingSize - 16) * 1.5;
    public double TranslatedSize => 32 + (ReadingSize - 16) * 1.5;
    public double InsightSize => 26 + (ReadingSize - 16);
    public void SetDisplay(string mode)
    {
        if (mode is not ("original" or "translated" or "bilingual")) return;
        _display = mode;
        foreach (var name in new[] { nameof(ShowOriginal), nameof(ShowTranslated), nameof(IsOriginal), nameof(IsTranslated), nameof(IsBilingual) }) OnPropertyChanged(name);
    }
    public void SetTab(string tab)
    {
        if (tab is not ("history" or "insight")) return;
        _tab = tab; OnPropertyChanged(nameof(IsHistory)); OnPropertyChanged(nameof(IsInsight));
    }
    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_history, _source.History))
        {
            _history.CollectionChanged -= HistoryChanged; _history = _source.History;
            _history.CollectionChanged += HistoryChanged; _cards.Clear(); _fallbackCommitted = false;
        }
        if (e.PropertyName is nameof(ILiveTranslationPresentationSource.CurrentOriginal) or nameof(ILiveTranslationPresentationSource.CurrentTranslated)) _fallbackCommitted = false;
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(ILiveTranslationPresentationSource.History)
            or nameof(ILiveTranslationPresentationSource.LatestRealtimeFrame) or nameof(ILiveTranslationPresentationSource.CurrentOriginal)
            or nameof(ILiveTranslationPresentationSource.CurrentTranslated)) Refresh();
    }
    private void HistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        if (_source.LatestRealtimeFrame == null && _frame == null && _current != null && e.NewItems != null)
            foreach (TranslationItem item in e.NewItems)
                if (item.OriginalText == _source.CurrentOriginal && item.TranslatedText == _source.CurrentTranslated) _fallbackCommitted = true;
        Refresh();
    }
    private void Refresh()
    {
        if (_disposed) return;
        var finals = new List<SongScrollCard>();
        foreach (var item in _history)
        {
            if (!_cards.TryGetValue(item, out var card))
                _cards[item] = card = _current != null && _frame != null && SameSentence(_frame, item) ? _current : new SongScrollCard();
            card.Update(item.OriginalText, item.TranslatedText, item.Timestamp, item.SubtitleSource ?? item.Source, item.SubtitleSegmentKey);
            card.IsAlternate = finals.Count % 2 == 0;
            finals.Add(card);
        }
        foreach (var stale in _cards.Keys.Where(item => !_history.Contains(item)).ToArray()) _cards.Remove(stale);
        Sync(VisibleHistory, (_allHistory ? finals : finals.Take(15)).ToList());
        var frame = _source.LatestRealtimeFrame;
        if (frame != null && (frame.OriginalText.Length != 0 || frame.TranslatedText.Length != 0) && !_history.Any(item => SameSentence(frame, item)))
        {
            if (_current == null || finals.Contains(_current) || (_frame?.SegmentKey is { Length: > 0 } oldKey
                && frame.SegmentKey is { Length: > 0 } newKey && oldKey != newKey)) _current = new SongScrollCard();
            _current.Update(frame.OriginalText, frame.TranslatedText, frame.Timestamp, frame.Source, frame.SegmentKey); _frame = frame;
        }
        else if (frame == null && !_fallbackCommitted && (_source.CurrentOriginal.Length != 0 || _source.CurrentTranslated.Length != 0))
        {
            if (_current == null || finals.Contains(_current)) _current = new SongScrollCard();
            _current.Update(_source.CurrentOriginal, _source.CurrentTranslated, default, VadGateController.ActiveSource.None, null); _frame = null;
        }
        else { _current = null; _frame = null; }
        // History 本身最近在前；对话阅读从旧到新，只反转投影，不排序或改变业务集合。
        var conversation = finals.AsEnumerable().Reverse().ToList();
        if (_current != null) conversation.Add(_current);
        foreach (var card in conversation) card.IsLatest = ReferenceEquals(card, conversation.LastOrDefault());
        Sync(Conversation, conversation);
        foreach (var name in new[] { nameof(HasConversation), nameof(HasHistory), nameof(HistorySummary), nameof(HistoryToggleText) }) OnPropertyChanged(name);
    }
    private static bool SameSentence(LiveTranslationFrame frame, TranslationItem item)
        => !string.IsNullOrEmpty(frame.SegmentKey) && !string.IsNullOrEmpty(item.SubtitleSegmentKey)
            ? frame.SegmentKey == item.SubtitleSegmentKey
            : frame.Timestamp != default && frame.Timestamp == item.Timestamp && frame.Source == (item.SubtitleSource ?? item.Source);
    private static void Sync(ObservableCollection<SongScrollCard> target, List<SongScrollCard> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var old = target.IndexOf(desired[i]);
            if (old >= 0) target.Move(old, i); else target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _source.PropertyChanged -= SourceChanged; _history.CollectionChanged -= HistoryChanged;
    }
}

public sealed class SongScrollCard : ViewModelBase
{
    private string _original = "", _translated = "", _key = "";
    private DateTime _time;
    private VadGateController.ActiveSource _source;
    private bool _latest;
    private bool _expanded, _alternate;
    public bool IsExpanded { get => _expanded; set { if (SetProperty(ref _expanded, value)) OnPropertyChanged(nameof(ExpandMark)); } }
    public string ExpandMark => IsExpanded ? "−" : "···";
    public bool IsAlternate { get => _alternate; internal set { if (SetProperty(ref _alternate, value)) OnPropertyChanged(nameof(HistoryBackground)); } }
    public IBrush HistoryBackground => IsAlternate ? new SolidColorBrush(Color.Parse("#77EDF0EF")) : Brushes.Transparent;
    public bool IsLatest { get => _latest; internal set { if (SetProperty(ref _latest, value)) OnPropertyChanged(nameof(ConversationHeight)); } }
    public double ConversationHeight => IsLatest ? 270 : 310;
    public string OriginalText => _original;
    public string TranslatedText => _translated;
    public string SegmentKey => _key;
    public string TimeText => _time == default ? "实时" : _time.ToString("HH:mm:ss");
    public string SourceLabel => _source switch { VadGateController.ActiveSource.Mic => "麦克风", VadGateController.ActiveSource.Loopback => "系统音频", _ => "音频输入" };
    internal void Update(string original, string translated, DateTime time, VadGateController.ActiveSource source, string? key)
    {
        SetProperty(ref _original, original, nameof(OriginalText)); SetProperty(ref _translated, translated, nameof(TranslatedText));
        SetProperty(ref _key, key ?? "", nameof(SegmentKey));
        if (SetProperty(ref _time, time)) OnPropertyChanged(nameof(TimeText));
        if (SetProperty(ref _source, source)) OnPropertyChanged(nameof(SourceLabel));
    }
}