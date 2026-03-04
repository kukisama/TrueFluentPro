using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace TrueFluentPro.Controls;

public enum InlineContextSelectionMode
{
    UseCurrentSelection,
    SelectHitItemThenOpen
}

/// <summary>
/// 为 ListBox 提供“非 Popup”的内嵌右键菜单控制器。
/// 负责：右键显示、Esc 关闭、外部点击关闭、边界裁剪。
/// </summary>
/// <typeparam name="TItem">列表项类型。</typeparam>
public sealed class InlineListContextMenuController<TItem> where TItem : class
{
    private readonly ListBox _listBox;
    private readonly Border _inlineMenuPanel;
    private readonly Func<TItem?> _selectedItemProvider;
    private readonly InlineContextSelectionMode _selectionMode;

    private bool _pendingRightClickOpen;
    private Point? _lastRightPressPosition;
    private Point? _lastRightReleasePosition;

    public InlineListContextMenuController(
        ListBox listBox,
        Border inlineMenuPanel,
        Func<TItem?> selectedItemProvider,
        InlineContextSelectionMode selectionMode = InlineContextSelectionMode.UseCurrentSelection)
    {
        _listBox = listBox;
        _inlineMenuPanel = inlineMenuPanel;
        _selectedItemProvider = selectedItemProvider;
        _selectionMode = selectionMode;
    }

    public TItem? CurrentItem { get; private set; }

    public event Action<TItem, Point>? MenuShown;

    public event Action<string>? MenuHidden;

    public void HandleListPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_listBox).Properties.IsRightButtonPressed)
        {
            HideMenu("left-or-non-right-press");
            return;
        }

        e.Handled = true;
        _pendingRightClickOpen = true;
        _lastRightPressPosition = e.GetPosition(_listBox);

        if (_selectionMode == InlineContextSelectionMode.SelectHitItemThenOpen)
        {
            var hitItem = ResolveHitItem(e.Source);
            if (hitItem != null && !ReferenceEquals(_listBox.SelectedItem, hitItem))
            {
                _listBox.SelectedItem = hitItem;
            }
        }

        CurrentItem = _selectedItemProvider();
    }

    public bool HandleListPointerReleased(PointerReleasedEventArgs e, out TItem? openedItem)
    {
        openedItem = null;
        _lastRightReleasePosition = e.GetPosition(_listBox);

        if (!_pendingRightClickOpen)
        {
            return false;
        }

        _pendingRightClickOpen = false;

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return false;
        }

        e.Handled = true;

        var selected = _selectedItemProvider();
        if (selected == null)
        {
            CurrentItem = null;
            return false;
        }

        CurrentItem = selected;
        if (!TryShowInternal(selected, out var shownAt))
        {
            return false;
        }

        openedItem = selected;
        MenuShown?.Invoke(selected, shownAt);
        return true;
    }

    public void HandleSelectionChanged()
    {
        HideMenu("selection-changed");
    }

    public void HandleAnyPointerPressed(PointerPressedEventArgs e)
    {
        if (!_inlineMenuPanel.IsVisible)
        {
            return;
        }

        if (IsSourceInsideInlineMenu(e.Source))
        {
            return;
        }

        HideMenu("outside-click");
    }

    public bool HandleAnyKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_inlineMenuPanel.IsVisible)
        {
            return false;
        }

        HideMenu("esc");
        e.Handled = true;
        return true;
    }

    public bool TryGetCurrentItem(out TItem? item)
    {
        item = CurrentItem;
        return item != null;
    }

    public void HideMenu(string reason)
    {
        if (!_inlineMenuPanel.IsVisible)
        {
            return;
        }

        _inlineMenuPanel.IsVisible = false;
        CurrentItem = null;
        MenuHidden?.Invoke(reason);
    }

    private bool TryShowInternal(TItem selectedItem, out Point shownAt)
    {
        shownAt = default;

        var pos = _lastRightReleasePosition ?? _lastRightPressPosition;
        if (pos == null)
        {
            return false;
        }

        var panelWidth = _inlineMenuPanel.Bounds.Width > 1 ? _inlineMenuPanel.Bounds.Width : 170;
        var panelHeight = _inlineMenuPanel.Bounds.Height > 1 ? _inlineMenuPanel.Bounds.Height : 42;

        var x = Math.Clamp(pos.Value.X + 8, 0, Math.Max(0, _listBox.Bounds.Width - panelWidth));
        var y = Math.Clamp(pos.Value.Y + 8, 0, Math.Max(0, _listBox.Bounds.Height - panelHeight));

        _inlineMenuPanel.DataContext = selectedItem;
        _inlineMenuPanel.RenderTransform = new TranslateTransform(x, y);
        _inlineMenuPanel.IsVisible = true;

        shownAt = new Point(x, y);
        return true;
    }

    private bool IsSourceInsideInlineMenu(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        Visual? current = visual;
        while (current != null)
        {
            if (ReferenceEquals(current, _inlineMenuPanel))
            {
                return true;
            }

            current = current.GetVisualParent() as Visual;
        }

        return false;
    }

    private static TItem? ResolveHitItem(object? source)
    {
        if (source is TItem item)
        {
            return item;
        }

        if (source is Control control)
        {
            var itemContainer = control.FindAncestorOfType<ListBoxItem>();
            return itemContainer?.DataContext as TItem;
        }

        return null;
    }
}
