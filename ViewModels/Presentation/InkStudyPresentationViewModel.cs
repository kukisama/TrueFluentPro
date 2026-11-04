using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels.Presentation;

/// <summary>
/// 书斋独立呈现层。沿用数据源的通知线程，仅投影数据，不拥有业务生命周期。
/// </summary>
public sealed class InkStudyPresentationViewModel : ViewModelBase, IDisposable
{
    private readonly ILiveTranslationPresentationSource _source;
    private ObservableCollection<TranslationItem> _history;
    private Dictionary<TranslationItem, InkTranslationCard> _historyCards = new(ReferenceEqualityComparer.Instance);
    private InkTranslationCard? _currentCard;
    private LiveTranslationFrame? _currentFrame;
    private bool _fallbackCommitted;
    private bool _disposed;
    private DisplayMode _displayMode = DisplayMode.Bilingual;
    private double _conversationFontSize = 16;

    public InkStudyPresentationViewModel(ILiveTranslationPresentationSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _history = source.History;
        _source.PropertyChanged += OnSourcePropertyChanged;
        _history.CollectionChanged += OnHistoryCollectionChanged;
        Refresh();
    }

    /// <summary>原业务对象，供 Session.ConfigVM、AudioDevices、AiInsight 和现有命令绑定。</summary>
    public object Session => _source;

    public ObservableCollection<InkTranslationCard> Conversation { get; } = new();
    public ObservableCollection<InkTranslationCard> RecentHistory { get; } = new();
    public bool HasConversation => Conversation.Count != 0;
    public bool HasHistory => RecentHistory.Count != 0;

    public bool IsOriginalMode
    {
        get => _displayMode == DisplayMode.Original;
        set { if (value) SetDisplayMode(DisplayMode.Original); }
    }

    public bool IsTranslatedMode
    {
        get => _displayMode == DisplayMode.Translated;
        set { if (value) SetDisplayMode(DisplayMode.Translated); }
    }

    public bool IsBilingualMode
    {
        get => _displayMode == DisplayMode.Bilingual;
        set { if (value) SetDisplayMode(DisplayMode.Bilingual); }
    }

    public bool ShowOriginalLine => _displayMode != DisplayMode.Translated;
    public bool ShowTranslatedLine => _displayMode != DisplayMode.Original;

    public double ConversationFontSize
    {
        get => _conversationFontSize;
        set => SetProperty(ref _conversationFontSize, Math.Clamp(double.IsNaN(value) ? 16 : value, 12, 28));
    }

    public int[] FontSizes { get; } = new[] { 14, 16, 18, 20, 22, 24 };

    /// <summary>仅卡片内容或投影集合实际变化时触发，可供 View 跟随滚动。</summary>
    public event EventHandler? Changed;

    private void SetDisplayMode(DisplayMode mode)
    {
        if (_displayMode == mode) return;
        _displayMode = mode;
        OnPropertyChanged(nameof(IsOriginalMode));
        OnPropertyChanged(nameof(IsTranslatedMode));
        OnPropertyChanged(nameof(IsBilingualMode));
        OnPropertyChanged(nameof(ShowOriginalLine));
        OnPropertyChanged(nameof(ShowTranslatedLine));
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(ILiveTranslationPresentationSource.History))
            ReconnectHistory();

        if (all || e.PropertyName == nameof(ILiveTranslationPresentationSource.CurrentOriginal)
            || e.PropertyName == nameof(ILiveTranslationPresentationSource.CurrentTranslated))
            _fallbackCommitted = false;

        if (all || e.PropertyName == nameof(ILiveTranslationPresentationSource.History)
            || e.PropertyName == nameof(ILiveTranslationPresentationSource.LatestRealtimeFrame)
            || e.PropertyName == nameof(ILiveTranslationPresentationSource.CurrentOriginal)
            || e.PropertyName == nameof(ILiveTranslationPresentationSource.CurrentTranslated))
            Refresh();
    }

    private void ReconnectHistory()
    {
        var history = _source.History;
        if (ReferenceEquals(_history, history)) return;
        _history.CollectionChanged -= OnHistoryCollectionChanged;
        _history = history;
        _historyCards.Clear();
        _fallbackCommitted = false;
        _history.CollectionChanged += OnHistoryCollectionChanged;
    }

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;

        // 兼容源没有句身份：只承认本次新增终稿与当前活跃卡的交接，
        // 不在已有历史中按正文查重，避免吞掉后来重复说出的另一句话。
        if (_source.LatestRealtimeFrame == null && _currentFrame == null && _currentCard != null
            && e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (TranslationItem item in e.NewItems)
            {
                if (item.OriginalText == _source.CurrentOriginal
                    && item.TranslatedText == _source.CurrentTranslated)
                {
                    _fallbackCommitted = true;
                    break;
                }
            }
        }

        Refresh();
    }

    private void Refresh()
    {
        if (_disposed) return;
        var hadConversation = HasConversation;
        var hadHistory = HasHistory;
        var changed = false;
        var frame = _source.LatestRealtimeFrame;

        // 只对本地枚举排序；时间相同时保留业务集合的新近优先顺序。
        var latest = _history.Select((item, index) => (Item: item, Index: index))
            .OrderByDescending(entry => entry.Item.Timestamp)
            .ThenBy(entry => entry.Index)
            .Take(12)
            .ToList();
        var retained = new Dictionary<TranslationItem, InkTranslationCard>(ReferenceEqualityComparer.Instance);
        var finals = new List<InkTranslationCard>();
        foreach (var entry in latest)
        {
            var item = entry.Item;
            if (!_historyCards.TryGetValue(item, out var card))
            {
                // 入历史时沿用同一句的卡片，partial → final 不先删后加。
                card = _currentCard != null && _currentFrame != null && SameSentence(_currentFrame, item)
                    ? _currentCard : new InkTranslationCard();
            }

            changed |= card.Update(item.OriginalText, item.TranslatedText, item.Timestamp,
                HistorySource(item), false, item.SubtitleSegmentKey);
            retained[item] = card;
            finals.Add(card);
        }
        _historyCards = retained;

        changed |= Synchronize(RecentHistory, finals.Take(3).ToList());

        if (frame != null)
        {
            // 快照存在时，不借正文、旧浮窗状态或历史来源补猜元数据。
            if (HasText(frame.OriginalText, frame.TranslatedText)
                && !_history.Any(item => SameSentence(frame, item)))
            {
                // 已转入历史的实例不能再被下一句（尤其无 key 的帧）改写。
                if (_currentCard == null || finals.Contains(_currentCard) || (_currentFrame != null
                    && HasKey(_currentFrame.SegmentKey) && HasKey(frame.SegmentKey)
                    && _currentFrame.SegmentKey != frame.SegmentKey))
                    _currentCard = new InkTranslationCard();

                changed |= _currentCard.Update(frame.OriginalText, frame.TranslatedText,
                    frame.Timestamp, frame.Source, !frame.IsFinal, frame.SegmentKey);
                _currentFrame = frame;
            }
            else
            {
                _currentCard = null;
                _currentFrame = null;
            }
        }
        else if (!_fallbackCommitted && HasText(_source.CurrentOriginal, _source.CurrentTranslated))
        {
            if (_currentCard == null || finals.Contains(_currentCard))
                _currentCard = new InkTranslationCard();
            _currentFrame = null;
            changed |= _currentCard.Update(_source.CurrentOriginal, _source.CurrentTranslated,
                default, VadGateController.ActiveSource.None, true, null);
        }
        else
        {
            _currentCard = null;
            _currentFrame = null;
        }

        // 极短的 final-before-history 窗口也可显示，但它占终稿的 12 条限额。
        if (_currentCard is { IsLive: false }) finals.Add(_currentCard);
        var conversation = finals.OrderByDescending(card => card.Timestamp)
            .Take(12).Reverse().ToList();
        if (_currentCard is { IsLive: true })
        {
            conversation.Add(_currentCard);
            // 无时间的兼容卡置于末尾；已知时间按实际阅读顺序排列。
            conversation = conversation.OrderBy(card => card.Timestamp == default ? DateTime.MaxValue : card.Timestamp)
                .ToList();
        }

        changed |= Synchronize(Conversation, conversation);
        if (hadConversation != HasConversation) OnPropertyChanged(nameof(HasConversation));
        if (hadHistory != HasHistory) OnPropertyChanged(nameof(HasHistory));
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static VadGateController.ActiveSource HistorySource(TranslationItem item)
        => item.SubtitleSource is { } routed && routed != VadGateController.ActiveSource.None
            ? routed : item.Source;

    private static bool HasKey(string? key) => !string.IsNullOrEmpty(key);

    private static bool HasText(string original, string translated)
        => !string.IsNullOrWhiteSpace(original) || !string.IsNullOrWhiteSpace(translated);

    private static bool SameSentence(LiveTranslationFrame frame, TranslationItem item)
    {
        if (HasKey(frame.SegmentKey) && HasKey(item.SubtitleSegmentKey))
            return string.Equals(frame.SegmentKey, item.SubtitleSegmentKey, StringComparison.Ordinal);

        // 缺 key 时需要真实时间和来源；正文相同本身不是句身份。
        return frame.Timestamp != default && frame.Timestamp == item.Timestamp
            && frame.Source == HistorySource(item);
    }

    private static bool Synchronize(ObservableCollection<InkTranslationCard> target, List<InkTranslationCard> desired)
    {
        var changed = false;
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], desired[index])) continue;
            var oldIndex = target.IndexOf(desired[index]);
            if (oldIndex >= 0) target.Move(oldIndex, index);
            else target.Insert(index, desired[index]);
            changed = true;
        }
        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
            changed = true;
        }
        return changed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.PropertyChanged -= OnSourcePropertyChanged;
        _history.CollectionChanged -= OnHistoryCollectionChanged;
    }

    private enum DisplayMode { Original, Translated, Bilingual }
}

/// <summary>独立可通知的呈现副本；不持有可供修改的业务 TranslationItem。</summary>
public sealed class InkTranslationCard : ViewModelBase
{
    private static readonly IBrush MicAccent = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush MicBubble = new SolidColorBrush(Color.Parse("#EFF6FF"));
    private static readonly IBrush MicAvatar = new SolidColorBrush(Color.Parse("#DBEAFE"));
    private static readonly IBrush LoopbackAccent = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush LoopbackBubble = new SolidColorBrush(Color.Parse("#FEF2F2"));
    private static readonly IBrush LoopbackAvatar = new SolidColorBrush(Color.Parse("#FEE2E2"));
    private static readonly IBrush NeutralAccent = new SolidColorBrush(Color.Parse("#78716C"));
    private static readonly IBrush NeutralBubble = new SolidColorBrush(Color.Parse("#F5F5F4"));
    private static readonly IBrush NeutralAvatar = new SolidColorBrush(Color.Parse("#E7E5E4"));

    private string _originalText = "";
    private string _translatedText = "";
    private DateTime _timestamp;
    private VadGateController.ActiveSource _source;
    private bool _isLive;
    private string? _segmentKey;

    internal InkTranslationCard() { }

    public string OriginalText => _originalText;
    public string TranslatedText => _translatedText;
    public DateTime Timestamp => _timestamp;
    public VadGateController.ActiveSource Source => _source;
    public string? SegmentKey => _segmentKey;
    public bool IsLive => _isLive;
    public bool HasOriginal => !string.IsNullOrWhiteSpace(_originalText);
    public bool HasTranslated => !string.IsNullOrWhiteSpace(_translatedText);
    public string TimeText => _timestamp == default ? "实时" : _timestamp.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string SpeakerLabel => _source switch
    {
        VadGateController.ActiveSource.Mic => "我",
        VadGateController.ActiveSource.Loopback => "对方",
        _ => "发言"
    };

    public IBrush AccentBrush => _source switch
    {
        VadGateController.ActiveSource.Mic => MicAccent,
        VadGateController.ActiveSource.Loopback => LoopbackAccent,
        _ => NeutralAccent
    };

    public IBrush BubbleBrush => _source switch
    {
        VadGateController.ActiveSource.Mic => MicBubble,
        VadGateController.ActiveSource.Loopback => LoopbackBubble,
        _ => NeutralBubble
    };

    public IBrush AvatarBrush => _source switch
    {
        VadGateController.ActiveSource.Mic => MicAvatar,
        VadGateController.ActiveSource.Loopback => LoopbackAvatar,
        _ => NeutralAvatar
    };

    internal bool Update(string original, string translated, DateTime timestamp,
        VadGateController.ActiveSource source, bool isLive, string? segmentKey)
    {
        var changed = SetProperty(ref _originalText, original, nameof(OriginalText));
        if (changed) OnPropertyChanged(nameof(HasOriginal));
        var translatedChanged = SetProperty(ref _translatedText, translated, nameof(TranslatedText));
        if (translatedChanged) OnPropertyChanged(nameof(HasTranslated));
        changed |= translatedChanged;
        if (SetProperty(ref _timestamp, timestamp, nameof(Timestamp)))
        {
            OnPropertyChanged(nameof(TimeText));
            changed = true;
        }
        if (SetProperty(ref _source, source, nameof(Source)))
        {
            OnPropertyChanged(nameof(SpeakerLabel));
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(BubbleBrush));
            OnPropertyChanged(nameof(AvatarBrush));
            changed = true;
        }
        changed |= SetProperty(ref _isLive, isLive, nameof(IsLive));
        changed |= SetProperty(ref _segmentKey, segmentKey, nameof(SegmentKey));
        return changed;
    }
}