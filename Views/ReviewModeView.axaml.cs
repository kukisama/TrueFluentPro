using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class ReviewModeView : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private ListBox? _audioFileListBox;

    public ReviewModeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _audioFileListBox ??= this.FindControl<ListBox>("AudioFileListBox");
        };
    }

    // ===== 音频文件列表右键 MenuFlyout =====

    private void AudioFileListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not ListBox listBox) return;

        // 命中测试：选中右键点击的项
        var hitItem = ResolveHitItem<MediaFileItem>(e.Source);
        if (hitItem != null && !ReferenceEquals(listBox.SelectedItem, hitItem))
        {
            listBox.SelectedItem = hitItem;
        }

        var selectedItem = listBox.SelectedItem as MediaFileItem;
        if (selectedItem == null)
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileRightClick", "skip-no-selected-item", isSuccess: false);
            return;
        }

        ViewModel?.BatchProcessing.AuditUiEvent(
            "AudioFileRightClick",
            $"selected={DescribeAudioItem(selectedItem)} pos={DescribePoint(e.GetPosition(listBox))}");
        CrashLogger.AddBreadcrumb($"AudioFileRightClick: {selectedItem.FullPath}");

        var flyout = new MenuFlyout();

        var enqueueItem = new MenuItem { Header = "生成字幕+复盘" };
        enqueueItem.Click += (_, _) =>
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileEnqueue", $"flyout-click item={selectedItem.FullPath}");
            ViewModel?.BatchProcessing.EnqueueSubtitleAndReviewFromLibraryUi(selectedItem);
        };
        flyout.Items.Add(enqueueItem);

        flyout.ShowAt(listBox, true);
        e.Handled = true;
    }

    // ===== 字幕列表右键 MenuFlyout =====

    private void SubtitleCueListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not ListBox listBox) return;

        // 命中测试：选中右键点击的项
        var hitItem = ResolveHitItem<SubtitleCue>(e.Source);
        if (hitItem != null && !ReferenceEquals(listBox.SelectedItem, hitItem))
        {
            listBox.SelectedItem = hitItem;
        }

        var selectedCue = listBox.SelectedItem as SubtitleCue;
        if (selectedCue == null) return;

        CrashLogger.AddBreadcrumb($"SubtitleCueRightClick: {selectedCue.RangeText}");

        var flyout = new MenuFlyout();

        var playItem = new MenuItem { Header = "跳转并播放" };
        playItem.Click += (_, _) =>
        {
            ViewModel?.Playback.PlayFromSubtitleCue(selectedCue);
        };
        flyout.Items.Add(playItem);

        var copyItem = new MenuItem { Header = "复制字幕文本" };
        copyItem.Click += (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            clipboard?.SetTextAsync(selectedCue.Text ?? "");
        };
        flyout.Items.Add(copyItem);

        flyout.ShowAt(listBox, true);
        e.Handled = true;
    }

    // ===== 字幕双击跳转（保留原有功能） =====

    private void SubtitleCueListBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel == null) return;
        if (sender is ListBox listBox && listBox.SelectedItem is SubtitleCue cue)
        {
            ViewModel.Playback.PlayFromSubtitleCue(cue);
        }
    }

    // ===== 辅助方法 =====

    private static T? ResolveHitItem<T>(object? source) where T : class
    {
        if (source is T item) return item;
        if (source is Control control)
        {
            var container = control.FindAncestorOfType<ListBoxItem>();
            return container?.DataContext as T;
        }
        return null;
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
