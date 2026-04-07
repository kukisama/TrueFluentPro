using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
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
    private bool _isApplyingNavPaneState;
    private readonly Dictionary<string, Control> _pageCache = new(StringComparer.Ordinal);
    private Control? _currentPage;

    private MediaStudioView? MediaStudioViewPage => GetCachedPage<MediaStudioView>(MainWindowViewModel.NavTagMedia);
    private MediaCenterV2View? MediaCenterV2ViewPage => GetCachedPage<MediaCenterV2View>(MainWindowViewModel.NavTagMediaV2);

    public MainWindow()
    {
        InitializeComponent();

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
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        MediaStudioViewPage?.Cleanup();
        MediaCenterV2ViewPage?.Cleanup();
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NativePrelaunchSplash.CloseIfOpen();
        ApplyMainNavPaneState(_viewModel?.IsMainNavPaneOpen ?? false);

        Dispatcher.UIThread.Post(() =>
        {
            ShowPage(_viewModel?.SelectedNavTag ?? MainWindowViewModel.NavTagLive);
            _viewModel?.NotifyMainWindowShown();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (_isApplyingNavPaneState || _viewModel?.IsMainNavPaneOpen != true)
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
            case Key.D2 when ctrl:
                SelectNavItemByIndex(1);
                e.Handled = true;
                break;
            case Key.D3 when ctrl:
                SelectNavItemByIndex(2);
                e.Handled = true;
                break;
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
        tag = NormalizeNavTag(tag);
        var page = GetOrCreatePage(tag);

        if (!ReferenceEquals(_currentPage, page))
        {
            // 隐藏旧页面（保留在视觉树中，避免下次切回时重新 Measure/Arrange）
            if (_currentPage != null)
                _currentPage.IsVisible = false;

            // 首次展示的页面加入 PageHost 子级
            if (!PageHost.Children.Contains(page))
            {
                PagePlaceholder.IsVisible = false;
                PageHost.Children.Add(page);
            }

            page.IsVisible = true;
            _currentPage = page;
        }

        if (_viewModel != null)
        {
            _viewModel.SelectedNavTag = tag;
        }

        UpdateShellNavSelection();

        // 延迟到首帧渲染后再执行重初始化，保证页面壳层立即可见
        if (tag == MainWindowViewModel.NavTagBatch)
        {
            Dispatcher.UIThread.Post(() =>
                _viewModel?.BatchProcessing.EnsureBatchCenterInitialized(),
                DispatcherPriority.Background);
        }

        if (tag == MainWindowViewModel.NavTagMedia && _viewModel != null && !_mediaStudioInitialized)
        {
            _mediaStudioInitialized = true;
            Dispatcher.UIThread.Post(() =>
            {
                var config = _viewModel.ConfigVM.Config;
                MediaStudioViewPage?.Initialize(
                    config.AiConfig ?? new AiConfig(),
                    config.MediaGenConfig,
                    config.Endpoints,
                    App.Services.GetRequiredService<IModelRuntimeResolver>(),
                    App.Services.GetRequiredService<IAzureTokenProviderStore>(),
                    App.Services.GetRequiredService<ConfigurationService>(),
                    () => _viewModel.ConfigVM.Config,
                    updatedConfig =>
                    {
                        _viewModel.ConfigVM.SetConfig(updatedConfig);
                        _viewModel.Settings.Initialize(updatedConfig);
                    });
            }, DispatcherPriority.Background);
        }

        if (tag == MainWindowViewModel.NavTagMediaV2 && !_mediaCenterV2Initialized)
        {
            _mediaCenterV2Initialized = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel != null)
                {
                    var config = _viewModel.ConfigVM.Config;
                    MediaCenterV2ViewPage?.Initialize(
                        config.AiConfig ?? new AiConfig(),
                        config.MediaGenConfig,
                        config.Endpoints,
                        App.Services.GetRequiredService<IModelRuntimeResolver>(),
                        App.Services.GetRequiredService<IAzureTokenProviderStore>());
                }
            }, DispatcherPriority.Background);
        }
    }

    private static string NormalizeNavTag(string? tag)
    {
        return tag switch
        {
            MainWindowViewModel.NavTagReview => MainWindowViewModel.NavTagReview,
            MainWindowViewModel.NavTagBatch => MainWindowViewModel.NavTagBatch,
            MainWindowViewModel.NavTagMedia => MainWindowViewModel.NavTagMedia,
            MainWindowViewModel.NavTagMediaV2 => MainWindowViewModel.NavTagMediaV2,
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

        if (_pageCache.TryGetValue(tag, out var existingPage))
        {
            UpdatePageDataContext(existingPage);
            return existingPage;
        }

        var page = CreatePage(tag);
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
        if (page is MediaStudioView || page is MediaCenterV2View)
        {
            return;
        }

        if (!ReferenceEquals(page.DataContext, _viewModel))
        {
            page.DataContext = _viewModel;
        }
    }

    private void OnSettingsConfigSaved(AzureSpeechConfig config)
    {
        if (!_mediaStudioInitialized && !_mediaCenterV2Initialized)
        {
            return;
        }

        var aiConfig = config.AiConfig ?? new AiConfig();

        if (_mediaStudioInitialized)
        {
            var mediaStudioView = MediaStudioViewPage;
            mediaStudioView?.UpdateConfiguration(
                aiConfig,
                config.MediaGenConfig,
                config.Endpoints,
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

        if (_mediaCenterV2Initialized)
        {
            var mediaCenterV2View = MediaCenterV2ViewPage;
            mediaCenterV2View?.UpdateConfiguration(
                aiConfig,
                config.MediaGenConfig,
                config.Endpoints);
        }
    }

    private void OnThemeModeChanged(ThemeModePreference mode)
    {
        ApplyThemeMode(mode);
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
            ApplyWindowWidthForPaneState(isOpen);
            if (ShellNavRail != null)
            {
                ShellNavRail.Width = isOpen ? ExpandedNavWidth : CompactNavWidth;
            }
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

    private void NavigateToWorkshopWithAttachments(string[] filePaths)
    {
        ShowPage(MainWindowViewModel.NavTagMedia);

        // 确保创作工坊已初始化后再操作
        Dispatcher.UIThread.Post(() =>
        {
            var studio = MediaStudioViewPage;
            if (studio?.ViewModel == null) return;

            studio.ViewModel.CreateNewSession();
            var session = studio.ViewModel.CurrentSession;
            if (session == null) return;

            foreach (var path in filePaths)
            {
                if (System.IO.File.Exists(path))
                    session.AddAttachmentFile(path);
            }
        }, DispatcherPriority.Background);
    }

    private void MediaV2NavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagMediaV2);
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
