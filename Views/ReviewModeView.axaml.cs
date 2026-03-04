using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class ReviewModeView : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private ListBox? _audioFileListBox;
    private Border? _audioInlineMenuPanel;
    private InlineListContextMenuController<MediaFileItem>? _audioInlineMenuController;
    private bool _audioPointerHooked;
    private bool _audioSelectionHooked;
    private bool _globalInputHooked;

    public ReviewModeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            EnsureAudioInlineMenuAttached();
        };
    }

    private void EnsureAudioInlineMenuAttached()
    {
        _audioFileListBox ??= this.FindControl<ListBox>("AudioFileListBox");
        _audioInlineMenuPanel ??= this.FindControl<Border>("AudioInlineMenuPanel");

        if (_audioInlineMenuController == null && _audioFileListBox != null && _audioInlineMenuPanel != null)
        {
            _audioInlineMenuController = new InlineListContextMenuController<MediaFileItem>(
                _audioFileListBox,
                _audioInlineMenuPanel,
                () => _audioFileListBox.SelectedItem as MediaFileItem,
                InlineContextSelectionMode.SelectHitItemThenOpen);

            _audioInlineMenuController.MenuShown += (item, shownAt) =>
            {
                ViewModel?.BatchProcessing.AuditUiEvent(
                    "AudioFileInlineMenu",
                    $"show target={DescribeAudioItem(item)} pos=({shownAt.X:F1},{shownAt.Y:F1}) list-bounds={_audioFileListBox.Bounds}");
                ViewModel?.BatchProcessing.AuditUiEvent(
                    "AudioFileRightClick",
                    $"open-by=inline-panel selected={DescribeAudioItem(item)}");
                CrashLogger.AddBreadcrumb($"AudioFileInlineMenu shown: {item.FullPath}");
            };

            _audioInlineMenuController.MenuHidden += reason =>
            {
                ViewModel?.BatchProcessing.AuditUiEvent("AudioFileInlineMenu", $"hide reason={reason}");
            };
        }

        if (_audioFileListBox != null)
        {
            if (!_audioPointerHooked)
            {
                _audioFileListBox.AddHandler(InputElement.PointerPressedEvent, AudioFileList_PointerPressed, RoutingStrategies.Tunnel);
                _audioFileListBox.AddHandler(InputElement.PointerReleasedEvent, AudioFileList_PointerReleased, RoutingStrategies.Tunnel);
                _audioPointerHooked = true;
            }

            if (!_audioSelectionHooked)
            {
                _audioFileListBox.SelectionChanged += AudioFileList_SelectionChanged;
                _audioSelectionHooked = true;
            }
        }

        if (!_globalInputHooked)
        {
            AddHandler(InputElement.PointerPressedEvent, AnyInput_PointerPressed, RoutingStrategies.Tunnel);
            AddHandler(InputElement.KeyDownEvent, AnyInput_KeyDown, RoutingStrategies.Tunnel);
            _globalInputHooked = true;
        }
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
        if (sender is not ListBox listBox || _audioInlineMenuController == null)
        {
            return;
        }

        var wasRight = e.GetCurrentPoint(listBox).Properties.IsRightButtonPressed;
        _audioInlineMenuController.HandleListPointerPressed(e);

        if (!wasRight)
        {
            return;
        }

        if (listBox.SelectedItem is not MediaFileItem selectedItem)
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileRightClick", "skip-no-selected-item", isSuccess: false);
            return;
        }

        ViewModel?.BatchProcessing.AuditUiEvent(
            "AudioFileRightClick",
            $"pointer-pressed selected={DescribeAudioItem(selectedItem)} pos={DescribePoint(e.GetPosition(listBox))} pending=true");
    }

    private void AudioFileList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ListBox listBox || _audioInlineMenuController == null)
        {
            return;
        }

        var opened = _audioInlineMenuController.HandleListPointerReleased(e, out var openedItem);
        if (e.InitialPressMouseButton == MouseButton.Right && !opened)
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileRightClick", "release-skip-no-selected-item", isSuccess: false);
            return;
        }

        if (opened && openedItem != null)
        {
            ViewModel?.BatchProcessing.AuditUiEvent(
                "AudioFileRightClick",
                $"pointer-released selected={DescribeAudioItem(openedItem)} pos={DescribePoint(e.GetPosition(listBox))} opened=true");
        }
    }

    private void AudioFileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _audioInlineMenuController?.HandleSelectionChanged();
    }

    private void AudioInlineEnqueue_Click(object? sender, RoutedEventArgs e)
    {
        if (_audioInlineMenuController == null || !_audioInlineMenuController.TryGetCurrentItem(out var currentItem) || currentItem == null)
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileEnqueue", "inline-menu-item-missing");
            _audioInlineMenuController?.HideMenu("inline-item-missing");
            return;
        }

        ViewModel?.BatchProcessing.AuditUiEvent("AudioFileEnqueue", $"inline-click item={currentItem.FullPath}");
        ViewModel?.BatchProcessing.EnqueueSubtitleAndReviewFromLibraryUi(currentItem);
        _audioInlineMenuController.HideMenu("inline-menu-click");
    }

    private void AnyInput_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _audioInlineMenuController?.HandleAnyPointerPressed(e);
    }

    private void AnyInput_KeyDown(object? sender, KeyEventArgs e)
    {
        _audioInlineMenuController?.HandleAnyKeyDown(e);
    }

    private static string DescribeAudioItem(MediaFileItem? item)
    {
        return item == null
            ? "(null)"
            : $"name='{item.Name}', path='{item.FullPath}'";
    }

    private static string DescribePoint(Point? point)
    {
        return point is Point p
            ? $"({p.X:F1},{p.Y:F1})"
            : "(null)";
    }
}
