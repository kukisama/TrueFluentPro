using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 文本选择管理器：统一处理单 STB 内多行选择和跨 STB 选择。
/// 在 MarkdownRenderer（StackPanel）层通过隧道事件拦截指针操作，
/// 使用 TextLayout.HitTestPoint 精确定位字符，并支持拖选时自动滚动。
///
/// 设计思路：
/// - 同行内水平拖选（delta.Y &lt; 阈值）→ 不干预，交给 STB 原生处理
/// - 超出阈值后进入管理模式 → 夺取 pointer capture，统一计算所有选区
/// - PointerPressed 时缓存 STB 列表和坐标，避免 PointerMoved 中反复遍历 visual tree
/// - 先设新选区再清旧选区，消除"先清后设"引起的闪烁
/// - 支持拖到 ScrollViewer 边缘时自动滚动
/// </summary>
public sealed class SelectionManager
{
    // 全局只允许一个 SelectionManager 持有选区；开始新选区时自动清除其他 renderer 的残留选区
    private static SelectionManager? s_activeManager;

    private readonly Panel _container;

    // ── 拖拽状态 ──
    private bool _isDragging;
    private bool _managedMode;
    private Point _dragStartPoint;
    private int _startCharIndex;
    private int _startStbIndex = -1;

    // PointerPressed 时缓存（拖拽期间不重新收集）
    private List<StbInfo>? _cachedStbs;

    // 当前管理选区的 STB 集合
    private readonly List<SelectableTextBlock> _selectedBlocks = new();

    // ── 自动滚动 ──
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollSpeed;
    private Point _lastContainerPoint;

    private const double DragThreshold = 4;
    private const double AutoScrollEdge = 40;
    private const double AutoScrollBaseSpeed = 5;
    private const double AutoScrollMaxSpeed = 30;

    public SelectionManager(Panel container)
    {
        _container = container;
        _container.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _container.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _container.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // ── 公开 API ─────────────────────────────────────────

    /// <summary>获取管理选区内的完整文本</summary>
    public string GetSelectedText()
    {
        if (_selectedBlocks.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var stb in _selectedBlocks)
        {
            if (stb.SelectionStart == stb.SelectionEnd) continue;
            var sel = GetSelectedSubstring(stb);
            if (!string.IsNullOrEmpty(sel))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(sel);
            }
        }
        return sb.ToString();
    }

    /// <summary>是否有 SelectionManager 管理的选区</summary>
    public bool HasManagedSelection =>
        _selectedBlocks.Count > 0 && _selectedBlocks.Any(s => s.SelectionStart != s.SelectionEnd);

    /// <summary>向后兼容别名</summary>
    public bool HasCrossBlockSelection => HasManagedSelection;

    /// <summary>清除所有管理选区</summary>
    public void ClearSelection()
    {
        foreach (var stb in _selectedBlocks)
        {
            stb.SelectionStart = 0;
            stb.SelectionEnd = 0;
        }
        _selectedBlocks.Clear();
    }

    /// <summary>分离事件</summary>
    public void Detach()
    {
        StopAutoScroll();
        _container.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _container.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _container.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
    }

    // ── 事件处理 ─────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_container).Properties.IsLeftButtonPressed) return;

        // 新选区开始：清除其他 renderer 的残留选区
        if (s_activeManager != null && s_activeManager != this)
            s_activeManager.ClearSelection();
        s_activeManager = this;

        _dragStartPoint = e.GetPosition(_container);
        _isDragging = true;
        _managedMode = false;
        ClearSelection();

        // 一次性缓存所有 STB 及其容器内坐标（拖拽期间有效）
        _cachedStbs = CollectSelectableTextBlocks();
        _startStbIndex = FindStbIndexAtPoint(_cachedStbs, _dragStartPoint);
        _startCharIndex = _startStbIndex >= 0
            ? HitTestCharIndex(_cachedStbs[_startStbIndex], _dragStartPoint)
            : 0;

        _scrollViewer ??= FindAncestorScrollViewer();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _cachedStbs == null || _startStbIndex < 0) return;

        var pt = e.GetPosition(_container);

        if (!_managedMode)
        {
            if (Math.Abs(pt.Y - _dragStartPoint.Y) < DragThreshold) return;
            _managedMode = true;
            e.Pointer.Capture(_container);
        }

        e.Handled = true;
        _lastContainerPoint = pt;
        UpdateSelectionTo(pt);
        UpdateAutoScroll(e);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopAutoScroll();
        if (_managedMode)
        {
            e.Pointer.Capture(null);
            _managedMode = false;
        }
        _isDragging = false;
        _cachedStbs = null;
    }

    // ── 选区计算（核心）─────────────────────────────────

    private void UpdateSelectionTo(Point containerPoint)
    {
        var stbs = _cachedStbs!;
        int endIdx = FindStbIndexAtPoint(stbs, containerPoint);
        if (endIdx < 0) return;

        int lo = Math.Min(_startStbIndex, endIdx);
        int hi = Math.Max(_startStbIndex, endIdx);
        bool reversed = _startStbIndex > endIdx;

        int endCharIdx = HitTestCharIndex(stbs[endIdx], containerPoint);

        // ① 先计算并应用新选区（避免闪烁）
        var newSet = new HashSet<SelectableTextBlock>();

        for (int i = lo; i <= hi; i++)
        {
            var info = stbs[i];
            int tLen = info.TextLength;
            if (tLen == 0) continue;

            newSet.Add(info.Stb);

            int ss, se;
            if (lo == hi)
            {
                int a = Math.Min(_startCharIndex, endCharIdx);
                int b = Math.Max(_startCharIndex, endCharIdx);
                ss = Math.Clamp(a, 0, tLen);
                se = Math.Clamp(b, 0, tLen);
            }
            else if (i == _startStbIndex)
            {
                ss = reversed ? 0 : Math.Clamp(_startCharIndex, 0, tLen);
                se = reversed ? Math.Clamp(_startCharIndex, 0, tLen) : tLen;
            }
            else if (i == endIdx)
            {
                ss = reversed ? Math.Clamp(endCharIdx, 0, tLen) : 0;
                se = reversed ? tLen : Math.Clamp(endCharIdx, 0, tLen);
            }
            else
            {
                ss = 0; se = tLen;
            }

            info.Stb.SelectionStart = ss;
            info.Stb.SelectionEnd = se;
        }

        // ② 再清除不再参与的旧 STB（新选区已渲染，不会闪烁）
        for (int i = _selectedBlocks.Count - 1; i >= 0; i--)
        {
            if (!newSet.Contains(_selectedBlocks[i]))
            {
                _selectedBlocks[i].SelectionStart = 0;
                _selectedBlocks[i].SelectionEnd = 0;
            }
        }

        _selectedBlocks.Clear();
        _selectedBlocks.AddRange(newSet);
    }

    // ── 自动滚动 ─────────────────────────────────────────

    private void UpdateAutoScroll(PointerEventArgs e)
    {
        if (_scrollViewer == null) return;

        var ptInScroller = e.GetPosition(_scrollViewer);
        double h = _scrollViewer.Viewport.Height;

        if (ptInScroller.Y < AutoScrollEdge)
        {
            double t = Math.Clamp((AutoScrollEdge - ptInScroller.Y) / AutoScrollEdge, 0, 1);
            _autoScrollSpeed = -(AutoScrollBaseSpeed + (AutoScrollMaxSpeed - AutoScrollBaseSpeed) * t);
            EnsureAutoScrollRunning();
        }
        else if (ptInScroller.Y > h - AutoScrollEdge)
        {
            double t = Math.Clamp((ptInScroller.Y - h + AutoScrollEdge) / AutoScrollEdge, 0, 1);
            _autoScrollSpeed = AutoScrollBaseSpeed + (AutoScrollMaxSpeed - AutoScrollBaseSpeed) * t;
            EnsureAutoScrollRunning();
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void EnsureAutoScrollRunning()
    {
        if (_autoScrollTimer != null) return;
        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += AutoScrollTick;
        _autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        if (_autoScrollTimer == null) return;
        _autoScrollTimer.Stop();
        _autoScrollTimer.Tick -= AutoScrollTick;
        _autoScrollTimer = null;
        _autoScrollSpeed = 0;
    }

    private void AutoScrollTick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null || !_managedMode) { StopAutoScroll(); return; }

        double oldY = _scrollViewer.Offset.Y;
        double maxY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        double newY = Math.Clamp(oldY + _autoScrollSpeed, 0, maxY);
        if (Math.Abs(newY - oldY) < 0.5) return;

        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, newY);

        // 滚动后 pointer 在屏幕上不动，但相对容器的 Y 发生了偏移
        double delta = newY - oldY;
        _lastContainerPoint = new Point(_lastContainerPoint.X, _lastContainerPoint.Y + delta);
        UpdateSelectionTo(_lastContainerPoint);
    }

    // ── 辅助方法 ─────────────────────────────────────────

    /// <summary>收集容器内所有 STB，缓存坐标和文本长度，按 Y 排序</summary>
    private List<StbInfo> CollectSelectableTextBlocks()
    {
        var result = new List<StbInfo>();
        CollectRecursive(_container, result);
        result.Sort((a, b) => a.Top.CompareTo(b.Top));
        return result;
    }

    private void CollectRecursive(Visual parent, List<StbInfo> result)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is SelectableTextBlock stb)
            {
                try
                {
                    var pos = stb.TranslatePoint(new Point(0, 0), _container);
                    if (pos.HasValue)
                        result.Add(new StbInfo(stb, pos.Value.Y, pos.Value.X,
                            stb.Bounds.Height, GetEffectiveTextLength(stb)));
                }
                catch { /* 布局未完成时可能失败 */ }
            }
            else if (child is Visual v)
            {
                CollectRecursive(v, result);
            }
        }
    }

    /// <summary>在已排序的 STB 列表中找到包含给定 Y 坐标的索引，间隙中取最近的</summary>
    private static int FindStbIndexAtPoint(List<StbInfo> stbs, Point containerPoint)
    {
        if (stbs.Count == 0) return -1;

        int bestIdx = 0;
        double bestDist = double.MaxValue;

        for (int i = 0; i < stbs.Count; i++)
        {
            double top = stbs[i].Top;
            double bottom = top + stbs[i].Height;

            // 精确命中
            if (containerPoint.Y >= top && containerPoint.Y <= bottom)
                return i;

            // 计算到最近边界的距离
            double dist = containerPoint.Y < top
                ? top - containerPoint.Y
                : containerPoint.Y - bottom;

            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    /// <summary>将容器坐标转换为 STB 本地坐标后用 TextLayout.HitTestPoint 定位字符</summary>
    private static int HitTestCharIndex(StbInfo info, Point containerPoint)
    {
        var localPoint = new Point(containerPoint.X - info.Left, containerPoint.Y - info.Top);

        var layout = info.Stb.TextLayout;
        if (layout == null || layout.TextLines.Count == 0)
            return 0;

        var hit = layout.HitTestPoint(localPoint);
        return Math.Clamp(hit.TextPosition, 0, info.TextLength);
    }

    /// <summary>获取 STB 有效文本长度（兼容 Text 属性和 Inlines 两种设置方式）</summary>
    private static int GetEffectiveTextLength(SelectableTextBlock stb)
    {
        if (!string.IsNullOrEmpty(stb.Text))
            return stb.Text.Length;

        var lines = stb.TextLayout?.TextLines;
        if (lines is { Count: > 0 })
        {
            var last = lines[lines.Count - 1];
            return last.FirstTextSourceIndex + last.Length;
        }
        return 0;
    }

    /// <summary>
    /// 从 STB 中提取选中区间的文本（兼容 Text / Inlines 两种模式）。
    /// 当 MarkdownRenderer 通过 Inlines 设置内容时，stb.Text 为 null，
    /// stb.SelectedText 也可能返回 null，因此需要自行从 Inlines 还原全文再截取。
    /// </summary>
    private static string GetSelectedSubstring(SelectableTextBlock stb)
    {
        int ss = stb.SelectionStart, se = stb.SelectionEnd;
        if (ss == se) return string.Empty;

        // Path 1：Text 模式（直接截取）
        if (stb.Text is { Length: > 0 } text)
        {
            int a = Math.Clamp(Math.Min(ss, se), 0, text.Length);
            int b = Math.Clamp(Math.Max(ss, se), 0, text.Length);
            return text.Substring(a, b - a);
        }

        // Path 2：Inlines 模式（递归提取文本）
        if (stb.Inlines is { Count: > 0 } inlines)
        {
            var sb = new StringBuilder();
            foreach (var inline in inlines)
                AppendInlineText(sb, inline);

            var full = sb.ToString();
            int a = Math.Clamp(Math.Min(ss, se), 0, full.Length);
            int b = Math.Clamp(Math.Max(ss, se), 0, full.Length);
            return full.Substring(a, b - a);
        }

        return string.Empty;
    }

    private static void AppendInlineText(StringBuilder sb, Inline inline)
    {
        if (inline is Run run)
            sb.Append(run.Text);
        else if (inline is Span span && span.Inlines is { Count: > 0 } children)
            foreach (var child in children)
                AppendInlineText(sb, child);
    }

    /// <summary>向上遍历 visual tree 找到最近的 ScrollViewer</summary>
    private ScrollViewer? FindAncestorScrollViewer()
    {
        for (var v = _container.GetVisualParent(); v != null; v = v.GetVisualParent())
            if (v is ScrollViewer sv) return sv;
        return null;
    }

    private readonly record struct StbInfo(
        SelectableTextBlock Stb, double Top, double Left, double Height, int TextLength);
}
