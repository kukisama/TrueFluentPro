using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private const double SectionTopPadding = 12;

    private Control[]? _sectionControls;
    private bool _isUpdatingSelectionFromScroll;
    private double? _pendingNavScrollOffsetY;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 缓存所有 Section UserControl
        _sectionControls = SectionsPanel.Children
            .OfType<Control>()
            .Where(c => c.Name?.StartsWith("Section_") == true)
            .ToArray<Control>();

        // 默认选中第一项
        if (NavListBox.SelectedIndex < 0 && NavListBox.ItemCount > 0)
            NavListBox.SelectedIndex = 0;
    }

    /// <summary>点击左侧导航 → 滚动到对应分区</summary>
    private void OnNavSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelectionFromScroll) return;
        if (e.AddedItems.Count == 0) return;

        ResetTransientSectionStates();

        string? tag = null;
        if (e.AddedItems[0] is ListBoxItem lbi)
            tag = lbi.Tag as string;
        else if (e.AddedItems[0] is { } item && NavListBox.ContainerFromItem(item) is ListBoxItem container)
            tag = container.Tag as string;

        if (string.IsNullOrEmpty(tag)) return;

        var target = _sectionControls?.FirstOrDefault(c => c.Name == tag);
        if (target == null) return;

        var transform = target.TransformToVisual(SettingsScroller);
        if (transform != null)
        {
            var pos = transform.Value.Transform(new Point(0, 0));
            var requestedOffset = SettingsScroller.Offset.Y + pos.Y - SectionTopPadding;
            var maxOffset = Math.Max(0, SettingsScroller.Extent.Height - SettingsScroller.Bounds.Height);
            var actualOffset = Math.Clamp(requestedOffset, 0, maxOffset);
            _pendingNavScrollOffsetY = actualOffset;
            SettingsScroller.Offset = new Avalonia.Vector(SettingsScroller.Offset.X, actualOffset);
        }
    }

    /// <summary>右侧滚动 → 更新左侧导航高亮</summary>
    private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_pendingNavScrollOffsetY.HasValue)
        {
            var pendingOffsetY = _pendingNavScrollOffsetY.Value;
            _pendingNavScrollOffsetY = null;

            if (Math.Abs(SettingsScroller.Offset.Y - pendingOffsetY) <= 0.5)
            {
                return;
            }
        }

        var activeSection = GetActiveSectionForViewport();
        if (activeSection == null) return;

        var targetIndex = -1;
        for (int i = 0; i < NavListBox.ItemCount; i++)
        {
            if (NavListBox.ContainerFromIndex(i) is ListBoxItem li && li.Tag as string == activeSection.Name)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex >= 0 && NavListBox.SelectedIndex != targetIndex)
        {
            ResetTransientSectionStates();
            _isUpdatingSelectionFromScroll = true;
            NavListBox.SelectedIndex = targetIndex;
            _isUpdatingSelectionFromScroll = false;
        }
    }

    private Control? GetActiveSectionForViewport()
    {
        if (_sectionControls == null || _sectionControls.Length == 0)
        {
            return null;
        }

        var viewportHeight = SettingsScroller.Bounds.Height;
        if (viewportHeight <= 0)
        {
            return _sectionControls[0];
        }

        var remainingScroll = SettingsScroller.Extent.Height - viewportHeight - SettingsScroller.Offset.Y;
        var isNearBottom = remainingScroll <= 24;
        var anchorY = SectionTopPadding;

        Control? firstVisibleSection = null;
        Control? lastVisibleSection = null;

        foreach (var section in _sectionControls)
        {
            var transform = section.TransformToVisual(SettingsScroller);
            if (transform == null)
            {
                continue;
            }

            var top = transform.Value.Transform(new Point(0, 0)).Y;
            var bottom = top + section.Bounds.Height;
            var isVisible = bottom > 0 && top < viewportHeight;

            if (!isVisible)
            {
                continue;
            }

            firstVisibleSection ??= section;
            lastVisibleSection = section;

            if (top <= anchorY && bottom > anchorY)
            {
                return section;
            }
        }

        if (isNearBottom && lastVisibleSection != null)
        {
            return lastVisibleSection;
        }

        return firstVisibleSection ?? _sectionControls.LastOrDefault();
    }

    private void ResetTransientSectionStates()
    {
        Section_Subscription?.ResetTransientUiState();
        Section_Endpoints?.ResetTransientUiState();
    }
}
