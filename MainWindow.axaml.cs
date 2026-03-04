using Avalonia.Controls;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;
using System;
using Avalonia.Interactivity;
using TrueFluentPro.Models;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace TrueFluentPro;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;    public MainWindow()
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
        }        catch (Exception ex)
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

    private void SubtitleCueListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (sender is ListBox listBox && listBox.SelectedItem is SubtitleCue cue)
        {
            _viewModel.PlayFromSubtitleCue(cue);
        }
    }

    private void AudioFileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (!e.GetCurrentPoint(listBox).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is not Control control)
        {
            _viewModel?.AuditUiEvent("AudioFileRightClick", "source-control-missing");
            return;
        }

        var item = control.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not MediaFileItem mediaItem)
        {
            var sourceType = control.GetType().Name;
            _viewModel?.AuditUiEvent("AudioFileRightClick", $"no-media-item source={sourceType}");
            return;
        }

        var before = listBox.SelectedItem is MediaFileItem beforeItem
            ? beforeItem.FullPath
            : "";

        listBox.SelectedItem = mediaItem;
        listBox.Focus();

        var after = listBox.SelectedItem is MediaFileItem afterItem
            ? afterItem.FullPath
            : "";
        _viewModel?.AuditUiEvent(
            "AudioFileRightClick",
            $"selected-before={before} selected-after={after} item={mediaItem.FullPath}");
    }

    private void AudioFileEnqueue_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            _viewModel?.AuditUiEvent("AudioFileEnqueue", "sender-not-menuitem");
            return;
        }

        if (menuItem.DataContext is not MediaFileItem mediaItem)
        {
            _viewModel?.AuditUiEvent("AudioFileEnqueue", "menuitem-datacontext-missing");
            return;
        }

        _viewModel?.AuditUiEvent("AudioFileEnqueue", $"click item={mediaItem.FullPath}");
        _viewModel?.EnqueueSubtitleAndReviewFromLibraryUi(mediaItem);
    }
}

