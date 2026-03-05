using Avalonia.Controls;
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
        LiveView.IsVisible = tag == MainWindowViewModel.NavTagLive;
        ReviewView.IsVisible = tag == MainWindowViewModel.NavTagReview;
        MediaStudioViewPage.IsVisible = tag == MainWindowViewModel.NavTagMedia;
        SettingsViewPage.IsVisible = tag == MainWindowViewModel.NavTagSettings;

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
                config.MediaGenConfig);
        }
    }

}
