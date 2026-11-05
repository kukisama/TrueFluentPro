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
        ["Section_Endpoints"] = () => new EndpointsSection(),
        ["Section_ModelSelection"] = () => new ModelSelectionSection(),
        ["Section_Storage"] = () => new StorageSection(),
        ["Section_Insight"] = () => new InsightSection(),
        ["Section_WebSearch"] = () => new WebSearchSection(),
        ["Section_Review"] = () => new ReviewSection(),
        ["Section_ImageGen"] = () => new ImageGenSection(),
        ["Section_VideoGen"] = () => new VideoGenSection(),
        ["Section_Audio"] = () => new AudioSection(),
        ["Section_AudioLab"] = () => new AudioLabSection(),
        ["Section_Recognition"] = () => new RecognitionSection(),
        ["Section_VoiceIme"] = () => new VoiceImeSection(),
        ["Section_Text"] = () => new TextSection(),
        ["Section_Transfer"] = () => new TransferSection(),
        ["Section_About"] = () => new AboutSection(),
        ["Section_Cloud"] = () => new CloudSection()
    };
    private static readonly IReadOnlyDictionary<string, Func<MainWindowViewModel, object>> SectionDataContextResolvers
        = new Dictionary<string, Func<MainWindowViewModel, object>>(StringComparer.Ordinal)
    {
        ["Section_Endpoints"] = vm => vm.Settings.EndpointsVM,
        ["Section_ModelSelection"] = vm => vm.Settings,
        ["Section_Storage"] = vm => vm.Settings.StorageVM,
        ["Section_Insight"] = vm => vm.Settings.InsightVM,
        ["Section_WebSearch"] = vm => vm.Settings.WebSearchVM,
        ["Section_Review"] = vm => vm.Settings.ReviewVM,
        ["Section_ImageGen"] = vm => vm.Settings.ImageGenVM,
        ["Section_VideoGen"] = vm => vm.Settings.VideoGenVM,
        ["Section_Audio"] = vm => vm.AudioDevices,
        ["Section_AudioLab"] = vm => vm.Settings.AudioLabVM,
        ["Section_Recognition"] = vm => vm.Settings.RecognitionVM,
        ["Section_VoiceIme"] = vm => vm.Settings.VoiceImeVM,
        ["Section_Text"] = vm => vm.Settings.TextVM,
        ["Section_Transfer"] = vm => vm.Settings.TransferVM,
        ["Section_About"] = vm => vm.Settings.AboutVM,
        ["Section_Cloud"] = vm => vm.Settings.CloudVM
    };

    private readonly Dictionary<string, Control> _createdSections = new(StringComparer.Ordinal);
    private bool _isUpdatingSelection;
    private string? _currentSectionName;
    private ListBox? _sectionNavigation;
    private ScrollViewer? _sectionScroller;
    private ContentControl? _sectionHost;

    public SettingsView() : this(true)
    {
    }

    /// <summary>派生呈现自建外层树，不先构造一个隐藏的经典设置页面。</summary>
    protected SettingsView(bool loadDefaultView)
    {
        if (loadDefaultView)
        {
            InitializeComponent();
            InitializeSections(NavListBox, SettingsScroller, SelectedSectionHost);
        }
    }

    /// <summary>复用同一分区工厂与 VM 映射，仅由各外观提供自己的导航和单分区宿主。</summary>
    protected void InitializeSections(ListBox navigation, ScrollViewer scroller, ContentControl sectionHost)
    {
        if (_sectionNavigation != null)
            _sectionNavigation.SelectionChanged -= OnNavSectionChanged;
        _sectionNavigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _sectionScroller = scroller ?? throw new ArgumentNullException(nameof(scroller));
        _sectionHost = sectionHost ?? throw new ArgumentNullException(nameof(sectionHost));
        // 经典 XAML 已订阅此事件；先移除再订阅，两个入口均只连接一次。
        _sectionNavigation.SelectionChanged -= OnNavSectionChanged;
        _sectionNavigation.SelectionChanged += OnNavSectionChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

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
        ShowSelectedSection();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 默认选中第一项
        if (_sectionNavigation == null) return;
        if (_sectionNavigation.SelectedIndex < 0 && _sectionNavigation.ItemCount > 0)
        {
            _sectionNavigation.SelectedIndex = 0;
        }

        Dispatcher.UIThread.Post(ShowSelectedSection, DispatcherPriority.Loaded);
    }

    /// <summary>点击左侧导航 → 显示对应分区</summary>
    protected void OnNavSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_sectionNavigation == null) return;
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
        if (_sectionNavigation?.SelectedItem is not null)
        {
            var tag = GetSelectedSectionTag();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                ShowSection(tag);
            }
        }
    }

    protected void ShowSection(string sectionName)
    {
        if (_sectionNavigation == null || _sectionHost == null || _sectionScroller == null
            || string.IsNullOrWhiteSpace(sectionName) || DataContext is not MainWindowViewModel vm
            || !SectionFactories.ContainsKey(sectionName))
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

        PrepareSection(sectionName, section);
        if (_sectionHost.Content != section)
        {
            _sectionHost.Content = section;
        }

        _currentSectionName = sectionName;
        _sectionScroller.Offset = new Vector(_sectionScroller.Offset.X, 0);
        OnSectionShown(sectionName);

        if (sectionName == "Section_VoiceIme")
        {
            _ = vm.Settings.VoiceImeVM.RefreshAsync();
        }
    }

    protected virtual void OnSectionShown(string sectionName)
    {
    }

    protected virtual void PrepareSection(string sectionName, Control section)
    {
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

        return e.AddedItems[0] is { } item && _sectionNavigation?.ContainerFromItem(item) is ListBoxItem container
            ? container.Tag as string
            : null;
    }

    private void EnsureNavSelection(string? sectionName)
    {
        if (_sectionNavigation == null || string.IsNullOrWhiteSpace(sectionName))
        {
            return;
        }

        var targetIndex = GetNavIndexBySectionName(sectionName);
        if (targetIndex < 0 || _sectionNavigation.SelectedIndex == targetIndex)
        {
            return;
        }

        _isUpdatingSelection = true;
        try { _sectionNavigation.SelectedIndex = targetIndex; }
        finally { _isUpdatingSelection = false; }
    }

    private int GetNavIndexBySectionName(string sectionName)
    {
        if (_sectionNavigation == null) return -1;
        for (int i = 0; i < _sectionNavigation.ItemCount; i++)
        {
            var li = _sectionNavigation.Items[i] as ListBoxItem
                ?? _sectionNavigation.ContainerFromIndex(i) as ListBoxItem;
            if (li != null
                && string.Equals(li.Tag as string, sectionName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    protected void OpenConfigLocationButton_Click(object? sender, RoutedEventArgs e)
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
        if (_sectionNavigation?.SelectedItem is ListBoxItem listBoxItem)
        {
            return listBoxItem.Tag as string;
        }

        return _sectionNavigation?.SelectedItem is { } item && _sectionNavigation.ContainerFromItem(item) is ListBoxItem container
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
