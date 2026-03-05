using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private bool _suppressScrollSync;
    private Border[]? _sectionBorders;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 缓存所有 Section Border
        _sectionBorders = SectionsPanel.Children
            .OfType<Border>()
            .Where(b => b.Name?.StartsWith("Section_") == true)
            .ToArray();

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

        var target = _sectionBorders?.FirstOrDefault(b => b.Name == tag);
        target?.BringIntoView();
    }

    /// <summary>右侧滚动 → 更新左侧导航高亮</summary>
    private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_sectionBorders == null || _sectionBorders.Length == 0) return;

        // 找到当前可见区域中最靠上的、且未完全滚出视口的分区
        Border? topVisible = null;
        double bestY = double.NegativeInfinity;

        foreach (var section in _sectionBorders)
        {
            var transform = section.TransformToVisual(SettingsScroller);
            if (transform == null) continue;

            var pos = transform.Value.Transform(new Point(0, 0));
            // 选择 Y <= 40（已滚过或刚到顶部）中 Y 值最大（最接近可视顶部）的分区
            if (pos.Y <= 40 && pos.Y > bestY)
            {
                topVisible = section;
                bestY = pos.Y;
            }
        }

        // 若没有分区在顶部以上，选第一个可见分区
        if (topVisible == null)
        {
            foreach (var section in _sectionBorders)
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

        // 在 NavListBox 中找到匹配 Tag 的项
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
