using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrueFluentPro.Views.Settings;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private const double SectionTopPadding = 12;
    private const double InitialViewportLoadPadding = 12;
    private const double NearBottomLoadThreshold = 24;
    private static readonly IReadOnlyDictionary<string, Func<Control>> SectionFactories
        = new Dictionary<string, Func<Control>>(StringComparer.Ordinal)
    {
        ["Section_Subscription"] = () => new SubscriptionSection(),
        ["Section_Endpoints"] = () => new EndpointsSection(),
        ["Section_ModelSelection"] = () => new ModelSelectionSection(),
        ["Section_Storage"] = () => new StorageSection(),
        ["Section_Insight"] = () => new InsightSection(),
        ["Section_WebSearch"] = () => new WebSearchSection(),
        ["Section_Review"] = () => new ReviewSection(),
        ["Section_ImageGen"] = () => new ImageGenSection(),
        ["Section_VideoGen"] = () => new VideoGenSection(),
        ["Section_Audio"] = () => new AudioSection(),
        ["Section_Recognition"] = () => new RecognitionSection(),
        ["Section_Text"] = () => new TextSection(),
        ["Section_Transfer"] = () => new TransferSection(),
        ["Section_About"] = () => new AboutSection()
    };
    private static readonly IReadOnlyDictionary<string, Func<MainWindowViewModel, object>> SectionDataContextResolvers
        = new Dictionary<string, Func<MainWindowViewModel, object>>(StringComparer.Ordinal)
    {
        ["Section_Subscription"] = vm => vm.Settings.SubscriptionVM,
        ["Section_Endpoints"] = vm => vm.Settings.EndpointsVM,
        ["Section_ModelSelection"] = vm => vm.Settings,
        ["Section_Storage"] = vm => vm.Settings.StorageVM,
        ["Section_Insight"] = vm => vm.Settings.InsightVM,
        ["Section_WebSearch"] = vm => vm.Settings.WebSearchVM,
        ["Section_Review"] = vm => vm.Settings.ReviewVM,
        ["Section_ImageGen"] = vm => vm.Settings.ImageGenVM,
        ["Section_VideoGen"] = vm => vm.Settings.VideoGenVM,
        ["Section_Audio"] = vm => vm.AudioDevices,
        ["Section_Recognition"] = vm => vm.Settings.RecognitionVM,
        ["Section_Text"] = vm => vm.Settings.TextVM,
        ["Section_Transfer"] = vm => vm.Settings.TransferVM,
        ["Section_About"] = vm => vm.Settings.AboutVM
    };
    private static readonly IReadOnlyList<string> SectionLoadOrder =
    [
        "Section_Subscription",
        "Section_Endpoints",
        "Section_ModelSelection",
        "Section_Storage",
        "Section_Insight",
        "Section_WebSearch",
        "Section_Review",
        "Section_ImageGen",
        "Section_VideoGen",
        "Section_Audio",
        "Section_Recognition",
        "Section_Text",
        "Section_Transfer",
        "Section_About"
    ];

    private ContentControl[]? _sectionHosts;
    private bool _isUpdatingSelectionFromScroll;
    private string? _pendingNavSectionName;
    private bool _hasInitializedVisibleSections;
    private readonly Dictionary<string, Control> _createdSections = new(StringComparer.Ordinal);

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_createdSections.Count == 0)
        {
            return;
        }

        foreach (var pair in _createdSections)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                pair.Value.ClearValue(DataContextProperty);
                continue;
            }

            if (TryResolveSectionDataContext(pair.Key, vm, out var resolvedDataContext))
            {
                pair.Value.DataContext = resolvedDataContext;
            }
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _sectionHosts = SectionsPanel.Children
            .OfType<ContentControl>()
            .Where(c => c.Name?.StartsWith("Section_") == true)
            .ToArray();

        // 默认选中第一项
        if (NavListBox.SelectedIndex < 0 && NavListBox.ItemCount > 0)
            NavListBox.SelectedIndex = 0;

        if (_hasInitializedVisibleSections)
        {
            return;
        }

        _hasInitializedVisibleSections = true;
        Dispatcher.UIThread.Post(EnsureInitialViewportSectionsLoaded, DispatcherPriority.Loaded);
    }

    /// <summary>点击左侧导航 → 滚动到对应分区</summary>
    private void OnNavSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelectionFromScroll) return;
        if (e.AddedItems.Count == 0) return;

        ResetTransientSectionStates();

        var tag = GetSectionTagFromSelection(e);

        if (string.IsNullOrEmpty(tag)) return;

        _pendingNavSectionName = tag;
        EnsureNavSelection(tag);
        EnsureSectionsCreatedThrough(tag);
        UpdateLayout();
        Dispatcher.UIThread.Post(() => ScrollToSection(tag), DispatcherPriority.Loaded);
    }

    /// <summary>右侧滚动 → 更新左侧导航高亮</summary>
    private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        EnsureAdditionalSectionsNearViewport();

        if (TryHandlePendingNavigation())
        {
            return;
        }

        SyncNavSelectionToActiveSection();
    }

    private Control? GetActiveSectionForViewport()
    {
        if (_sectionHosts == null || _sectionHosts.Length == 0)
        {
            return null;
        }

        var loadedHosts = _sectionHosts.Where(host => host.Content != null).ToArray();
        if (loadedHosts.Length == 0)
        {
            return null;
        }

        var viewportHeight = SettingsScroller.Bounds.Height;
        if (viewportHeight <= 0)
        {
            return loadedHosts[0];
        }

        var remainingScroll = SettingsScroller.Extent.Height - viewportHeight - SettingsScroller.Offset.Y;
        var isNearBottom = remainingScroll <= 24;
        var anchorY = SectionTopPadding;

        Control? firstVisibleSection = null;
        Control? lastVisibleSection = null;

        foreach (var section in loadedHosts)
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

        return firstVisibleSection ?? loadedHosts.LastOrDefault();
    }

    private void ResetTransientSectionStates()
    {
        GetCreatedSection<SubscriptionSection>("Section_Subscription")?.ResetTransientUiState();
        GetCreatedSection<EndpointsSection>("Section_Endpoints")?.ResetTransientUiState();
    }

    private void EnsureAdditionalSectionsNearViewport()
    {
        if (_sectionHosts == null || _sectionHosts.Length == 0)
        {
            return;
        }

        var viewportHeight = SettingsScroller.Bounds.Height;
        if (viewportHeight <= 0)
        {
            return;
        }

        var remainingScroll = SettingsScroller.Extent.Height - viewportHeight - SettingsScroller.Offset.Y;
        while (remainingScroll <= NearBottomLoadThreshold && TryCreateNextSection())
        {
            UpdateLayout();
            remainingScroll = SettingsScroller.Extent.Height - viewportHeight - SettingsScroller.Offset.Y;
        }
    }

    private void EnsureInitialViewportSectionsLoaded()
    {
        if (_sectionHosts == null || _sectionHosts.Length == 0)
        {
            return;
        }

        if (_createdSections.Count == 0)
        {
            var firstHostName = _sectionHosts[0].Name;
            if (!string.IsNullOrWhiteSpace(firstHostName))
            {
                EnsureSectionCreated(firstHostName);
                UpdateLayout();
            }
        }

        var viewportHeight = SettingsScroller.Bounds.Height;
        if (viewportHeight <= 0)
        {
            Dispatcher.UIThread.Post(EnsureInitialViewportSectionsLoaded, DispatcherPriority.Loaded);
            return;
        }

        var guard = 0;
        while (ShouldLoadMoreInitialSections(viewportHeight) && guard++ < SectionLoadOrder.Count && TryCreateNextSection())
        {
            UpdateLayout();
        }
    }

    private bool ShouldLoadMoreInitialSections(double viewportHeight)
    {
        if (_sectionHosts == null || _sectionHosts.Length == 0)
        {
            return false;
        }

        var loadedHosts = _sectionHosts.Where(host => host.Content != null).ToList();
        if (loadedHosts.Count == 0)
        {
            return true;
        }

        var lastLoadedHost = loadedHosts[^1];
        var transform = lastLoadedHost.TransformToVisual(SettingsScroller);
        if (transform == null)
        {
            return true;
        }

        var bottom = transform.Value.Transform(new Point(0, 0)).Y + lastLoadedHost.Bounds.Height;
        return bottom < viewportHeight + InitialViewportLoadPadding;
    }

    private bool TryCreateNextSection()
    {
        if (_sectionHosts == null)
        {
            return false;
        }

        var nextHost = _sectionHosts.FirstOrDefault(host => host.Content == null);
        if (nextHost == null || string.IsNullOrWhiteSpace(nextHost.Name))
        {
            return false;
        }

        return EnsureSectionCreated(nextHost.Name);
    }

    private void EnsureSectionsCreatedThrough(string sectionName)
    {
        foreach (var currentName in SectionLoadOrder)
        {
            EnsureSectionCreated(currentName);
            if (string.Equals(currentName, sectionName, StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    private bool EnsureSectionCreated(string sectionName)
    {
        if (_createdSections.ContainsKey(sectionName))
        {
            return false;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return false;
        }

        var host = GetSectionHost(sectionName);
        if (host == null || !SectionFactories.TryGetValue(sectionName, out var factory))
        {
            return false;
        }

        var section = factory();
        if (TryResolveSectionDataContext(sectionName, vm, out var targetDataContext))
        {
            section.DataContext = targetDataContext;
        }

        host.Content = section;
        _createdSections[sectionName] = section;
        return true;
    }

    private void ScrollToSection(string sectionName)
    {
        var target = GetSectionHost(sectionName);
        if (target == null || target.Content == null)
        {
            _pendingNavSectionName = null;
            return;
        }

        EnsureNavSelection(sectionName);
        target.UpdateLayout();
        UpdateLayout();

        if (!TryGetSectionOffsetY(target, out var actualOffset))
        {
            _pendingNavSectionName = null;
            return;
        }

        SettingsScroller.Offset = new Avalonia.Vector(SettingsScroller.Offset.X, actualOffset);
    }

    private string? GetSectionTagFromSelection(SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0)
        {
            return null;
        }

        if (e.AddedItems[0] is ListBoxItem listBoxItem)
        {
            return listBoxItem.Tag as string;
        }

        return e.AddedItems[0] is { } item && NavListBox.ContainerFromItem(item) is ListBoxItem container
            ? container.Tag as string
            : null;
    }

    private bool TryHandlePendingNavigation()
    {
        if (string.IsNullOrWhiteSpace(_pendingNavSectionName))
        {
            return false;
        }

        EnsureNavSelection(_pendingNavSectionName);

        if (IsSectionActiveForViewport(_pendingNavSectionName))
        {
            _pendingNavSectionName = null;
        }

        return true;
    }

    private void SyncNavSelectionToActiveSection()
    {
        var activeSection = GetActiveSectionForViewport();
        if (activeSection == null || string.IsNullOrWhiteSpace(activeSection.Name))
        {
            return;
        }

        var targetIndex = GetNavIndexBySectionName(activeSection.Name);
        if (targetIndex < 0 || NavListBox.SelectedIndex == targetIndex)
        {
            return;
        }

        ResetTransientSectionStates();
        _isUpdatingSelectionFromScroll = true;
        NavListBox.SelectedIndex = targetIndex;
        _isUpdatingSelectionFromScroll = false;
    }

    private bool TryGetSectionOffsetY(Control target, out double actualOffset)
    {
        actualOffset = 0;
        var transform = target.TransformToVisual(SettingsScroller);
        if (transform == null)
        {
            return false;
        }

        var pos = transform.Value.Transform(new Point(0, 0));
        var requestedOffset = SettingsScroller.Offset.Y + pos.Y - SectionTopPadding;
        var maxOffset = Math.Max(0, SettingsScroller.Extent.Height - SettingsScroller.Bounds.Height);
        actualOffset = Math.Clamp(requestedOffset, 0, maxOffset);
        return true;
    }

    private bool IsSectionActiveForViewport(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return false;
        }

        var activeSection = GetActiveSectionForViewport();
        return string.Equals(activeSection?.Name, sectionName, StringComparison.Ordinal);
    }

    private void EnsureNavSelection(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return;
        }

        var targetIndex = GetNavIndexBySectionName(sectionName);
        if (targetIndex < 0 || NavListBox.SelectedIndex == targetIndex)
        {
            return;
        }

        _isUpdatingSelectionFromScroll = true;
        NavListBox.SelectedIndex = targetIndex;
        _isUpdatingSelectionFromScroll = false;
    }

    private int GetNavIndexBySectionName(string sectionName)
    {
        for (int i = 0; i < NavListBox.ItemCount; i++)
        {
            if (NavListBox.ContainerFromIndex(i) is ListBoxItem li
                && string.Equals(li.Tag as string, sectionName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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

    private ContentControl? GetSectionHost(string sectionName)
    {
        return _sectionHosts?.FirstOrDefault(host => string.Equals(host.Name, sectionName, StringComparison.Ordinal));
    }

    private T? GetCreatedSection<T>(string sectionName) where T : Control
    {
        return _createdSections.TryGetValue(sectionName, out var control) ? control as T : null;
    }

    private static bool TryResolveSectionDataContext(string sectionName, MainWindowViewModel vm, out object? dataContext)
    {
        dataContext = SectionDataContextResolvers.TryGetValue(sectionName, out var resolver)
            ? resolver(vm)
            : null;

        return dataContext != null;
    }
}
