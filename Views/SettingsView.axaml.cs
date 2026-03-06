using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private bool _suppressScrollSync;
    private Control[]? _sectionControls;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 缓存所有 Section UserControl
        _sectionControls = SectionsPanel.Children
            .OfType<UserControl>()
            .Where(c => c.Name?.StartsWith("Section_") == true)
            .ToArray<Control>();

        // 默认选中第一项
        if (NavListBox.SelectedIndex < 0 && NavListBox.ItemCount > 0)
            NavListBox.SelectedIndex = 0;
    }

    /// <summary>点击左侧导航 → 滚动到对应分区</summary>
    private void OnNavSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressScrollSync) return;
        if (e.AddedItems.Count == 0) return;

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
            var newOffset = SettingsScroller.Offset.Y + pos.Y;
            _suppressScrollSync = true;
            SettingsScroller.Offset = new Avalonia.Vector(SettingsScroller.Offset.X, Math.Max(0, newOffset));
            _suppressScrollSync = false;
        }
    }

    /// <summary>右侧滚动 → 更新左侧导航高亮</summary>
    private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_sectionControls == null || _sectionControls.Length == 0) return;

        Control? topVisible = null;
        double bestY = double.NegativeInfinity;

        foreach (var section in _sectionControls)
        {
            var transform = section.TransformToVisual(SettingsScroller);
            if (transform == null) continue;

            var pos = transform.Value.Transform(new Point(0, 0));
            if (pos.Y <= 40 && pos.Y > bestY)
            {
                topVisible = section;
                bestY = pos.Y;
            }
        }

        if (topVisible == null)
        {
            foreach (var section in _sectionControls)
            {
                var transform = section.TransformToVisual(SettingsScroller);
                if (transform == null) continue;
                var pos = transform.Value.Transform(new Point(0, 0));
                if (pos.Y >= 0)
                {
                    topVisible = section;
                    break;
                }
            }
        }

        if (topVisible == null) return;

        var targetIndex = -1;
        for (int i = 0; i < NavListBox.ItemCount; i++)
        {
            if (NavListBox.ContainerFromIndex(i) is ListBoxItem li && li.Tag as string == topVisible.Name)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex >= 0 && NavListBox.SelectedIndex != targetIndex)
        {
            _suppressScrollSync = true;
            NavListBox.SelectedIndex = targetIndex;
            _suppressScrollSync = false;
        }
    }
}
