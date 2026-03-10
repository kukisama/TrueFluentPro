using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace TrueFluentPro.Controls;

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

    private void OnFilePathChanged()
    {
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

            var estimatedHeight = ResolveEstimatedExtent(Bounds.Height, MaxHeight, Height, 120);
            var top = point.Value.Y;
            var bottom = top + estimatedHeight;
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

        Source = bitmap;
        _bitmapLoaded = true;
        LayoutUpdated -= ViewportLazyImage_LayoutUpdated;
        if (_ownerScrollViewer != null)
        {
            _ownerScrollViewer.ScrollChanged -= OwnerScrollViewer_ScrollChanged;
        }
    }

    private static double ResolveEstimatedExtent(double actual, double max, double configured, double fallback)
    {
        if (actual > 0)
        {
            return actual;
        }

        if (!double.IsNaN(configured) && configured > 0)
        {
            return configured;
        }

        if (!double.IsNaN(max) && max > 0)
        {
            return max;
        }

        return fallback;
    }
}