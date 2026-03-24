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

    private readonly Dictionary<string, Control> _createdSections = new(StringComparer.Ordinal);
    private bool _isUpdatingSelection;
    private string? _currentSectionName;

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

        // 默认选中第一项
        if (NavListBox.SelectedIndex < 0 && NavListBox.ItemCount > 0)
        {
            NavListBox.SelectedIndex = 0;
        }

        Dispatcher.UIThread.Post(ShowSelectedSection, DispatcherPriority.Loaded);
    }

    /// <summary>点击左侧导航 → 显示对应分区</summary>
    private void OnNavSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (e.AddedItems.Count == 0) return;

        ResetTransientSectionStates();

        var tag = GetSectionTagFromSelection(e);

        if (string.IsNullOrEmpty(tag)) return;

        ShowSection(tag);
    }

    private void ResetTransientSectionStates()
    {
        GetCreatedSection<SubscriptionSection>("Section_Subscription")?.ResetTransientUiState();
        GetCreatedSection<EndpointsSection>("Section_Endpoints")?.ResetTransientUiState();
    }

    private void ShowSelectedSection()
    {
        if (NavListBox.SelectedItem is not null)
        {
            var tag = GetSelectedSectionTag();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                ShowSection(tag);
            }
        }
    }

    private void ShowSection(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName) || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        EnsureNavSelection(sectionName);

        if (!_createdSections.TryGetValue(sectionName, out var section))
        {
            if (!SectionFactories.TryGetValue(sectionName, out var factory))
            {
                return;
            }

            section = factory();
            if (TryResolveSectionDataContext(sectionName, vm, out var targetDataContext))
            {
                section.DataContext = targetDataContext;
            }

            _createdSections[sectionName] = section;
        }

        if (SelectedSectionHost.Content != section)
        {
            SelectedSectionHost.Content = section;
        }

        _currentSectionName = sectionName;
        SettingsScroller.Offset = new Vector(SettingsScroller.Offset.X, 0);
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

        _isUpdatingSelection = true;
        NavListBox.SelectedIndex = targetIndex;
        _isUpdatingSelection = false;
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

    private string? GetSelectedSectionTag()
    {
        if (NavListBox.SelectedItem is ListBoxItem listBoxItem)
        {
            return listBoxItem.Tag as string;
        }

        return NavListBox.SelectedItem is { } item && NavListBox.ContainerFromItem(item) is ListBoxItem container
            ? container.Tag as string
            : _currentSectionName;
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
