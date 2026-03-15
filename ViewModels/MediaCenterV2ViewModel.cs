using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 媒体中心 v2 ViewModel — 以媒体资产为中心的画廊/管理视图。
    /// 完全独立于 MediaStudioViewModel，可安全删除/回滚。
    /// </summary>
    public class MediaCenterV2ViewModel : ViewModelBase, IDisposable
    {
        // ── 媒体项集合 ──

        private readonly List<MediaAssetItem> _allItems = new();
        private ObservableCollection<MediaAssetItem> _filteredItems = new();
        public ObservableCollection<MediaAssetItem> FilteredItems
        {
            get => _filteredItems;
            private set => SetProperty(ref _filteredItems, value);
        }

        // ── 筛选状态 ──

        private MediaFilterCategory _selectedCategory = MediaFilterCategory.All;
        public MediaFilterCategory SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (SetProperty(ref _selectedCategory, value))
                {
                    ApplyFilter();
                }
            }
        }

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    ApplyFilter();
                }
            }
        }

        private MediaSortMode _sortMode = MediaSortMode.DateDescending;
        public MediaSortMode SortMode
        {
            get => _sortMode;
            set
            {
                if (SetProperty(ref _sortMode, value))
                {
                    ApplyFilter();
                }
            }
        }

        // ── 选中项 ──

        private MediaAssetItem? _selectedItem;
        public MediaAssetItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetProperty(ref _selectedItem, value))
                {
                    OnPropertyChanged(nameof(HasSelectedItem));
                }
            }
        }

        public bool HasSelectedItem => SelectedItem != null;

        // ── 统计 ──

        private int _totalCount;
        public int TotalCount
        {
            get => _totalCount;
            private set => SetProperty(ref _totalCount, value);
        }

        private int _imageCount;
        public int ImageCount
        {
            get => _imageCount;
            private set => SetProperty(ref _imageCount, value);
        }

        private int _videoCount;
        public int VideoCount
        {
            get => _videoCount;
            private set => SetProperty(ref _videoCount, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        private string _statusText = "就绪";
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        // ── 命令 ──

        public ICommand RefreshCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand OpenInExplorerCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        // ── 内部 ──

        private readonly string _studioDirectory;
        private CancellationTokenSource? _scanCts;
        private bool _disposed;

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mov", ".avi"
        };

        private static readonly HashSet<string> FirstFrameSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "_first_frame.png", "_first_frame.jpg"
        };

        public MediaCenterV2ViewModel()
        {
            var sessionsPath = PathManager.Instance.SessionsPath;
            _studioDirectory = Path.Combine(sessionsPath, "media-studio");

            RefreshCommand = new RelayCommand(_ => _ = ScanMediaAsync());
            OpenFileCommand = new RelayCommand(
                p => OpenFile(p as MediaAssetItem ?? SelectedItem),
                p => (p as MediaAssetItem ?? SelectedItem) != null);
            OpenInExplorerCommand = new RelayCommand(
                p => OpenInExplorer(p as MediaAssetItem ?? SelectedItem),
                p => (p as MediaAssetItem ?? SelectedItem) != null);
            ClearSelectionCommand = new RelayCommand(_ => SelectedItem = null);
        }

        /// <summary>
        /// 首次进入时调用；扫描磁盘上所有 media-studio 会话中的媒体文件。
        /// </summary>
        public async Task ScanMediaAsync()
        {
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            IsLoading = true;
            StatusText = "正在扫描媒体文件...";

            try
            {
                var items = await Task.Run(() => ScanDirectory(ct), ct);

                if (ct.IsCancellationRequested) return;

                _allItems.Clear();
                _allItems.AddRange(items);

                ImageCount = _allItems.Count(i => i.MediaType == MediaAssetType.Image);
                VideoCount = _allItems.Count(i => i.MediaType == MediaAssetType.Video);
                TotalCount = _allItems.Count;

                ApplyFilter();
                StatusText = $"共 {TotalCount} 个媒体文件（{ImageCount} 张图片，{VideoCount} 个视频）";
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                StatusText = $"扫描失败: {ex.Message}";
                Debug.WriteLine($"[MediaCenterV2] Scan error: {ex}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private List<MediaAssetItem> ScanDirectory(CancellationToken ct)
        {
            var results = new List<MediaAssetItem>();

            if (!Directory.Exists(_studioDirectory))
                return results;

            foreach (var sessionDir in Directory.GetDirectories(_studioDirectory, "session_*"))
            {
                ct.ThrowIfCancellationRequested();

                var sessionName = Path.GetFileName(sessionDir);

                foreach (var filePath in Directory.EnumerateFiles(sessionDir))
                {
                    ct.ThrowIfCancellationRequested();

                    var ext = Path.GetExtension(filePath);
                    var fileName = Path.GetFileName(filePath);

                    // Skip first-frame thumbnails — they are paired with video files
                    if (IsFirstFrameFile(fileName))
                        continue;

                    MediaAssetType? type = null;
                    if (ImageExtensions.Contains(ext))
                        type = MediaAssetType.Image;
                    else if (VideoExtensions.Contains(ext))
                        type = MediaAssetType.Video;

                    if (type == null)
                        continue;

                    var info = new FileInfo(filePath);
                    var item = new MediaAssetItem
                    {
                        FilePath = filePath,
                        FileName = fileName,
                        MediaType = type.Value,
                        FileSize = info.Length,
                        CreatedAt = info.CreationTime,
                        ModifiedAt = info.LastWriteTime,
                        SessionName = sessionName,
                    };

                    // Look for a paired first-frame thumbnail for video files
                    if (type == MediaAssetType.Video)
                    {
                        var baseName = Path.GetFileNameWithoutExtension(filePath);
                        var dir = Path.GetDirectoryName(filePath)!;
                        var firstFrame = Path.Combine(dir, baseName + "_first_frame.png");
                        if (File.Exists(firstFrame))
                        {
                            item.ThumbnailPath = firstFrame;
                        }
                    }
                    else
                    {
                        // For images, the file itself is the thumbnail
                        item.ThumbnailPath = filePath;
                    }

                    results.Add(item);
                }
            }

            return results;
        }

        private static bool IsFirstFrameFile(string fileName)
        {
            return FirstFrameSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplyFilter()
        {
            IEnumerable<MediaAssetItem> query = _allItems;

            // Category filter
            query = _selectedCategory switch
            {
                MediaFilterCategory.Images => query.Where(i => i.MediaType == MediaAssetType.Image),
                MediaFilterCategory.Videos => query.Where(i => i.MediaType == MediaAssetType.Video),
                _ => query
            };

            // Text search
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                var term = _searchText.Trim();
                query = query.Where(i =>
                    i.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    i.SessionName.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            // Sort
            query = _sortMode switch
            {
                MediaSortMode.DateAscending => query.OrderBy(i => i.ModifiedAt),
                MediaSortMode.NameAscending => query.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
                MediaSortMode.NameDescending => query.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase),
                MediaSortMode.SizeDescending => query.OrderByDescending(i => i.FileSize),
                MediaSortMode.SizeAscending => query.OrderBy(i => i.FileSize),
                _ => query.OrderByDescending(i => i.ModifiedAt) // DateDescending default
            };

            var result = query.ToList();

            Dispatcher.UIThread.Post(() =>
            {
                _filteredItems.Clear();
                foreach (var item in result)
                {
                    _filteredItems.Add(item);
                }
            });
        }

        private static void OpenFile(MediaAssetItem? item)
        {
            if (item == null || !File.Exists(item.FilePath)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Open file error: {ex}");
            }
        }

        private static void OpenInExplorer(MediaAssetItem? item)
        {
            if (item == null || !File.Exists(item.FilePath)) return;

            try
            {
                var dir = Path.GetDirectoryName(item.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCenterV2] Open explorer error: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _scanCts?.Cancel();
            _scanCts?.Dispose();
        }
    }

    // ── 支撑类型 ──

    public enum MediaFilterCategory
    {
        All,
        Images,
        Videos
    }

    public enum MediaSortMode
    {
        DateDescending,
        DateAscending,
        NameAscending,
        NameDescending,
        SizeDescending,
        SizeAscending
    }

    public enum MediaAssetType
    {
        Image,
        Video
    }

    public class MediaAssetItem : ViewModelBase
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public MediaAssetType MediaType { get; set; }
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string SessionName { get; set; } = string.Empty;
        public string? ThumbnailPath { get; set; }

        public bool IsImage => MediaType == MediaAssetType.Image;
        public bool IsVideo => MediaType == MediaAssetType.Video;

        public string TypeDisplayText => MediaType == MediaAssetType.Image ? "图片" : "视频";
        public string TypeIcon => MediaType == MediaAssetType.Image ? "🖼️" : "🎬";

        public string FileSizeText
        {
            get
            {
                if (FileSize < 1024) return $"{FileSize} B";
                if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
                return $"{FileSize / (1024.0 * 1024.0):F1} MB";
            }
        }

        public string DateText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");
    }
}
