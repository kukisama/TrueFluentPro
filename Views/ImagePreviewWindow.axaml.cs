using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TrueFluentPro.Views
{
    public partial class ImagePreviewWindow : Window
    {
        private readonly List<string> _filePaths;
        private int _currentIndex;
        private Bitmap? _bitmap;

        // ===== 缩放 & 平移状态 =====
        private double _scale = 1.0;
        private double _translateX, _translateY;
        private bool _isDragging;
        private Point _dragStart;
        private double _dragStartTx, _dragStartTy;

        private const double MinScale = 0.1;   // 10%
        private const double MaxScale = 8.0;    // 800%
        private const double ZoomFactor = 1.15;
        private readonly MatrixTransform _matrixTransform = new();

        public ImagePreviewWindow()
        {
            InitializeComponent();
            _filePaths = new List<string>();
            _currentIndex = 0;
            PreviewImage.RenderTransform = _matrixTransform;
        }

        public ImagePreviewWindow(string filePath) : this(new[] { filePath }, 0)
        {
        }

        public ImagePreviewWindow(IReadOnlyList<string> filePaths, int startIndex = 0)
        {
            InitializeComponent();
            PreviewImage.RenderTransform = _matrixTransform;

            _filePaths = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .ToList();

            if (_filePaths.Count == 0)
            {
                _filePaths.Add(filePaths.FirstOrDefault() ?? "");
            }

            _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, _filePaths.Count - 1));

            LoadImage();
            SetupEventHandlers();
        }

        // ============================== 图片加载 ==============================

        private void LoadImage()
        {
            try
            {
                if (_filePaths.Count == 0)
                {
                    FileInfoTextBlock.Text = "未找到图片";
                    return;
                }

                var filePath = _filePaths[_currentIndex];
                if (!File.Exists(filePath))
                {
                    FileInfoTextBlock.Text = "文件不存在";
                    return;
                }

                _bitmap?.Dispose();
                _bitmap = new Bitmap(filePath);
                PreviewImage.Source = _bitmap;
                MiniMapImage.Source = _bitmap;

                Title = $"图片预览 — {Path.GetFileName(filePath)}";

                var fileInfo = new FileInfo(filePath);
                var sizeKb = fileInfo.Length / 1024.0;
                FileInfoTextBlock.Text = $"{_bitmap.PixelSize.Width}×{_bitmap.PixelSize.Height}  |  {sizeKb:F1} KB  |  {Path.GetFileName(filePath)}  |  {_currentIndex + 1}/{_filePaths.Count}";

                UpdateNavigationButtons();
                UpdateMetaInfo(filePath, fileInfo);

                // 初始适应窗口
                FitToWindow();
            }
            catch (Exception ex)
            {
                FileInfoTextBlock.Text = $"加载失败: {ex.Message}";
            }
        }

        // ============================== 缩放 & 平移 ==============================

        private void ApplyTransform()
        {
            _matrixTransform.Matrix = new Matrix(_scale, 0, 0, _scale, _translateX, _translateY);
            UpdateZoomText();
            UpdateMiniMap();
        }

        private void UpdateZoomText()
        {
            ZoomTextBlock.Text = $"{_scale * 100:F0}%";
        }

        private void FitToWindow()
        {
            if (_bitmap == null) return;

            var vw = ViewportBorder.Bounds.Width;
            var vh = ViewportBorder.Bounds.Height;
            if (vw <= 0 || vh <= 0) return; // 尚未布局，ViewportBorder.SizeChanged 会再次调用

            var iw = (double)_bitmap.PixelSize.Width;
            var ih = (double)_bitmap.PixelSize.Height;

            var fitScale = Math.Min(vw / iw, vh / ih);
            if (fitScale > 1) fitScale = 1; // 不放大小图

            _scale = fitScale;
            _translateX = (vw - iw * _scale) / 2;
            _translateY = (vh - ih * _scale) / 2;
            ApplyTransform();
        }

        private void SetActualSize()
        {
            if (_bitmap == null) return;

            var vw = ViewportBorder.Bounds.Width;
            var vh = ViewportBorder.Bounds.Height;
            var iw = (double)_bitmap.PixelSize.Width;
            var ih = (double)_bitmap.PixelSize.Height;

            _scale = 1.0;
            _translateX = (vw - iw) / 2;
            _translateY = (vh - ih) / 2;
            ApplyTransform();
        }

        private void ZoomAtPoint(Point viewportPoint, double factor)
        {
            var newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
            if (Math.Abs(newScale - _scale) < 1e-9) return;

            // 鼠标在图片坐标系中的位置
            var imgX = (viewportPoint.X - _translateX) / _scale;
            var imgY = (viewportPoint.Y - _translateY) / _scale;

            _scale = newScale;

            // 重新计算平移，使鼠标指向的图片位置不变
            _translateX = viewportPoint.X - imgX * _scale;
            _translateY = viewportPoint.Y - imgY * _scale;
            ApplyTransform();
        }

        // ============================== Mini-map ==============================

        private void UpdateMiniMap()
        {
            if (_bitmap == null) return;

            var vw = ViewportBorder.Bounds.Width;
            var vh = ViewportBorder.Bounds.Height;
            if (vw <= 0 || vh <= 0) return;

            var iw = (double)_bitmap.PixelSize.Width;
            var ih = (double)_bitmap.PixelSize.Height;

            // 仅当图片超出视口时显示 mini-map
            var scaledW = iw * _scale;
            var scaledH = ih * _scale;
            var needsMiniMap = scaledW > vw * 1.05 || scaledH > vh * 1.05;
            MiniMapBorder.IsVisible = needsMiniMap;
            if (!needsMiniMap) return;

            // mini-map 内部可用尺寸
            var mmW = MiniMapBorder.Width - 6;  // 减去 Margin=3 × 2
            var mmH = MiniMapBorder.Height - 6;
            var mmScale = Math.Min(mmW / iw, mmH / ih);

            // 视口在图片坐标系中的矩形
            var vpLeft = -_translateX / _scale;
            var vpTop = -_translateY / _scale;
            var vpRight = (vw - _translateX) / _scale;
            var vpBottom = (vh - _translateY) / _scale;

            // 裁切到图片范围
            vpLeft = Math.Max(0, vpLeft);
            vpTop = Math.Max(0, vpTop);
            vpRight = Math.Min(iw, vpRight);
            vpBottom = Math.Min(ih, vpBottom);

            // 图片在 mini-map 中的偏移（居中）
            var imgOffsetX = (mmW - iw * mmScale) / 2 + 3;
            var imgOffsetY = (mmH - ih * mmScale) / 2 + 3;

            MiniMapViewRect.Width = Math.Max(4, (vpRight - vpLeft) * mmScale);
            MiniMapViewRect.Height = Math.Max(4, (vpBottom - vpTop) * mmScale);
            MiniMapViewRect.Margin = new Thickness(
                imgOffsetX + vpLeft * mmScale,
                imgOffsetY + vpTop * mmScale,
                0, 0);
        }

        // ============================== 元信息 ==============================

        private void UpdateMetaInfo(string filePath, FileInfo fileInfo)
        {
            if (_bitmap == null) return;

            MetaSizeText.Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height} px";

            var ext = Path.GetExtension(filePath).ToUpperInvariant().TrimStart('.');
            MetaFormatText.Text = ext;

            var size = fileInfo.Length;
            MetaFileSizeText.Text = size < 1024 * 1024
                ? $"{size / 1024.0:F1} KB"
                : $"{size / (1024.0 * 1024.0):F2} MB";

            MetaDpiText.Text = $"{_bitmap.Dpi.X:F0} × {_bitmap.Dpi.Y:F0}";
            MetaPathText.Text = filePath;
            MetaDateText.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
        }

        // ============================== 事件处理 ==============================

        private void SetupEventHandlers()
        {
            PrevButton.Click += (_, _) => Navigate(-1);
            NextButton.Click += (_, _) => Navigate(1);
            DownloadButton.Click += async (_, _) => await DownloadAsync();
            OpenLocationButton.Click += (_, _) => OpenLocation();
            CopyButton.Click += async (_, _) => await CopyToClipboardAsync();
            FitButton.Click += (_, _) => FitToWindow();
            ActualSizeButton.Click += (_, _) => SetActualSize();
            InfoButton.Click += (_, _) => InfoPanel.IsVisible = !InfoPanel.IsVisible;

            KeyDown += OnWindowKeyDown;

            // 缩放：滚轮
            ViewportBorder.PointerWheelChanged += OnViewportWheelChanged;

            // 拖拽：左键
            ViewportBorder.PointerPressed += OnViewportPointerPressed;
            ViewportBorder.PointerMoved += OnViewportPointerMoved;
            ViewportBorder.PointerReleased += OnViewportPointerReleased;

            // 双击：切换 适应/100%
            ViewportBorder.DoubleTapped += OnViewportDoubleTapped;

            // ViewportBorder 获得/变更实际尺寸时自动适应（覆盖初始布局 + 窗口缩放）
            ViewportBorder.SizeChanged += OnViewportSizeChanged;
        }

        private void OnViewportWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            var pos = e.GetPosition(ViewportBorder);
            var factor = e.Delta.Y > 0 ? ZoomFactor : 1.0 / ZoomFactor;
            ZoomAtPoint(pos, factor);
            e.Handled = true;
        }

        private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(ViewportBorder).Properties.IsLeftButtonPressed) return;

            _isDragging = true;
            _dragStart = e.GetPosition(ViewportBorder);
            _dragStartTx = _translateX;
            _dragStartTy = _translateY;
            ViewportBorder.Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(ViewportBorder);
            e.Handled = true;
        }

        private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isDragging) return;

            var pos = e.GetPosition(ViewportBorder);
            _translateX = _dragStartTx + (pos.X - _dragStart.X);
            _translateY = _dragStartTy + (pos.Y - _dragStart.Y);
            ApplyTransform();
            e.Handled = true;
        }

        private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDragging) return;

            _isDragging = false;
            ViewportBorder.Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void OnViewportDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (_bitmap == null) return;

            var vw = ViewportBorder.Bounds.Width;
            var vh = ViewportBorder.Bounds.Height;
            var iw = (double)_bitmap.PixelSize.Width;
            var ih = (double)_bitmap.PixelSize.Height;
            var fitScale = Math.Min(vw / iw, vh / ih);
            if (fitScale > 1) fitScale = 1;

            // 如果当前接近 fit，切换到 100%；否则切换到 fit
            if (Math.Abs(_scale - fitScale) < 0.01)
                SetActualSize();
            else
                FitToWindow();

            e.Handled = true;
        }

        private void Navigate(int offset)
        {
            if (_filePaths.Count <= 1) return;

            var nextIndex = _currentIndex + offset;
            if (nextIndex < 0 || nextIndex >= _filePaths.Count) return;

            _currentIndex = nextIndex;
            LoadImage();
        }

        private void UpdateNavigationButtons()
        {
            PrevButton.IsEnabled = _currentIndex > 0;
            NextButton.IsEnabled = _currentIndex < _filePaths.Count - 1;
        }

        private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_bitmap != null && !_isDragging)
                FitToWindow();
        }

        private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (Width < 480) Width = 480;
            if (Height < 200) Height = 200;
        }

        // ============================== 快捷键 ==============================

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Left:
                    Navigate(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                    Navigate(1);
                    e.Handled = true;
                    break;
                case Key.OemPlus or Key.Add:
                    ZoomAtCenter(ZoomFactor);
                    e.Handled = true;
                    break;
                case Key.OemMinus or Key.Subtract:
                    ZoomAtCenter(1.0 / ZoomFactor);
                    e.Handled = true;
                    break;
                case Key.D0 or Key.NumPad0 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    FitToWindow();
                    e.Handled = true;
                    break;
                case Key.D1 or Key.NumPad1 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    SetActualSize();
                    e.Handled = true;
                    break;
                case Key.I when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    InfoPanel.IsVisible = !InfoPanel.IsVisible;
                    e.Handled = true;
                    break;
            }
        }

        private void ZoomAtCenter(double factor)
        {
            var cx = ViewportBorder.Bounds.Width / 2;
            var cy = ViewportBorder.Bounds.Height / 2;
            ZoomAtPoint(new Point(cx, cy), factor);
        }

        // ============================== 文件操作 ==============================

        private async Task DownloadAsync()
        {
            if (_filePaths.Count == 0) return;

            var filePath = _filePaths[_currentIndex];
            if (!File.Exists(filePath)) return;

            var storageProvider = StorageProvider;
            var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存图片",
                SuggestedFileName = Path.GetFileName(filePath)
            });

            if (result == null) return;

            await using var source = File.OpenRead(filePath);
            await using var target = await result.OpenWriteAsync();
            await source.CopyToAsync(target);
        }

        private void OpenLocation()
        {
            if (_filePaths.Count == 0) return;

            var filePath = _filePaths[_currentIndex];
            if (!File.Exists(filePath)) return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch { }
        }

        private async Task CopyToClipboardAsync()
        {
            if (_filePaths.Count == 0) return;
            var filePath = _filePaths[_currentIndex];
            if (!File.Exists(filePath)) return;

            if (OperatingSystem.IsWindows())
            {
                if (TryCopyImageToWindowsClipboard(filePath))
                    return;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(filePath);
        }

        // ========== Windows 剪贴板图片复制（GDI+ → CF_DIB） ==========

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("gdiplus.dll")]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll")]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipSaveImageToFile(IntPtr image, string filename, ref Guid clsidEncoder, IntPtr encoderParams);

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        private const uint CF_DIB = 8;
        private const uint GMEM_MOVEABLE = 0x0002;

        private static bool TryCopyImageToWindowsClipboard(string filePath)
        {
            IntPtr gdipToken = IntPtr.Zero;
            IntPtr gdipBitmap = IntPtr.Zero;
            string? tempBmpPath = null;

            try
            {
                var startupInput = new GdiplusStartupInput { GdiplusVersion = 1 };
                if (GdiplusStartup(out gdipToken, ref startupInput, IntPtr.Zero) != 0)
                    return false;

                if (GdipCreateBitmapFromFile(filePath, out gdipBitmap) != 0)
                    return false;

                tempBmpPath = Path.Combine(Path.GetTempPath(), $"clipboard_{Guid.NewGuid():N}.bmp");
                var bmpEncoderClsid = new Guid("557cf400-1a04-11d3-9a73-0000f81ef32e");
                if (GdipSaveImageToFile(gdipBitmap, tempBmpPath, ref bmpEncoderClsid, IntPtr.Zero) != 0)
                    return false;

                GdipDisposeImage(gdipBitmap);
                gdipBitmap = IntPtr.Zero;
                GdiplusShutdown(gdipToken);
                gdipToken = IntPtr.Zero;

                var bmpData = File.ReadAllBytes(tempBmpPath);
                if (bmpData.Length <= 54)
                    return false;

                const int bmpFileHeaderSize = 14;
                var dibLength = bmpData.Length - bmpFileHeaderSize;

                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dibLength);
                if (hMem == IntPtr.Zero)
                    return false;

                var pMem = GlobalLock(hMem);
                if (pMem == IntPtr.Zero)
                    return false;

                try
                {
                    Marshal.Copy(bmpData, bmpFileHeaderSize, pMem, dibLength);
                }
                finally
                {
                    GlobalUnlock(hMem);
                }

                if (!OpenClipboard(IntPtr.Zero))
                    return false;

                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CF_DIB, hMem) != IntPtr.Zero)
                        return true;
                    return false;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"剪贴板图片复制失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (gdipBitmap != IntPtr.Zero) GdipDisposeImage(gdipBitmap);
                if (gdipToken != IntPtr.Zero) GdiplusShutdown(gdipToken);
                if (tempBmpPath != null)
                {
                    try { File.Delete(tempBmpPath); } catch { }
                }
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            base.OnClosing(e);
        }
    }
}
