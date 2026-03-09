using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private const double SectionTopPadding = 12;
    private static readonly IReadOnlyDictionary<string, Func<MainWindowViewModel, object>> LazySectionDataContextResolvers
        = new Dictionary<string, Func<MainWindowViewModel, object>>(StringComparer.Ordinal)
    {
        ["Section_ModelSelection"] = vm => vm.Settings,
        ["Section_Insight"] = vm => vm.Settings.InsightVM,
        ["Section_Review"] = vm => vm.Settings.ReviewVM,
        ["Section_ImageGen"] = vm => vm.Settings.ImageGenVM,
        ["Section_VideoGen"] = vm => vm.Settings.VideoGenVM,
        ["Section_Transfer"] = vm => vm.Settings.TransferVM,
        ["Section_About"] = vm => vm.Settings.AboutVM
    };
    private static readonly HashSet<string> LazySectionNames = new(LazySectionDataContextResolvers.Keys, StringComparer.Ordinal);

    private Control[]? _sectionControls;
    private bool _isUpdatingSelectionFromScroll;
    private double? _pendingNavScrollOffsetY;
    private readonly HashSet<string> _boundLazySections = new(StringComparer.Ordinal);

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _boundLazySections.Clear();

        if (_sectionControls == null)
        {
            return;
        }

        foreach (var section in _sectionControls)
        {
            if (!LazySectionNames.Contains(section.Name ?? string.Empty))
            {
                continue;
            }

            section.ClearValue(DataContextProperty);
        }

        EnsureVisibleSectionDataContexts();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 缓存所有 Section UserControl
        _sectionControls = SectionsPanel.Children
            .OfType<Control>()
            .Where(c => c.Name?.StartsWith("Section_") == true)
            .ToArray<Control>();

        EnsureVisibleSectionDataContexts();

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

        EnsureLazySectionDataContext(target);

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

        EnsureVisibleSectionDataContexts();

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

    private void EnsureVisibleSectionDataContexts()
    {
        if (_sectionControls == null || _sectionControls.Length == 0)
        {
            return;
        }

        var viewportHeight = SettingsScroller.Bounds.Height;
        if (viewportHeight <= 0)
        {
            foreach (var section in _sectionControls.Take(4))
            {
                EnsureLazySectionDataContext(section);
            }

            return;
        }

        foreach (var section in _sectionControls)
        {
            var transform = section.TransformToVisual(SettingsScroller);
            if (transform == null)
            {
                continue;
            }

            var top = transform.Value.Transform(new Point(0, 0)).Y;
            var bottom = top + section.Bounds.Height;
            if (bottom > -48 && top < viewportHeight + 48)
            {
                EnsureLazySectionDataContext(section);
            }
        }
    }

    private void EnsureLazySectionDataContext(Control? section)
    {
        if (section == null || string.IsNullOrWhiteSpace(section.Name))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!TryResolveLazySectionDataContext(section.Name, vm, out var targetDataContext))
        {
            return;
        }

        if (_boundLazySections.Contains(section.Name) && ReferenceEquals(section.DataContext, targetDataContext))
        {
            return;
        }

        section.DataContext = targetDataContext;
        _boundLazySections.Add(section.Name);
    }

    private void OpenConfigLocationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        try
        {
            var configPath = vm.ConfigVM.GetConfigFilePath();
            if (string.IsNullOrWhiteSpace(configPath))
            {
                vm.Settings.ReportStatus("未找到配置文件路径");
                return;
            }

            var directory = Path.GetDirectoryName(configPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                vm.Settings.ReportStatus("配置目录不存在");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{configPath}\"",
                UseShellExecute = true
            });

            vm.Settings.ReportStatus("已打开配置文件位置");
        }
        catch (Exception ex)
        {
            vm.Settings.ReportStatus($"打开配置文件位置失败: {ex.Message}");
        }
    }

    private static bool TryResolveLazySectionDataContext(string sectionName, MainWindowViewModel vm, out object? dataContext)
    {
        dataContext = LazySectionDataContextResolvers.TryGetValue(sectionName, out var resolver)
            ? resolver(vm)
            : null;

        return dataContext != null;
    }
}
