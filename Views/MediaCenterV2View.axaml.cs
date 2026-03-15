using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    /// <summary>
    /// 媒体中心 v2 — 以媒体资产画廊为中心的浏览视图。
    /// 完全独立于 MediaStudioView，可安全删除/回滚。
    /// </summary>
    public partial class MediaCenterV2View : UserControl
    {
        private MediaCenterV2ViewModel? _viewModel;
        private bool _initialized;

        public MediaCenterV2View()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 懒初始化：首次导航到该页面时由 MainWindow 调用。
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _viewModel = new MediaCenterV2ViewModel();
            DataContext = _viewModel;

            _ = _viewModel.ScanMediaAsync().ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    Debug.WriteLine($"[MediaCenterV2] Initial scan error: {t.Exception}");
                }
            }, TaskScheduler.Default);
        }

        public void Cleanup()
        {
            _viewModel?.Dispose();
        }

        // ── 分类筛选点击 ──

        private void CategoryAll_Click(object? sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.SelectedCategory = MediaFilterCategory.All;
        }

        private void CategoryImages_Click(object? sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.SelectedCategory = MediaFilterCategory.Images;
        }

        private void CategoryVideos_Click(object? sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.SelectedCategory = MediaFilterCategory.Videos;
        }

        // ── 排序切换 ──

        private void SortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_viewModel == null || sender is not ComboBox cb) return;

            _viewModel.SortMode = cb.SelectedIndex switch
            {
                0 => MediaSortMode.DateDescending,
                1 => MediaSortMode.DateAscending,
                2 => MediaSortMode.NameAscending,
                3 => MediaSortMode.NameDescending,
                4 => MediaSortMode.SizeDescending,
                5 => MediaSortMode.SizeAscending,
                _ => MediaSortMode.DateDescending
            };
        }

        // ── 画廊项交互 ──

        private void GalleryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel == null) return;
            if (sender is Border { DataContext: MediaAssetItem item })
            {
                _viewModel.SelectedItem = item;
            }
        }

        private void GalleryItem_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (_viewModel == null) return;
            if (sender is Border { DataContext: MediaAssetItem item })
            {
                _viewModel.OpenFileCommand.Execute(item);
            }
        }
    }
}
