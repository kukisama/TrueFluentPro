using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 跨控件文本选择协调器。
/// 解决 Avalonia SelectableTextBlock 只能逐块选择的问题：
/// 在 StackPanel 层拦截 PointerPressed/PointerMoved/PointerReleased，
/// 自行计算哪些子 STB 在拖选范围内，调用各 STB 的 SelectionStart/SelectionEnd
/// 实现视觉上的"跨块选择"，复制时拼接所有选中文本。
///
/// 灵感来源：Cherry Studio (React) 的浏览器原生 Selection API 可跨 DOM 节点选择，
/// 此组件在 Avalonia 桌面端实现类似效果。
/// </summary>
public sealed class SelectionManager
{
    private readonly Panel _container;
    private bool _isDragging;
    private Point _dragStartPoint;
    private SelectableTextBlock? _startStb;
    private int _startCharIndex;

    /// <summary>当前选区内所有参与选择的 STB（按视觉位置排序）</summary>
    private readonly List<SelectableTextBlock> _selectedBlocks = new();

    /// <summary>拖拽跨越此纵向像素数后才进入跨块选择模式</summary>
    private const double CrossBlockDragThreshold = 10;

    public SelectionManager(Panel container)
    {
        _container = container;

        // 使用隧道事件（AddHandler tunnel: true），在子控件处理之前拦截
        _container.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _container.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _container.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>获取当前跨块选中的完整文本（含换行分隔）</summary>
    public string GetSelectedText()
    {
        if (_selectedBlocks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var stb in _selectedBlocks)
        {
            var selected = stb.SelectedText;
            if (!string.IsNullOrEmpty(selected))
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(selected);
            }
        }
        return sb.ToString();
    }

    /// <summary>是否有跨块选择</summary>
    public bool HasCrossBlockSelection => _selectedBlocks.Count > 1 &&
        _selectedBlocks.Any(s => !string.IsNullOrEmpty(s.SelectedText));

    /// <summary>清除所有块的选择</summary>
    public void ClearSelection()
    {
        foreach (var stb in _selectedBlocks)
        {
            stb.SelectionStart = 0;
            stb.SelectionEnd = 0;
        }
        _selectedBlocks.Clear();
    }

    /// <summary>分离事件处理器（用于销毁时清理）</summary>
    public void Detach()
    {
        _container.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _container.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _container.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
    }

    // ── 事件处理 ─────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_container).Properties.IsLeftButtonPressed)
            return;

        _dragStartPoint = e.GetPosition(_container);
        _isDragging = true;

        // 清除之前的跨块选区
        ClearSelection();

        // 定位起始 STB 和字符位置
        _startStb = HitTestSelectableTextBlock(e.GetPosition(_container));
        _startCharIndex = _startStb != null ? GetCharIndexAtPoint(_startStb, e.GetPosition(_startStb)) : 0;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _startStb == null)
            return;

        var currentPoint = e.GetPosition(_container);

        // 如果拖拽距离很小，还在单个 STB 内操作，不干预
        var delta = currentPoint - _dragStartPoint;
        if (Math.Abs(delta.Y) < CrossBlockDragThreshold)
            return;

        // 进入跨块选择模式：拦截事件防止单个 STB 的默认选择
        e.Handled = true;

        var endStb = HitTestSelectableTextBlock(currentPoint);
        if (endStb == null)
            return;

        UpdateCrossBlockSelection(_startStb, _startCharIndex, endStb, e.GetPosition(endStb));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _selectedBlocks.Count > 1)
        {
            // 跨块选择完成，不阻止事件（让上下文菜单正常工作）
        }
        _isDragging = false;
    }

    // ── 核心：更新跨块选区 ───────────────────────────────

    private void UpdateCrossBlockSelection(
        SelectableTextBlock startStb, int startCharIdx,
        SelectableTextBlock endStb, Point endLocalPoint)
    {
        // 收集容器内所有 SelectableTextBlock 的视觉位置
        var allStbs = CollectSelectableTextBlocks();
        if (allStbs.Count == 0)
            return;

        // 找到 start 和 end 对应的索引
        int startIdx = allStbs.FindIndex(s => ReferenceEquals(s.Stb, startStb));
        int endIdx = allStbs.FindIndex(s => ReferenceEquals(s.Stb, endStb));
        if (startIdx < 0 || endIdx < 0)
            return;

        // 确保 start <= end
        bool reversed = startIdx > endIdx;
        int lo = Math.Min(startIdx, endIdx);
        int hi = Math.Max(startIdx, endIdx);

        // 先清除之前的选区
        foreach (var stb in _selectedBlocks)
        {
            stb.SelectionStart = 0;
            stb.SelectionEnd = 0;
        }
        _selectedBlocks.Clear();

        // 对范围内每个 STB 设置选区
        for (int i = lo; i <= hi; i++)
        {
            var stb = allStbs[i].Stb;
            var textLen = stb.Text?.Length ?? 0;
            if (textLen == 0)
                continue;

            _selectedBlocks.Add(stb);

            if (i == lo && i == hi)
            {
                // 起止在同一个 STB
                int endCharIdx = GetCharIndexAtPoint(stb, endLocalPoint);
                int a = Math.Min(startCharIdx, endCharIdx);
                int b = Math.Max(startCharIdx, endCharIdx);
                stb.SelectionStart = Math.Clamp(a, 0, textLen);
                stb.SelectionEnd = Math.Clamp(b, 0, textLen);
            }
            else if (i == (reversed ? hi : lo))
            {
                // 起始 STB：从 startCharIdx 选到末尾（或从开头选到 startCharIdx）
                if (reversed)
                {
                    stb.SelectionStart = 0;
                    stb.SelectionEnd = Math.Clamp(startCharIdx, 0, textLen);
                }
                else
                {
                    stb.SelectionStart = Math.Clamp(startCharIdx, 0, textLen);
                    stb.SelectionEnd = textLen;
                }
            }
            else if (i == (reversed ? lo : hi))
            {
                // 结束 STB：从开头选到当前鼠标位置（或从当前位置选到末尾）
                int endCharIdx = GetCharIndexAtPoint(stb, endLocalPoint);
                if (reversed)
                {
                    stb.SelectionStart = Math.Clamp(endCharIdx, 0, textLen);
                    stb.SelectionEnd = textLen;
                }
                else
                {
                    stb.SelectionStart = 0;
                    stb.SelectionEnd = Math.Clamp(endCharIdx, 0, textLen);
                }
            }
            else
            {
                // 中间的 STB：全选
                stb.SelectionStart = 0;
                stb.SelectionEnd = textLen;
            }
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────

    /// <summary>从容器中收集所有 SelectableTextBlock，按视觉纵坐标排序</summary>
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
                        result.Add(new StbInfo(stb, pos.Value.Y));
                }
                catch
                {
                    // TranslatePoint 可能在布局未完成时失败
                }
            }
            else if (child is Visual v)
            {
                CollectRecursive(v, result);
            }
        }
    }

    /// <summary>对容器坐标做 HitTest，找到最近的 SelectableTextBlock</summary>
    private SelectableTextBlock? HitTestSelectableTextBlock(Point containerPoint)
    {
        var allStbs = CollectSelectableTextBlocks();
        if (allStbs.Count == 0)
            return null;

        // 找到纵坐标最近的 STB
        SelectableTextBlock? best = null;
        double bestDist = double.MaxValue;

        foreach (var info in allStbs)
        {
            var stbBounds = info.Stb.Bounds;
            var globalPos = info.Stb.TranslatePoint(new Point(0, 0), _container);
            if (!globalPos.HasValue)
                continue;

            double stbTop = globalPos.Value.Y;
            double stbBottom = stbTop + stbBounds.Height;

            // 如果点在 STB 的纵向范围内
            if (containerPoint.Y >= stbTop && containerPoint.Y <= stbBottom)
            {
                best = info.Stb;
                break;
            }

            // 否则找最近的
            double dist = Math.Min(Math.Abs(containerPoint.Y - stbTop), Math.Abs(containerPoint.Y - stbBottom));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = info.Stb;
            }
        }

        return best;
    }

    /// <summary>估算在 STB 内指定本地坐标对应的字符索引（近似）</summary>
    private static int GetCharIndexAtPoint(SelectableTextBlock stb, Point localPoint)
    {
        var text = stb.Text;
        if (string.IsNullOrEmpty(text))
            return 0;

        // 使用 TextLayout 的 HitTest 能力进行精确字符定位
        var textLayout = stb.TextLayout;
        if (textLayout != null)
        {
            var hitResult = textLayout.HitTestPoint(localPoint);
            return Math.Clamp(hitResult.TextPosition, 0, text.Length);
        }

        // 降级：组合 X+Y 坐标按比例估算
        double yRatio = Math.Clamp(localPoint.Y / Math.Max(stb.Bounds.Height, 1), 0, 1);
        double xRatio = Math.Clamp(localPoint.X / Math.Max(stb.Bounds.Width, 1), 0, 1);
        // 对单行文本主要用 X 比例；多行文本综合 Y 和 X
        double lineCount = Math.Max(stb.Bounds.Height / 24.0, 1); // 近似行高 24px
        double combinedRatio = lineCount > 1.5
            ? yRatio * (1.0 - 1.0 / lineCount) + xRatio / lineCount
            : xRatio;
        return (int)(Math.Clamp(combinedRatio, 0, 1) * text.Length);
    }

    private readonly record struct StbInfo(SelectableTextBlock Stb, double Top);
}
