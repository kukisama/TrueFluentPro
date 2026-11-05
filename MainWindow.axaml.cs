using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

namespace TrueFluentPro;

public partial class MainWindow : Window
{
    private const double CompactNavWidth = 52;
    private const double ExpandedNavWidth = 176;

    private double _paneExpansionWidth;
    private MainWindowViewModel? _viewModel;
    private bool _mediaStudioInitialized;
    private bool _mediaCenterV2Initialized;
    private bool _audioLabInitialized;
    private bool _taskMonitorInitialized;
    private bool _isApplyingNavPaneState;
    private readonly Dictionary<string, Control> _pageCache = new(StringComparer.Ordinal);
    private Control? _currentPage;
    private Window? _workshopChatWindow;

    private MediaStudioView? MediaStudioViewPage => GetCachedPage<MediaStudioView>(GetPresentationCacheKey(MainWindowViewModel.NavTagMedia));
    private MediaCenterV2View? MediaCenterV2ViewPage => GetCachedPage<MediaCenterV2View>(GetPresentationCacheKey(MainWindowViewModel.NavTagMediaV2));
    private AudioLabView? AudioLabViewPage => GetCachedPage<AudioLabView>(GetPresentationCacheKey(MainWindowViewModel.NavTagAudioLab));
    private TaskQueueMonitorView? TaskMonitorViewPage => GetCachedPage<TaskQueueMonitorView>(GetPresentationCacheKey(MainWindowViewModel.NavTagTaskMonitor));

    public MainWindow() : this(new ShellAppearancePreferencesService())
    {
    }

    public MainWindow(ShellAppearancePreferencesService appearancePreferences)
    {
        _appearancePreferences = appearancePreferences ?? throw new ArgumentNullException(nameof(appearancePreferences));
        InitializeComponent();
        ShellTitleBar.PropertyChanged += OnShellTitleBarPropertyChanged;
        UpdateCaptionState();
        ApplyShellAppearance(_appearancePreferences.Load());

        try
        {
            var icon = AppIconProvider.WindowIcon;
            if (icon != null)
            {
                Icon = icon;
            }
        }
        catch
        {
            // ignore icon failures
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == CanMaximizeProperty
            || change.Property == CanMinimizeProperty)
            UpdateCaptionState();
        if (change.Property == WindowStateProperty || change.Property == IsActiveProperty)
            UpdateNativeCaptionColors();
    }

    private void UpdateCaptionState()
    {
        // Window 基类构造及 XAML 加载期间也可能收到属性通知。
        if (ShellMaximizeButton == null || ShellMinimizeButton == null || ShellMaximizeIcon == null) return;
        var maximized = WindowState == WindowState.Maximized;
        var label = maximized ? "还原" : "最大化";
        ShellMaximizeIcon.Value = maximized ? "fa-regular fa-window-restore" : "fa-regular fa-window-maximize";
        ToolTip.SetTip(ShellMaximizeButton, label);
        AutomationProperties.SetName(ShellMaximizeButton, label);
        ShellMaximizeButton.IsEnabled = CanMaximize;
        ShellMinimizeButton.IsEnabled = CanMinimize;
    }

    private void ShellTitleDragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    private void ToggleMaximize()
    {
        if (CanMaximize)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ShellMinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CanMinimize) WindowState = WindowState.Minimized;
    }

    private void ShellMaximizeButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ShellCloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel != null)
        {
            _viewModel.Settings.ConfigSaved -= OnSettingsConfigSaved;
            _viewModel.ThemeModeChanged -= OnThemeModeChanged;
            _viewModel.MainNavPaneStateChanged -= OnMainNavPaneStateChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.NavigateToSettings = () => ShowPage(MainWindowViewModel.NavTagSettings);
            _viewModel.NavigateToWorkshopWithAttachments = NavigateToWorkshopWithAttachments;

            _viewModel.Settings.ConfigSaved += OnSettingsConfigSaved;
            _viewModel.ThemeModeChanged += OnThemeModeChanged;
            _viewModel.MainNavPaneStateChanged += OnMainNavPaneStateChanged;
            ApplyThemeMode(_viewModel.CurrentThemeMode);
            ApplyMainNavPaneState(_viewModel.IsMainNavPaneOpen);
            UpdateShellNavSelection();

            foreach (var page in _pageCache.Values)
            {
                UpdatePageDataContext(page);
            }
            UpdateThemePresentation();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosingBusinessPages = true;
        ShellTitleBar.PropertyChanged -= OnShellTitleBarPropertyChanged;
        if (_viewModel != null)
        {
            _viewModel.Settings.ConfigSaved -= OnSettingsConfigSaved;
            _viewModel.ThemeModeChanged -= OnThemeModeChanged;
            _viewModel.MainNavPaneStateChanged -= OnMainNavPaneStateChanged;
            _viewModel.NavigateToSettings = null;
            _viewModel.NavigateToWorkshopWithAttachments = null;
        }
        _workshopChatWindow?.Close();
        GetCachedPage<InkStudyTranslationView>(InkStudyCacheKey)?.DisposePresentation();
        GetCachedPage<SongScrollTranslationView>(SongScrollCacheKey)?.DisposePresentation();
        DisposeBusinessPages();
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateNativeCaptionColors();
        NativePrelaunchSplash.CloseIfOpen();
        ApplyMainNavPaneState(_viewModel?.IsMainNavPaneOpen ?? false);

        var startupAudioPath = TryGetStartupAudioFilePath();

        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosingBusinessPages) return;
            var initialTag = !string.IsNullOrWhiteSpace(startupAudioPath)
                ? MainWindowViewModel.NavTagAudioLab
                : (_viewModel?.SelectedNavTag ?? MainWindowViewModel.NavTagLive);

            ShowPage(initialTag);
            _viewModel?.NotifyMainWindowShown();

            // 首次启动：确保主窗口置于最前
            Activate();
            Topmost = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (_isClosingBusinessPages) return;
                Topmost = false;
                if (!string.IsNullOrWhiteSpace(startupAudioPath)
                    && AudioLabViewPage is { } audioLab)
                {
                    EnsureAudioLabInitialized(audioLab);
                    await audioLab.LoadAudioFileWithFormatCheckAsync(startupAudioPath);
                }
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private static string? TryGetStartupAudioFilePath()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.Args == null
            || desktop.Args.Length == 0)
        {
            return null;
        }

        foreach (var rawArg in desktop.Args)
        {
            if (string.IsNullOrWhiteSpace(rawArg)) continue;
            var candidate = rawArg.Trim().Trim('\u202a', '\u202b', '\u202c', '\u200e', '\u200f', '"');
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (IsSongStudyAppearance || IsInkStudyAppearance || IsSongScrollAppearance || _isApplyingNavPaneState || _viewModel?.IsMainNavPaneOpen != true)
        {
            return;
        }

        var minimumExpandedWindowWidth = MinWidth + (ExpandedNavWidth - CompactNavWidth);
        if (e.NewSize.Width < minimumExpandedWindowWidth)
        {
            _viewModel.UpdateMainNavPaneState(false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.F5:
                _viewModel?.StartTranslationCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F6:
                _viewModel?.StopTranslationCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D1 when ctrl:
                SelectNavItemByIndex(0);
                e.Handled = true;
                break;
            // Ctrl+2 / Ctrl+3 原指向复盘洞察 / 批量字幕，入口已隐藏，快捷键一并停用
            case Key.D4 when ctrl:
                SelectNavItemByIndex(3);
                e.Handled = true;
                break;
            case Key.D5 when ctrl:
                ShowPage(MainWindowViewModel.NavTagSettings);
                e.Handled = true;
                break;
            case Key.D6 when ctrl:
                ShowPage(MainWindowViewModel.NavTagMediaV2);
                e.Handled = true;
                break;
            case Key.D7 when ctrl:
                ShowPage(MainWindowViewModel.NavTagAudioLab);
                e.Handled = true;
                break;
            case Key.D8 when ctrl:
                ShowPage(MainWindowViewModel.NavTagTaskMonitor);
                e.Handled = true;
                break;
            case Key.OemComma when ctrl:
                ShowPage(MainWindowViewModel.NavTagSettings);
                e.Handled = true;
                break;
        }
    }

    private void SelectNavItemByIndex(int index)
    {
        switch (index)
        {
            case 0:
                ShowPage(MainWindowViewModel.NavTagLive);
                break;
            case 1:
                ShowPage(MainWindowViewModel.NavTagReview);
                break;
            case 2:
                ShowPage(MainWindowViewModel.NavTagBatch);
                break;
            case 3:
                ShowPage(MainWindowViewModel.NavTagMedia);
                break;
            default:
                ShowPage(MainWindowViewModel.NavTagSettings);
                break;
        }
    }

    private void ShowPage(string tag)
    {
        if (_isClosingBusinessPages) return;
        tag = NormalizeNavTag(tag);
        if (tag == MainWindowViewModel.NavTagMedia && _workshopChatWindow != null)
        {
            ActivateWorkshopChatWindow();
            return;
        }
        _currentNavTag = tag;
        var page = GetOrCreatePage(tag);

        if (!ReferenceEquals(_currentPage, page))
        {
            // 隐藏旧页面（保留在视觉树中，避免下次切回时重新 Measure/Arrange）
            if (_currentPage != null)
                _currentPage.IsVisible = false;

            // 长卷的独立业务页共用全页宿主；各外观的缓存树仍留在自己的宿主。
            var host = page is SongScrollTranslationView or ISongScrollPage
                ? SongScrollHost : page is InkStudyTranslationView ? InkStudyHost : PageHost;
            if (!host.Children.Contains(page))
            {
                PagePlaceholder.IsVisible = false;
                host.Children.Add(page);
            }

            page.IsVisible = true;
            _currentPage = page;
        }

        if (_viewModel != null)
        {
            _viewModel.SelectedNavTag = tag;
        }

        UpdateShellNavSelection();
        UpdateInkStudyHost();

        // 延迟到首帧渲染后再执行重初始化，保证页面壳层立即可见
        if (tag == MainWindowViewModel.NavTagBatch)
        {
            Dispatcher.UIThread.Post(() =>
                _viewModel?.BatchProcessing.EnsureBatchCenterInitialized(),
                DispatcherPriority.Background);
        }

        ScheduleBusinessPageInitialization(page);
    }

    private static string NormalizeNavTag(string? tag)
    {
        return tag switch
        {
            MainWindowViewModel.NavTagReview => MainWindowViewModel.NavTagReview,
            MainWindowViewModel.NavTagBatch => MainWindowViewModel.NavTagBatch,
            MainWindowViewModel.NavTagMedia => MainWindowViewModel.NavTagMedia,
            MainWindowViewModel.NavTagMediaV2 => MainWindowViewModel.NavTagMediaV2,
            MainWindowViewModel.NavTagAudioLab => MainWindowViewModel.NavTagAudioLab,
            MainWindowViewModel.NavTagTaskMonitor => MainWindowViewModel.NavTagTaskMonitor,
            MainWindowViewModel.NavTagSettings => MainWindowViewModel.NavTagSettings,
            _ => MainWindowViewModel.NavTagLive
        };
    }

    private T? GetCachedPage<T>(string tag) where T : Control
    {
        return _pageCache.TryGetValue(tag, out var page) ? page as T : null;
    }

    private Control GetOrCreatePage(string tag)
    {
        tag = NormalizeNavTag(tag);
        if (TryGetOrCreateSongScrollPage(tag, out var scrollPage))
            return scrollPage;
        if (tag == MainWindowViewModel.NavTagLive && IsSongScrollAppearance)
        {
            if (!_pageCache.TryGetValue(SongScrollCacheKey, out var scroll))
            {
                var view = new SongScrollTranslationView();
                view.NavigationRequested += ShowPage;
                view.ThemeRequested += ShowAppearanceMenu;
                scroll = view;
                _pageCache[SongScrollCacheKey] = scroll;
            }
            UpdatePageDataContext(scroll);
            return scroll;
        }
        if (tag == MainWindowViewModel.NavTagLive && IsInkStudyAppearance)
        {
            if (!_pageCache.TryGetValue(InkStudyCacheKey, out var ink))
            {
                var view = new InkStudyTranslationView();
                view.NavigationRequested += ShowPage;
                view.ThemeRequested += ShowAppearanceMenu;
                ink = view;
                _pageCache[InkStudyCacheKey] = ink;
            }
            UpdatePageDataContext(ink);
            return ink;
        }

        if (_pageCache.TryGetValue(tag, out var existingPage))
        {
            UpdatePageDataContext(existingPage);
            return existingPage;
        }

        var page = CreatePage(tag);
        if (page is LiveTranslationView live)
            live.ApplyAppearance(IsSongStudyAppearance);
        _pageCache[tag] = page;
        return page;
    }

    private Control CreatePage(string tag)
    {
        return tag switch
        {
            MainWindowViewModel.NavTagReview => CreateBoundPage(new ReviewModeView()),
            MainWindowViewModel.NavTagBatch => CreateBoundPage(new BatchCenterView()),
            MainWindowViewModel.NavTagMedia => new MediaStudioView(),
            MainWindowViewModel.NavTagMediaV2 => new MediaCenterV2View(),
            MainWindowViewModel.NavTagAudioLab => new Views.AudioLabView(),
            MainWindowViewModel.NavTagTaskMonitor => new Views.TaskQueueMonitorView(),
            MainWindowViewModel.NavTagSettings => CreateBoundPage(new SettingsView()),
            _ => CreateBoundPage(new LiveTranslationView())
        };
    }

    private T CreateBoundPage<T>(T page) where T : Control
    {
        UpdatePageDataContext(page);
        return page;
    }

    private void UpdatePageDataContext(Control page)
    {
        if (page is SongScrollTranslationView scroll)
        {
            scroll.BindSession(_viewModel);
            return;
        }
        if (page is InkStudyTranslationView ink)
        {
            ink.BindSession(_viewModel);
            return;
        }
        if (page is MediaStudioView || page is MediaCenterV2View || page is AudioLabView || page is TaskQueueMonitorView)
        {
            BindBusinessPage(page);
            return;
        }

        if (!ReferenceEquals(page.DataContext, _viewModel))
        {
            page.DataContext = _viewModel;
        }
    }

    private void OnSettingsConfigSaved(AzureSpeechConfig config)
    {
        if (_isClosingBusinessPages) return;
        // 每份业务只更新一次；不能让两个呈现重复刷新历史、鉴权或播放器。
        _audioLabBusiness?.RefreshStagePresetVisibility();
        var aiConfig = config.AiConfig ?? new AiConfig();
        // 原 UpdateConfiguration 会清空自己的终结点列表，传入快照避免别名清空源配置。
        var endpoints = config.Endpoints.ToList();
        if (_mediaStudioBusiness != null)
        {
            _mediaStudioBusiness.UpdateConfiguration(
                aiConfig,
                config.MediaGenConfig,
                endpoints,
                config.WebSearchProviderId,
                config.WebSearchTriggerMode,
                config.WebSearchMaxResults,
                config.WebSearchEnableIntentAnalysis,
                config.WebSearchEnableResultCompression,
                config.WebSearchMcpEndpoint,
                config.WebSearchMcpToolName,
                config.WebSearchMcpApiKey,
                config.WebSearchDebugMode);
        }

        if (_mediaCenterBusiness != null)
        {
            _mediaCenterBusiness.UpdateConfiguration(
                aiConfig,
                config.MediaGenConfig,
                endpoints);
        }
    }

    private void OnThemeModeChanged(ThemeModePreference mode)
    {
        ApplyThemeMode(mode);
        UpdateThemePresentation();
    }

    private void OnMainNavPaneStateChanged(bool isOpen)
    {
        ApplyMainNavPaneState(isOpen);
    }

    private void ApplyMainNavPaneState(bool isOpen)
    {
        _isApplyingNavPaneState = true;
        try
        {
            if (!IsSongStudyAppearance && !IsInkStudyAppearance && !IsSongScrollAppearance)
                ApplyWindowWidthForPaneState(isOpen);
            if (ShellNavRail != null)
            {
                ShellNavRail.Width = IsSongStudyAppearance ? 192 : isOpen ? ExpandedNavWidth : CompactNavWidth;
            }
            UpdateNavigationLabels(isOpen || IsSongStudyAppearance);
        }
        finally
        {
            _isApplyingNavPaneState = false;
        }
    }

    private void ApplyWindowWidthForPaneState(bool isOpen)
    {
        var targetExpansionWidth = isOpen ? ExpandedNavWidth - CompactNavWidth : 0;
        var delta = targetExpansionWidth - _paneExpansionWidth;
        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            _paneExpansionWidth = targetExpansionWidth;
            return;
        }

        var oldWidth = Width;
        var newWidth = Math.Max(MinWidth, oldWidth + delta);
        Width = newWidth;

        _paneExpansionWidth = targetExpansionWidth;
    }

    private void UpdateShellNavSelection()
    {
        LiveNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagLive);
        ReviewNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagReview);
        BatchNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagBatch);
        MediaNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagMedia);
        MediaV2NavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagMediaV2);
        AudioLabNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagAudioLab);
        TaskMonitorNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagTaskMonitor);
        SettingsNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagSettings);
    }

    private void ToggleShellNavButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.UpdateMainNavPaneState(!_viewModel.IsMainNavPaneOpen);
    }

    private void LiveNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagLive);
    }

    private void ReviewNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagReview);
    }

    private void BatchNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagBatch);
    }

    private void MediaNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagMedia);
    }

    private void EnsureMediaStudioInitialized()
    {
        if (_isClosingBusinessPages || _viewModel == null) return;
        if (_workshopChatWindow?.Content is MediaStudioView floatingStudio)
        {
            BindBusinessPage(floatingStudio);
            return;
        }
        var studio = (MediaStudioView)GetOrCreatePage(MainWindowViewModel.NavTagMedia);
        EnsureMediaStudioInitialized(studio);
    }

    private async Task OpenTranscriptChatAsync(string sourcePath, string transcript, string selectedQuote)
    {
        if (_isClosingBusinessPages) return;
        EnsureMediaStudioInitialized();
        var studio = _workshopChatWindow?.Content as MediaStudioView ?? MediaStudioViewPage
            ?? throw new InvalidOperationException("创作工坊尚未就绪。");
        var vm = studio.ViewModel
            ?? throw new InvalidOperationException("聊天服务尚未就绪。");
        await vm.InitializationTask;
        if (_isClosingBusinessPages) return;
        vm.CreateNewSession();
        var session = vm.CurrentSession!;
        await session.InitializeTranscriptConversationAsync(sourcePath, transcript, selectedQuote);
        if (_isClosingBusinessPages) return;

        if (_workshopChatWindow == null)
        {
            // 移动已有视图，不创建第二份会话管理器或历史缓存。
            RemovePageFromHost(studio);
            var wasMainPage = ReferenceEquals(_currentPage, studio);
            if (wasMainPage)
                _currentPage = null;
            studio.IsVisible = true;
            var window = new Window
            {
                Title = "字幕问答 · 创作工坊",
                Width = 1120,
                Height = 800,
                MinWidth = 960,
                MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = Icon,
                Content = studio
            };
            _workshopChatWindow = window;
            window.Closed += (_, _) =>
            {
                window.Content = null;
                studio.IsVisible = false;
                _workshopChatWindow = null;
                // 保留原缓存与共享业务；若主窗此时没有页面，重新接回当前外观。
                if (!_isClosingBusinessPages && _currentPage == null)
                    ShowPage(MainWindowViewModel.NavTagMedia);
            };
            window.Show(this);
            if (wasMainPage)
                ShowPage(MainWindowViewModel.NavTagAudioLab);
        }
        ActivateWorkshopChatWindow();
    }

    private void ActivateWorkshopChatWindow()
    {
        if (_workshopChatWindow == null) return;
        if (_workshopChatWindow.WindowState == WindowState.Minimized)
            _workshopChatWindow.WindowState = WindowState.Normal;
        _workshopChatWindow.Activate();
    }

    private async void NavigateToWorkshopWithAttachments(string[] filePaths)
    {
        try
        {
            foreach (var path in filePaths)
            {
                if (_isClosingBusinessPages) return;
                if (!File.Exists(path))
                    throw new FileNotFoundException("字幕文件不存在，请重新加载字幕。", path);
                await OpenTranscriptChatAsync(path, await File.ReadAllTextAsync(path), "");
            }
        }
        catch (Exception ex)
        {
            if (_mediaStudioBusiness is { } vm)
                vm.StatusText = $"打开字幕问答失败：{ex.Message}";
            CrashLogger.AddBreadcrumb($"打开字幕问答失败：{ex.Message}");
        }
    }

    private void MediaV2NavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagMediaV2);
    }

    private void AudioLabNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagAudioLab);
    }

    private void TaskMonitorNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagTaskMonitor);
    }

    private void SettingsNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagSettings);
    }

    private static void ApplyThemeMode(ThemeModePreference mode)
    {
        if (Application.Current == null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeModePreference.Light => ThemeVariant.Light,
            ThemeModePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
