using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private bool _suppressScrollSync;
    private Border[]? _sectionBorders;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
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

        // 绑定 Preset/Review 编辑器到 SettingsVM
        if (DataContext is MainWindowViewModel vm)
        {
            PresetButtonsEditorControl.Items = vm.Settings.PresetButtons;
            ReviewSheetsEditorControl.Items = vm.Settings.ReviewSheets;

            // 初始化字号下拉
            SelectFontSizeComboBox(vm.Settings.DefaultFontSize);
        }
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
        if (target == null) return;

        // 直接计算目标在 ScrollViewer 内容中的偏移量并滚动，避免 BringIntoView 在元素已可见时不滚动
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
        if (_sectionBorders == null || _sectionBorders.Length == 0) return;

        Border? topVisible = null;
        double bestY = double.NegativeInfinity;

        foreach (var section in _sectionBorders)
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

    // ═══ 事件处理 ═══

    private async void BrowseSessionDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择会话目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null) return;
        var path = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel vm)
        {
            vm.Settings.SessionDirectory = path;
        }
    }

    private void FontSizeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DefaultFontSizeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var size) &&
            DataContext is MainWindowViewModel vm)
        {
            vm.Settings.DefaultFontSize = size;
        }
    }

    private void SelectFontSizeComboBox(int fontSize)
    {
        for (var i = 0; i < DefaultFontSizeComboBox.Items.Count; i++)
        {
            if (DefaultFontSizeComboBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == fontSize.ToString())
            {
                DefaultFontSizeComboBox.SelectedIndex = i;
                return;
            }
        }
        // Fallback: select first item if requested font size not found
        if (DefaultFontSizeComboBox.Items.Count > 0)
            DefaultFontSizeComboBox.SelectedIndex = 0;
    }

    private void AddModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // 添加一个默认模型
        vm.Settings.AddModelToSelectedEndpoint(
            "new-model", "", "", "", ModelCapability.Text);
    }

    private void RemoveModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AiModelEntry model &&
            DataContext is MainWindowViewModel vm)
        {
            vm.Settings.RemoveModelFromSelectedEndpoint(model);
        }
    }

    /// <summary>模型字段编辑完成后同步保存</summary>
    private void ModelField_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Settings.NotifyModelChanged();
    }

    /// <summary>模型能力下拉框选中变化后同步到模型</summary>
    private void ModelCapability_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not AiModelEntry model) return;
        if (combo.SelectedItem is not ComboBoxItem selected) return;
        var tag = selected.Tag?.ToString();
        model.Capabilities = tag switch
        {
            "Image" => ModelCapability.Image,
            "Video" => ModelCapability.Video,
            _ => ModelCapability.Text,
        };
        if (DataContext is MainWindowViewModel vm)
            vm.Settings.NotifyModelChanged();
    }
}
