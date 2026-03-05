using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class ReviewModeView : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private ListBox? _audioFileListBox;
    private Border? _dropZone;

    // 支持的音频文件扩展名
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aac" };

    public ReviewModeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _audioFileListBox ??= this.FindControl<ListBox>("AudioFileListBox");
            _dropZone ??= this.FindControl<Border>("DropZone");

            // 3.7 注册拖放事件
            if (_dropZone != null)
            {
                _dropZone.AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);
                _dropZone.AddHandler(DragDrop.DragLeaveEvent, DropZone_DragLeave);
                _dropZone.AddHandler(DragDrop.DropEvent, DropZone_Drop);
            }
        };
    }

    // ===== 3.7 拖放事件处理 =====

    private static List<string> ExtractAudioPaths(DragEventArgs e)
    {
        var result = new List<string>();
#pragma warning disable CS0618 // Data is obsolete but DataTransfer may not have GetFiles in this version
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files == null) return result;

        foreach (var item in files)
        {
            if (item is IStorageFile sf)
            {
                var localPath = sf.Path?.LocalPath;
                if (!string.IsNullOrEmpty(localPath))
                {
                    var ext = Path.GetExtension(localPath);
                    if (SupportedAudioExtensions.Contains(ext))
                    {
                        result.Add(localPath);
                    }
                }
            }
        }
        return result;
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

#pragma warning disable CS0618
        if (e.Data.GetFiles() != null)
#pragma warning restore CS0618
        {
            var audioPaths = ExtractAudioPaths(e);
            if (audioPaths.Count > 0)
            {
                e.DragEffects = DragDropEffects.Copy;
            }
        }

        // 高亮拖放区
        if (_dropZone != null && e.DragEffects != DragDropEffects.None)
        {
            _dropZone.BorderBrush = new SolidColorBrush(Color.Parse("#FF2563EB"));
            _dropZone.Background = new SolidColorBrush(Color.Parse("#10256BEB"));
        }
    }

    private void DropZone_DragLeave(object? sender, DragEventArgs e)
    {
        ResetDropZoneAppearance();
    }

    private void DropZone_Drop(object? sender, DragEventArgs e)
    {
        ResetDropZoneAppearance();

        var audioFiles = ExtractAudioPaths(e);
        if (audioFiles.Count > 0)
        {
            CrashLogger.AddBreadcrumb($"DragDrop: {audioFiles.Count} audio files dropped");

            // 将拖入的文件复制到 Sessions 目录并刷新
            var sessionsDir = PathManager.Instance.SessionsPath;
            foreach (var filePath in audioFiles)
            {
                try
                {
                    Directory.CreateDirectory(sessionsDir);
                    var destPath = Path.Combine(sessionsDir, Path.GetFileName(filePath));
                    if (!File.Exists(destPath))
                    {
                        File.Copy(filePath, destPath);
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.AddBreadcrumb($"DragDrop copy failed: {ex.Message}");
                }
            }

            // 刷新文件库
            ViewModel?.FileLibrary?.RefreshAudioLibraryCommand?.Execute(null);
        }
    }

    private void ResetDropZoneAppearance()
    {
        if (_dropZone != null)
        {
            _dropZone.ClearValue(Border.BorderBrushProperty);
            _dropZone.ClearValue(Border.BackgroundProperty);
        }
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
