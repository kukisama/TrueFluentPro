using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;
using System;
using FluentAvalonia.UI.Controls;

namespace TrueFluentPro;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private bool _mediaStudioInitialized;

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
            
            // ViewModel is created and assigned by App.axaml.cs via DI
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
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.NavigateToSettings = () =>
            {
                NavView.SelectedItem = NavView.SettingsItem;
                ShowPage(MainWindowViewModel.NavTagSettings);
            };
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
        this.Show();
        _viewModel?.NotifyMainWindowShown();

        // Select first navigation item on startup
        if (NavView.MenuItems.Count > 0)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
        }
    }

    // 3.5 响应式侧边栏：窗口宽度 < 1200 时自动折叠 NavigationView 到 Compact 模式
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (NavView != null)
        {
            NavView.IsPaneOpen = e.NewSize.Width >= 1200;
        }
    }

    // 3.1 全局快捷键
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
            case Key.OemComma when ctrl:
                NavView.SelectedItem = NavView.SettingsItem;
                ShowPage(MainWindowViewModel.NavTagSettings);
                e.Handled = true;
                break;
        }
    }

    private void SelectNavItemByIndex(int index)
    {
        if (index < NavView.MenuItems.Count)
        {
            NavView.SelectedItem = NavView.MenuItems[index];
        }
        else
        {
            // Out-of-bounds index (e.g. Ctrl+4) navigates to Settings
            NavView.SelectedItem = NavView.SettingsItem;
            ShowPage(MainWindowViewModel.NavTagSettings);
        }
    }

    private void NavView_SelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            ShowPage(MainWindowViewModel.NavTagSettings);
            return;
        }

        if (e.SelectedItem is NavigationViewItem navItem && navItem.Tag is string tag)
        {
            ShowPage(tag);
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

        if (tag == MainWindowViewModel.NavTagMedia && _viewModel != null && !_mediaStudioInitialized)
        {
            _mediaStudioInitialized = true;
            var config = _viewModel.ConfigVM.Config;
            MediaStudioViewPage.Initialize(
                config.AiConfig ?? new TrueFluentPro.Models.AiConfig(),
                config.MediaGenConfig,
                config.Endpoints);
        }
    }

}
