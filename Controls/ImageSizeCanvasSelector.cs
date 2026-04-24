using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using TrueFluentPro.Helpers;

namespace TrueFluentPro.Controls
{
    /// <summary>
    /// 可视化画布尺寸选择器。用户拖拽右下角调整图片尺寸，自动 snap 到 16px 网格。
    /// 独立控件，不依赖任何 ViewModel，通过 StyledProperty 双向绑定。
    /// </summary>
    public class ImageSizeCanvasSelector : TemplatedControl
    {
        // ── StyledProperty ──

        public static readonly StyledProperty<string> SelectedSizeProperty =
            AvaloniaProperty.Register<ImageSizeCanvasSelector, string>(
                nameof(SelectedSize), "1024x1024", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        public static readonly StyledProperty<string> SelectedQualityProperty =
            AvaloniaProperty.Register<ImageSizeCanvasSelector, string>(
                nameof(SelectedQuality), "medium", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        public static readonly DirectProperty<ImageSizeCanvasSelector, string> SizeInfoTextProperty =
            AvaloniaProperty.RegisterDirect<ImageSizeCanvasSelector, string>(
                nameof(SizeInfoText), o => o.SizeInfoText);

        public static readonly DirectProperty<ImageSizeCanvasSelector, int> EstimatedTokensProperty =
            AvaloniaProperty.RegisterDirect<ImageSizeCanvasSelector, int>(
                nameof(EstimatedTokens), o => o.EstimatedTokens);

        public string SelectedSize
        {
            get => GetValue(SelectedSizeProperty);
            set => SetValue(SelectedSizeProperty, value);
        }

        public string SelectedQuality
        {
            get => GetValue(SelectedQualityProperty);
            set => SetValue(SelectedQualityProperty, value);
        }

        private string _sizeInfoText = "";
        public string SizeInfoText
        {
            get => _sizeInfoText;
            private set => SetAndRaise(SizeInfoTextProperty, ref _sizeInfoText, value);
        }

        private int _estimatedTokens;
        public int EstimatedTokens
        {
            get => _estimatedTokens;
            private set => SetAndRaise(EstimatedTokensProperty, ref _estimatedTokens, value);
        }

        // ── 内部状态 ──
        private int _imageWidth = 1024;
        private int _imageHeight = 1024;
        private bool _isDragging;
        private Point _dragStart;
        private int _dragStartW, _dragStartH;

        // 画布参数
        private const double CanvasSize = 240;
        private const double ScaleFactor = CanvasSize / ImageSizeCalculator.MaxEdge; // 每像素对应的画布 dp

        // 模板控件引用
        private Border? _sizeRect;
        private Border? _dragHandle;
        private TextBlock? _dimensionLabel;
        private Panel? _canvasArea;

        static ImageSizeCanvasSelector()
        {
            SelectedSizeProperty.Changed.AddClassHandler<ImageSizeCanvasSelector>((s, e) => s.OnSelectedSizeChanged());
            SelectedQualityProperty.Changed.AddClassHandler<ImageSizeCanvasSelector>((s, e) => s.UpdateInfoText());
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            _sizeRect = e.NameScope.Find<Border>("PART_SizeRect");
            _dimensionLabel = e.NameScope.Find<TextBlock>("PART_DimensionLabel");
            _canvasArea = e.NameScope.Find<Panel>("PART_CanvasArea");
            _dragHandle = e.NameScope.Find<Border>("PART_DragHandle");

            // 拖拽手柄
            if (_dragHandle != null)
            {
                _dragHandle.PointerPressed += OnHandlePointerPressed;
                _dragHandle.PointerMoved += OnHandlePointerMoved;
                _dragHandle.PointerReleased += OnHandlePointerReleased;
            }

            // 画布点击直接定位
            if (_canvasArea != null)
            {
                _canvasArea.PointerPressed += OnCanvasPointerPressed;
            }

            OnSelectedSizeChanged();
        }

        private void OnSelectedSizeChanged()
        {
            if (ImageSizeCalculator.TryParse(SelectedSize, out var w, out var h))
            {
                _imageWidth = w;
                _imageHeight = h;
            }
            else if (SelectedSize == "auto")
            {
                _imageWidth = 1024;
                _imageHeight = 1024;
            }
            UpdateVisuals();
            UpdateInfoText();
        }

        private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_canvasArea == null) return;
            var point = e.GetPosition(_canvasArea);
            SetSizeFromCanvasPoint(point.X, point.Y);
        }

        private void OnHandlePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border handle) return;
            _isDragging = true;
            _dragStart = e.GetPosition(_canvasArea ?? (Control)this);
            _dragStartW = _imageWidth;
            _dragStartH = _imageHeight;
            e.Pointer.Capture(handle);
            e.Handled = true;
        }

        private void OnHandlePointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isDragging || _canvasArea == null) return;
            var current = e.GetPosition(_canvasArea);
            SetSizeFromCanvasPoint(current.X, current.Y);
            e.Handled = true;
        }

        private void OnHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void SetSizeFromCanvasPoint(double canvasX, double canvasY)
        {
            // 画布坐标 → 图片像素
            var rawW = (int)(canvasX / ScaleFactor);
            var rawH = (int)(canvasY / ScaleFactor);

            var aligned = ImageSizeCalculator.AlignToGrid(rawW, rawH);

            // 边界保护：AlignToGrid 应总是合法，但若仍有校验失败则不提交
            if (ImageSizeCalculator.Validate(aligned.Width, aligned.Height) != null)
                return;

            _imageWidth = aligned.Width;
            _imageHeight = aligned.Height;

            SelectedSize = aligned.ToSizeString();
            UpdateVisuals();
            UpdateInfoText();
        }

        private void UpdateVisuals()
        {
            if (_sizeRect == null) return;

            var rectW = Math.Max(16, _imageWidth * ScaleFactor);
            var rectH = Math.Max(16, _imageHeight * ScaleFactor);

            _sizeRect.Width = rectW;
            _sizeRect.Height = rectH;

            if (_dimensionLabel != null)
                _dimensionLabel.Text = $"{_imageWidth} × {_imageHeight}";

            // 拖拽手柄定位到矩形右下角
            if (_dragHandle != null)
            {
                _dragHandle.Margin = new Thickness(rectW - 7, rectH - 7, 0, 0);
            }
        }

        private void UpdateInfoText()
        {
            var totalPx = (long)_imageWidth * _imageHeight;
            var ratio = GcdRatio(_imageWidth, _imageHeight);
            var tokens = ImageSizeCalculator.EstimateOutputTokens(_imageWidth, _imageHeight, SelectedQuality);
            var validation = ImageSizeCalculator.Validate(_imageWidth, _imageHeight);
            var experimental = totalPx > ImageSizeCalculator.ExperimentalPixelThreshold ? " ⚠️ 2K+" : "";

            EstimatedTokens = tokens;
            SizeInfoText = validation != null
                ? $"⚠ {validation}"
                : $"{totalPx:N0} px  |  {ratio}  |  ~{tokens} token{experimental}";
        }

        private static string GcdRatio(int w, int h)
        {
            var g = Gcd(w, h);
            return g > 0 ? $"{w / g}:{h / g}" : $"{w}:{h}";
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0) { var t = b; b = a % b; a = t; }
            return a;
        }
    }
}
