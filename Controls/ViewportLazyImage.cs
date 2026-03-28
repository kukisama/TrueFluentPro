using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace TrueFluentPro.Controls;

/// <summary>
/// 延迟加载图片控件。核心保证：Width/Height 在 FilePath 设置后立即固定，
/// 无论 bitmap 是否已加载，控件高度不变，杜绝虚拟化 Extent 震荡。
/// </summary>
public sealed class ViewportLazyImage : Image
{
    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<ViewportLazyImage, string?>(nameof(FilePath));

    private ScrollViewer? _ownerScrollViewer;
    private bool _bitmapLoaded;

    static ViewportLazyImage()
    {
        FilePathProperty.Changed.AddClassHandler<ViewportLazyImage>((image, _) => image.OnFilePathChanged());
    }

    public ViewportLazyImage()
    {
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ownerScrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_ownerScrollViewer != null)
        {
            _ownerScrollViewer.ScrollChanged += OwnerScrollViewer_ScrollChanged;
        }

        LayoutUpdated += ViewportLazyImage_LayoutUpdated;
        TryLoadWhenVisible();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= ViewportLazyImage_LayoutUpdated;

        if (_ownerScrollViewer != null)
        {
            _ownerScrollViewer.ScrollChanged -= OwnerScrollViewer_ScrollChanged;
            _ownerScrollViewer = null;
        }
    }

    /// <summary>
    /// 根据 MaxWidth/MaxHeight 和像素尺寸计算并锁定 Width/Height，
    /// 确保控件在 Source 为空时也保持正确占位。
    /// </summary>
    private void ApplyFixedSize(PixelSize pixel)
    {
        double maxW = MaxWidth is double mw && !double.IsNaN(mw) && !double.IsInfinity(mw) ? mw : 200;
        double maxH = MaxHeight is double mh && !double.IsNaN(mh) && !double.IsInfinity(mh) ? mh : 200;
        var display = FilePathToBitmapConverter.CalculateDisplaySize(pixel, maxW, maxH);
        Width = display.Width;
        Height = display.Height;
    }

    private void OnFilePathChanged()
    {
        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _bitmapLoaded = false;
            Source = null;
            Width = double.NaN;
            Height = double.NaN;
            return;
        }

        // ① 尝试 LRU bitmap 缓存直接命中（虚拟化回收最常见路径）
        var cached = FilePathToBitmapConverter.TryGetBitmap(path);
        if (cached != null)
        {
            ApplyFixedSize(cached.PixelSize);
            Source = cached;
            _bitmapLoaded = true;
            LayoutUpdated -= ViewportLazyImage_LayoutUpdated;
            if (_ownerScrollViewer != null)
                _ownerScrollViewer.ScrollChanged -= OwnerScrollViewer_ScrollChanged;
            return;
        }

        // ② bitmap 未命中但尺寸缓存/PNG头可用 → 立即锁定占位高度
        var pixel = FilePathToBitmapConverter.TryGetPixelSize(path);
        if (pixel.HasValue)
        {
            ApplyFixedSize(pixel.Value);
        }
        else
        {
            // 所有手段都无效时用 MaxWidth×MaxHeight 做保守占位
            double maxW = MaxWidth is double mw && !double.IsNaN(mw) && !double.IsInfinity(mw) ? mw : 200;
            double maxH = MaxHeight is double mh && !double.IsNaN(mh) && !double.IsInfinity(mh) ? mh : 200;
            Width = maxW;
            Height = maxH;
        }

        // Source 设 null 但 Width/Height 已锁定，高度不变
        _bitmapLoaded = false;
        Source = null;

        LayoutUpdated -= ViewportLazyImage_LayoutUpdated;
        LayoutUpdated += ViewportLazyImage_LayoutUpdated;

        if (_ownerScrollViewer != null)
        {
            _ownerScrollViewer.ScrollChanged -= OwnerScrollViewer_ScrollChanged;
            _ownerScrollViewer.ScrollChanged += OwnerScrollViewer_ScrollChanged;
        }

        TryLoadWhenVisible();
    }

    private void OwnerScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        TryLoadWhenVisible();
    }

    private void ViewportLazyImage_LayoutUpdated(object? sender, EventArgs e)
    {
        TryLoadWhenVisible();
    }

    private void TryLoadWhenVisible()
    {
        if (_bitmapLoaded || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        if (!IsEffectivelyVisible)
        {
            return;
        }

        if (_ownerScrollViewer != null)
        {
            var point = this.TranslatePoint(new Point(0, 0), _ownerScrollViewer);
            if (!point.HasValue)
            {
                return;
            }

            // Height 已被 OnFilePathChanged 锁定，直接用
            var h = !double.IsNaN(Height) && Height > 0 ? Height : 120;
            var top = point.Value.Y;
            var bottom = top + h;
            const double preloadMargin = 160;
            var viewportTop = -preloadMargin;
            var viewportBottom = _ownerScrollViewer.Viewport.Height + preloadMargin;
            if (bottom < viewportTop || top > viewportBottom)
            {
                return;
            }
        }

        Bitmap? bitmap = FilePathToBitmapConverter.TryGetBitmap(FilePath);
        if (bitmap == null)
        {
            return;
        }

        ApplyFixedSize(bitmap.PixelSize);
        Source = bitmap;
        _bitmapLoaded = true;
        LayoutUpdated -= ViewportLazyImage_LayoutUpdated;
        if (_ownerScrollViewer != null)
        {
            _ownerScrollViewer.ScrollChanged -= OwnerScrollViewer_ScrollChanged;
        }
        TrueFluentPro.Helpers.ScrollDiagLog.Log($"[LazyImg] 首次加载 {System.IO.Path.GetFileName(FilePath)} size={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}");
    }
}