using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace TrueFluentPro.Controls;

/// <summary>
/// Panel 形式的会话缓存控件：每个 Content（会话 VM）对应一棵子视觉树，
/// 仅当前会话可见，其余隐藏但保留在 Children 中。切换时直接 Show/Hide，
/// 无需重建，实现秒级切换。配合 StackPanel ItemsPanel 消除 Extent 震荡。
/// </summary>
public class CachedContentControl : Panel
{
    public static readonly StyledProperty<int> MaxCachedViewsProperty =
        AvaloniaProperty.Register<CachedContentControl, int>(nameof(MaxCachedViews), 8);

    public static readonly StyledProperty<Func<object?, string?>?> KeySelectorProperty =
        AvaloniaProperty.Register<CachedContentControl, Func<object?, string?>?>(nameof(KeySelector));

    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<CachedContentControl, object?>(nameof(Content));

    public static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<CachedContentControl, IDataTemplate?>(nameof(ContentTemplate));

    /// <summary>最多缓存的视觉树数量，超出按 LRU 驱逐。</summary>
    public int MaxCachedViews
    {
        get => GetValue(MaxCachedViewsProperty);
        set => SetValue(MaxCachedViewsProperty, value);
    }

    /// <summary>从 Content 对象提取缓存 key 的委托。</summary>
    public Func<object?, string?>? KeySelector
    {
        get => GetValue(KeySelectorProperty);
        set => SetValue(KeySelectorProperty, value);
    }

    /// <summary>当前绑定的内容对象（通常是会话 ViewModel）。</summary>
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>用于从 Content 创建视觉树的 DataTemplate。</summary>
    public IDataTemplate? ContentTemplate
    {
        get => GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private Control? _currentView;

    private record CacheEntry(Control View, LinkedListNode<string> Node);

    /// <summary>最近一次 SwitchContent 是否命中缓存。View 据此决定是否需要 scroll-to-bottom。</summary>
    public bool LastSwitchWasCacheHit { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ContentProperty)
            SwitchContent(change.NewValue);
    }

    private void SwitchContent(object? newContent)
    {
        // 隐藏当前
        if (_currentView != null)
        {
            _currentView.IsVisible = false;
            _currentView = null;
        }

        if (newContent == null)
            return;

        var key = KeySelector?.Invoke(newContent);

        // 有 key → 走缓存路径
        if (key != null)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                // 缓存命中：直接显示
                entry.View.DataContext = newContent;
                entry.View.IsVisible = true;
                TouchLru(entry.Node);
                _currentView = entry.View;
                LastSwitchWasCacheHit = true;
                Helpers.ScrollDiagLog.Log($"[ViewCache] HIT key={key}");
                return;
            }

            // 缓存未命中：用 DataTemplate 创建新视觉树
            var view = BuildView(newContent);
            Children.Add(view);
            var node = _lruOrder.AddFirst(key);
            _cache[key] = new CacheEntry(view, node);
            _currentView = view;
            LastSwitchWasCacheHit = false;
            EnforceCacheLimit();
            Helpers.ScrollDiagLog.Log($"[ViewCache] MISS key={key}, cached={_cache.Count}");
            return;
        }

        // 无 key → 不缓存，直接创建临时视图
        var ephemeral = BuildView(newContent);
        Children.Add(ephemeral);
        _currentView = ephemeral;
    }

    private Control BuildView(object content)
    {
        var template = ContentTemplate;
        if (template != null)
        {
            var view = template.Build(content);
            if (view != null)
            {
                view.DataContext = content;
                return view;
            }
        }
        return new TextBlock { Text = content?.ToString() ?? "" };
    }

    private void TouchLru(LinkedListNode<string> node)
    {
        _lruOrder.Remove(node);
        _lruOrder.AddFirst(node);
    }

    private void EnforceCacheLimit()
    {
        while (_cache.Count > Math.Max(1, MaxCachedViews) && _lruOrder.Last != null)
        {
            var evictKey = _lruOrder.Last.Value;
            _lruOrder.RemoveLast();
            if (_cache.Remove(evictKey, out var evicted))
            {
                Children.Remove(evicted.View);
                Helpers.ScrollDiagLog.Log($"[ViewCache] EVICT key={evictKey}");
            }
        }
    }

    /// <summary>清除指定 key 的缓存。</summary>
    public void EvictCache(string key)
    {
        if (_cache.Remove(key, out var entry))
        {
            _lruOrder.Remove(entry.Node);
            Children.Remove(entry.View);
        }
    }

    /// <summary>清空所有缓存。</summary>
    public void ClearAllCache()
    {
        _cache.Clear();
        _lruOrder.Clear();
        Children.Clear();
        _currentView = null;
    }

    // —— Layout：只测量/排列当前可见子项 ——

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_currentView != null)
        {
            _currentView.Measure(availableSize);
            return _currentView.DesiredSize;
        }
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_currentView != null)
            _currentView.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
