using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class ReviewModeView : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public ReviewModeView()
    {
        InitializeComponent();
    }

    private void SubtitleCueListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        if (sender is ListBox listBox && listBox.SelectedItem is SubtitleCue cue)
        {
            ViewModel.Playback.PlayFromSubtitleCue(cue);
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
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileRightClick", "source-control-missing");
            return;
        }

        var item = control.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not MediaFileItem mediaItem)
        {
            var sourceType = control.GetType().Name;
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileRightClick", $"no-media-item source={sourceType}");
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
        ViewModel?.BatchProcessing.AuditUiEvent(
            "AudioFileRightClick",
            $"selected-before={before} selected-after={after} item={mediaItem.FullPath}");
    }

    private void AudioFileEnqueue_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileEnqueue", "sender-not-menuitem");
            return;
        }

        if (menuItem.DataContext is not MediaFileItem mediaItem)
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileEnqueue", "menuitem-datacontext-missing");
            return;
        }

        ViewModel?.BatchProcessing.AuditUiEvent("AudioFileEnqueue", $"click item={mediaItem.FullPath}");
        ViewModel?.BatchProcessing.EnqueueSubtitleAndReviewFromLibraryUi(mediaItem);
    }
}
