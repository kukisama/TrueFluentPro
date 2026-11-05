using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TrueFluentPro.Views;

/// <summary>仅绘制书体；书缘折口、纸面及装订线共用设计坐标中的 SpinePosition。</summary>
public class SongScrollBookSurface : Control
{
    internal const double PaperLeft = 343;
    internal const double PaperRight = 2521;
    internal const double PaperWidth = PaperRight - PaperLeft;
    internal const string AssetRoot = "avares://TrueFluentPro/Assets/Appearance/SongScroll/";

    public static readonly StyledProperty<double> SpinePositionProperty =
        AvaloniaProperty.Register<SongScrollBookSurface, double>(nameof(SpinePosition), 1378,
            coerce: (_, value) => double.IsFinite(value)
                ? Math.Clamp(value, PaperLeft + PaperWidth * .25, PaperLeft + PaperWidth * .55) : 1378);
    public static readonly StyledProperty<double> SceneHeightProperty =
        AvaloniaProperty.Register<SongScrollBookSurface, double>(nameof(SceneHeight), 1440,
            coerce: (_, value) => double.IsFinite(value) ? Math.Max(1, value) : 1440);

    public double SpinePosition { get => GetValue(SpinePositionProperty); set => SetValue(SpinePositionProperty, value); }
    public double SceneHeight { get => GetValue(SceneHeightProperty); set => SetValue(SceneHeightProperty, value); }

    private readonly Dictionary<string, Bitmap> _images = new();
    private ImageBrush? _paper;
    private readonly IBrush _cover = Brush("#56716B");
    private readonly Pen _coverEdge = new(Brush("#786B55"), 8);
    private readonly BoxShadows _coverShadow = BoxShadows.Parse("1 20 28 0 #554B3C28");
    private readonly BoxShadows _leftShadow = BoxShadows.Parse("-15 0 22 0 #228C7951 inset");
    private readonly BoxShadows _rightShadow = BoxShadows.Parse("16 0 24 0 #2D8D7965 inset");
    private readonly LinearGradientBrush _paperLight = Gradient(true,
        (0, "#A0F2ECDF"), (.22, "#A0FFFBF2"), (.72, "#BBFFFDF8"), (1, "#88E9DECD"));
    private readonly LinearGradientBrush _leftGutter = Gradient(true, (0, "#00725B3E"), (1, "#25725B3E"));
    private readonly LinearGradientBrush _rightGutter = Gradient(true, (0, "#25725B3E"), (1, "#00725B3E"));
    private readonly LinearGradientBrush _topMask = Gradient(false, (0, "#FF000000"), (42d / 56, "#FF000000"), (1, "#00000000"));
    private readonly LinearGradientBrush _bottomMask = Gradient(false, (0, "#00000000"), (14d / 62, "#FF000000"), (1, "#FF000000"));
    private readonly LinearGradientBrush _spineAcrossMask = Gradient(true,
        (0, "#00000000"), (14d / 37, "#FF000000"), (23d / 37, "#FF000000"), (1, "#00000000"));
    private readonly LinearGradientBrush _spineAlongMask = Gradient(false,
        (0, "#00000000"), (14d / 1260, "#FF000000"), (1 - 14d / 1260, "#FF000000"), (1, "#00000000"));

    static SongScrollBookSurface() => AffectsRender<SongScrollBookSurface>(SpinePositionProperty, SceneHeightProperty);
    public SongScrollBookSurface() { IsHitTestVisible = false; UseLayoutRounding = false; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 所有 Bitmap 随这次视觉树挂载存活，Render 不打开文件或分配位图。
        if (_images.Count == 0)
        {
            foreach (var name in new[] { "page-paper", "book-top", "book-bottom", "book-left", "book-right", "book-spine" })
            {
                using var stream = AssetLoader.Open(new Uri(AssetRoot + name + ".png"));
                _images.Add(name, new Bitmap(stream));
            }
            var image = _images["page-paper"];
            _paper = new ImageBrush(image) { TileMode = TileMode.Tile,
                DestinationRect = new RelativeRect(new Rect(image.Size), RelativeUnit.Absolute) };
        }
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _paper = null;
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_paper == null) return;
        var extra = SceneHeight - 1440;
        var spine = SpinePosition;
        var pageHeight = Math.Max(0, 1308 + extra);
        var coverHeight = Math.Max(0, 1337 + extra);
        if (coverHeight > 0)
            context.DrawRectangle(_cover, _coverEdge, new Rect(312, 68, 2248, coverHeight), 20, 20, _coverShadow);
        if (pageHeight > 0)
        {
            DrawPage(context, new Rect(PaperLeft, 80, spine - PaperLeft, pageHeight), _leftShadow);
            DrawPage(context, new Rect(spine, 80, PaperRight - spine, pageHeight), _rightShadow);
            context.DrawRectangle(_leftGutter, null, new Rect(spine - 38, 80, 38, pageHeight));
            context.DrawRectangle(_rightGutter, null, new Rect(spine, 80, 38, pageHeight));
        }
        // 顶/底折口都是 128 设计 px，中心同源；不再整张横向拉伸或保留原中心折痕。
        DrawEdge(context, "book-top", 64, 56, 1068, 160, _topMask);
        DrawEdge(context, "book-bottom", 1352 + extra, 62, 1072, 360, _bottomMask);
        DrawImage(context, "book-left", new Rect(308, 120, 98, Math.Max(0, 1232 + extra)));
        DrawImage(context, "book-right", new Rect(2485, 120, 75, Math.Max(0, 1232 + extra)));
        var spineRect = new Rect(spine - 18.5, 106, 37, Math.Max(0, 1260 + extra));
        if (spineRect.Height > 0)
        {
            var fade = Math.Min(.5, 14 / spineRect.Height);
            _spineAlongMask.GradientStops[1].Offset = fade;
            _spineAlongMask.GradientStops[2].Offset = 1 - fade;
            using (context.PushOpacityMask(_spineAcrossMask, spineRect))
            using (context.PushOpacityMask(_spineAlongMask, spineRect))
                DrawImage(context, "book-spine", spineRect);
        }
    }

    private void DrawPage(DrawingContext context, Rect rect, BoxShadows shadow)
    {
        context.DrawRectangle(_paper, null, rect, 16, 16);
        context.DrawRectangle(_paperLight, null, rect, 16, 16, shadow);
    }

    private void DrawEdge(DrawingContext context, string name, double y, double height,
        double sourceFold, double rightCap, IBrush mask)
    {
        var image = _images[name];
        var fold = SpinePosition - 308;
        ReadOnlySpan<double> sourceCuts = stackalloc double[] { 0, 160, sourceFold - 64, sourceFold + 64, 2252 - rightCap, 2252 };
        ReadOnlySpan<double> targetCuts = stackalloc double[] { 0, 160, fold - 64, fold + 64, 2252 - rightCap, 2252 };
        using (context.PushOpacityMask(mask, new Rect(308, y, 2252, height)))
        {
            for (var i = 0; i < 5; i++)
            {
                // DrawImage 的源矩形是 IImage.Size 的 DIP，而非假定 PNG 无 DPI 元数据。
                var source = new Rect(sourceCuts[i] * image.Size.Width / 2252, 0,
                    (sourceCuts[i + 1] - sourceCuts[i]) * image.Size.Width / 2252, image.Size.Height);
                var target = new Rect(308 + targetCuts[i], y, targetCuts[i + 1] - targetCuts[i], height);
                context.DrawImage(image, source, target);
            }
        }
    }

    private void DrawImage(DrawingContext context, string name, Rect target)
    {
        if (target.Width > 0 && target.Height > 0)
        {
            var image = _images[name];
            context.DrawImage(image, new Rect(image.Size), target);
        }
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
    private static LinearGradientBrush Gradient(bool horizontal, params (double Offset, string Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(horizontal ? 1 : 0, horizontal ? 0 : 1, RelativeUnit.Relative) };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset));
        return brush;
    }
}