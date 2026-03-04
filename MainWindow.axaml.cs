using Avalonia.Controls;
using TrueFluentPro.ViewModels;
using System;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;

namespace TrueFluentPro;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        Console.WriteLine("MainWindow constructor called");
        try
        {
            InitializeComponent();
            Console.WriteLine("InitializeComponent completed");

            try
            {
                var icon = Services.AppIconProvider.WindowIcon;
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
    }

    protected override void OnClosed(EventArgs e)
    {
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
            ShowPage("settings");
            return;
        }

        if (e.SelectedItem is NavigationViewItem navItem && navItem.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        LiveView.IsVisible = tag == "live";
        ReviewView.IsVisible = tag == "review";
        SettingsViewPage.IsVisible = tag == "settings";

        if (_viewModel != null)
        {
            _viewModel.SelectedNavTag = tag;
        }

        // Open Media Studio in a separate window when selected
        if (tag == "media" && _viewModel != null)
        {
            _viewModel.ShowMediaStudioCommand.Execute(null);
            // Navigate back to previous view since Media Studio opens as window
            if (NavView.MenuItems.Count > 0)
            {
                NavView.SelectedItem = NavView.MenuItems[0];
            }
        }
    }

    private void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            // ContextMenu is hosted in a separate popup tree and does not reliably inherit
            // DataContext. Assign it explicitly so MenuItem Command bindings work.
            button.ContextMenu.DataContext = DataContext;
            button.ContextMenu.Open(button);
        }
    }
}
