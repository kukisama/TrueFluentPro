using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using System;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro;

public partial class MainWindow : Window
{
    private const double CompactNavWidth = 52;
    private const double ExpandedNavWidth = 164;

    private double _paneExpansionWidth;
    private MainWindowViewModel? _viewModel;
    private bool _mediaStudioInitialized;
    private bool _isApplyingNavPaneState;

    public MainWindow()
    {
        Console.WriteLine("MainWindow constructor called");
        try
        {
            InitializeComponent();
            Console.WriteLine("InitializeComponent completed");

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

            Console.WriteLine("MainWindow constructor completed (ViewModel set by DI)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in MainWindow constructor: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
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

            _viewModel.Settings.ConfigSaved += OnSettingsConfigSaved;
            _viewModel.ThemeModeChanged += OnThemeModeChanged;
            _viewModel.MainNavPaneStateChanged += OnMainNavPaneStateChanged;
            ApplyThemeMode(_viewModel.CurrentThemeMode);
            ApplyMainNavPaneState(_viewModel.IsMainNavPaneOpen);
            UpdateShellNavSelection();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        MediaStudioViewPage?.Cleanup();
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Show();
        _viewModel?.NotifyMainWindowShown();
        ApplyMainNavPaneState(_viewModel?.IsMainNavPaneOpen ?? false);
        ShowPage(_viewModel?.SelectedNavTag ?? MainWindowViewModel.NavTagLive);
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
                ShowPage(MainWindowViewModel.NavTagSettings);
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
                ShowPage(MainWindowViewModel.NavTagMedia);
                break;
            default:
                ShowPage(MainWindowViewModel.NavTagSettings);
                break;
        }
    }

    private void ShowPage(string tag)
    {
        var newVisible = tag switch
        {
            MainWindowViewModel.NavTagLive => (Control)LiveView,
            MainWindowViewModel.NavTagReview => (Control)ReviewView,
            MainWindowViewModel.NavTagMedia => (Control)MediaStudioViewPage,
            MainWindowViewModel.NavTagSettings => (Control)SettingsViewPage,
            _ => (Control)LiveView
        };

        var pages = new Control[] { LiveView, ReviewView, MediaStudioViewPage, SettingsViewPage };

        foreach (var page in pages)
        {
            page.IsVisible = page == newVisible;
        }

        if (_viewModel != null)
        {
            _viewModel.SelectedNavTag = tag;
        }

        UpdateShellNavSelection();

        if (tag == MainWindowViewModel.NavTagMedia && _viewModel != null && !_mediaStudioInitialized)
        {
            _mediaStudioInitialized = true;
            var config = _viewModel.ConfigVM.Config;
            MediaStudioViewPage.Initialize(
                config.AiConfig ?? new AiConfig(),
                config.MediaGenConfig,
                config.Endpoints);
        }
    }

    private void OnSettingsConfigSaved(AzureSpeechConfig config)
    {
        if (!_mediaStudioInitialized)
        {
            return;
        }

        MediaStudioViewPage.UpdateConfiguration(
            config.AiConfig ?? new AiConfig(),
            config.MediaGenConfig,
            config.Endpoints);
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
            ShellNavRail.Width = isOpen ? ExpandedNavWidth : CompactNavWidth;
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
        MediaNavButton.Classes.Set("selected", _viewModel?.SelectedNavTag == MainWindowViewModel.NavTagMedia);
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

    private void MediaNavButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(MainWindowViewModel.NavTagMedia);
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
