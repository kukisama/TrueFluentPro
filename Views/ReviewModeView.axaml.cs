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
    private Button? _loadAudioButton;

    // 支持的音频文件扩展名
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aac" };

    public ReviewModeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _audioFileListBox ??= this.FindControl<ListBox>("AudioFileListBox");
            _loadAudioButton ??= this.FindControl<Button>("LoadAudioButton");

            if (_loadAudioButton != null)
            {
                _loadAudioButton.AddHandler(DragDrop.DragOverEvent, LoadAudioButton_DragOver);
                _loadAudioButton.AddHandler(DragDrop.DragLeaveEvent, LoadAudioButton_DragLeave);
                _loadAudioButton.AddHandler(DragDrop.DropEvent, LoadAudioButton_Drop);
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

    private void LoadAudioButton_DragOver(object? sender, DragEventArgs e)
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

        if (_loadAudioButton != null && e.DragEffects != DragDropEffects.None)
        {
            _loadAudioButton.BorderBrush = new SolidColorBrush(Color.Parse("#FF2563EB"));
            _loadAudioButton.Background = new SolidColorBrush(Color.Parse("#10256BEB"));
        }
    }

    private void LoadAudioButton_DragLeave(object? sender, DragEventArgs e)
    {
        ResetLoadAudioButtonAppearance();
    }

    private void LoadAudioButton_Drop(object? sender, DragEventArgs e)
    {
        ResetLoadAudioButtonAppearance();

        var audioFiles = ExtractAudioPaths(e);
        if (audioFiles.Count > 0)
        {
            ImportAudioFiles(audioFiles, "LoadAudioButtonDragDrop");
        }
    }

    private void ResetLoadAudioButtonAppearance()
    {
        if (_loadAudioButton != null)
        {
            _loadAudioButton.ClearValue(Button.BorderBrushProperty);
            _loadAudioButton.ClearValue(Button.BackgroundProperty);
        }
    }

    private async void LoadAudioButton_Click(object? sender, RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider == null)
        {
            return;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("音频")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.flac", "*.m4a", "*.ogg", "*.wma", "*.aac" }
                }
            }
        });

        if (files == null || files.Count == 0)
        {
            return;
        }

        var audioPaths = files
            .Select(file => file.Path?.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Where(path => SupportedAudioExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        if (audioPaths.Count == 0)
        {
            return;
        }

        ImportAudioFiles(audioPaths, "LoadAudioButtonPicker");
    }

    private void ImportAudioFiles(IEnumerable<string> audioFiles, string source)
    {
        var sessionsDir = PathManager.Instance.SessionsPath;
        var importedCount = 0;

        CrashLogger.AddBreadcrumb($"{source}: importing audio files");

        foreach (var filePath in audioFiles)
        {
            try
            {
                Directory.CreateDirectory(sessionsDir);
                var destPath = Path.Combine(sessionsDir, Path.GetFileName(filePath));
                File.Copy(filePath, destPath, overwrite: false);
                importedCount++;
            }
            catch (IOException)
            {
                // 目标文件已存在，跳过
            }
            catch (Exception ex)
            {
                CrashLogger.AddBreadcrumb($"{source} copy failed: {ex.Message}");
            }
        }

        if (importedCount > 0)
        {
            ViewModel?.FileLibrary?.RefreshAudioLibraryCommand?.Execute(null);
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

        var generateSpeechItem = new MenuItem
        {
            Header = ViewModel?.BatchProcessing.GenerateSelectedSubtitleMenuText ?? "生成字幕",
            IsEnabled = ViewModel?.BatchProcessing.GenerateBatchSpeechSubtitleCommand.CanExecute(null) == true
        };
        generateSpeechItem.Click += (_, _) =>
        {
            ViewModel?.BatchProcessing.AuditUiEvent("AudioFileGenerateSpeech", $"flyout-click item={selectedItem.FullPath}");
            if (ViewModel?.BatchProcessing.GenerateBatchSpeechSubtitleCommand.CanExecute(null) == true)
            {
                ViewModel.BatchProcessing.GenerateBatchSpeechSubtitleCommand.Execute(null);
            }
        };
        flyout.Items.Add(generateSpeechItem);

        var enqueueItem = new MenuItem { Header = "加入批量字幕" };
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

        flyout.Items.Add(new Separator());

        var askAiItem = new MenuItem { Header = "以此向AI提问" };
        askAiItem.Click += (_, _) =>
        {
            var subtitleFilePath = ViewModel?.FileLibrary.SelectedSubtitleFile?.FullPath;
            if (string.IsNullOrWhiteSpace(subtitleFilePath) || !File.Exists(subtitleFilePath)) return;

            ViewModel?.NavigateToWorkshopWithAttachments?.Invoke(new[] { subtitleFilePath });
        };
        flyout.Items.Add(askAiItem);

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
