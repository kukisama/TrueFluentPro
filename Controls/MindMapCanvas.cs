using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.Models;

namespace TrueFluentPro.Controls;

/// <summary>
/// 可视化思维导图控件 — 从 MindMapNode 树生成左→右放射状布局。
/// </summary>
public class MindMapCanvas : Canvas
{
    public static readonly StyledProperty<MindMapNode?> RootProperty =
        AvaloniaProperty.Register<MindMapCanvas, MindMapNode?>(nameof(Root));

    public MindMapNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    static MindMapCanvas()
    {
        RootProperty.Changed.AddClassHandler<MindMapCanvas>((c, _) => c.Rebuild());
    }

    private const double NodePadH = 14;
    private const double NodePadV = 7;
    private const double HGap = 48;
    private const double VGap = 8;
    private const double FontSize = 13;
    private const double RootFontSize = 15;
    private const double CornerRad = 10;
    private const int MaxTextLen = 35;

    private static readonly Color[] Palette =
    {
        Color.Parse("#6366F1"),
        Color.Parse("#8B5CF6"),
        Color.Parse("#06B6D4"),
        Color.Parse("#10B981"),
        Color.Parse("#F59E0B"),
    };

    private sealed class NodeLayout
    {
        public MindMapNode Data = null!;
        public double X, Y, W, H;
        public double TreeH;
        public int Depth;
        public List<NodeLayout> Kids = new();
    }

    private void Rebuild()
    {
        Children.Clear();
        Width = 0;
        Height = 0;

        if (Root == null || string.IsNullOrWhiteSpace(Root.Title)) return;

        var layout = Build(Root, 0);
        MeasureNodes(layout);
        var totalW = TreeWidth(layout) + 20;
        var totalH = layout.TreeH + 20;
        PlaceNodes(layout, 10, 10, layout.TreeH);

        // Add connections first (behind nodes)
        AddConnections(layout);

        // Add node visuals
        AddNodeVisuals(layout);

        Width = totalW;
        Height = totalH;
    }

    private static NodeLayout Build(MindMapNode n, int depth)
    {
        var nl = new NodeLayout { Data = n, Depth = depth };
        foreach (var c in n.Children)
            nl.Kids.Add(Build(c, depth + 1));
        return nl;
    }

    private void MeasureNodes(NodeLayout n)
    {
        var text = Truncate(n.Data.Title);
        var fontSize = n.Depth == 0 ? RootFontSize : FontSize;
        var padH = n.Depth == 0 ? NodePadH + 4 : NodePadH;
        var padV = n.Depth == 0 ? NodePadV + 3 : NodePadV;

        // Measure text with a temporary TextBlock
        var tb = new TextBlock { Text = text, FontSize = fontSize, FontWeight = n.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal };
        tb.Measure(Size.Infinity);
        n.W = tb.DesiredSize.Width + padH * 2;
        n.H = Math.Max(tb.DesiredSize.Height + padV * 2, 32);

        foreach (var k in n.Kids) MeasureNodes(k);

        n.TreeH = n.Kids.Count == 0
            ? n.H
            : Math.Max(n.H, n.Kids.Sum(k => k.TreeH) + (n.Kids.Count - 1) * VGap);
    }

    private static double TreeWidth(NodeLayout n)
    {
        if (n.Kids.Count == 0) return n.W;
        return n.W + HGap + n.Kids.Max(k => TreeWidth(k));
    }

    private void PlaceNodes(NodeLayout n, double x, double yTop, double span)
    {
        n.X = x;
        n.Y = yTop + (span - n.H) / 2;

        if (n.Kids.Count == 0) return;

        var cx = x + n.W + HGap;
        var totalKidsH = n.Kids.Sum(k => k.TreeH) + (n.Kids.Count - 1) * VGap;
        var cy = yTop + (span - totalKidsH) / 2;
        foreach (var k in n.Kids)
        {
            PlaceNodes(k, cx, cy, k.TreeH);
            cy += k.TreeH + VGap;
        }
    }

    private void AddConnections(NodeLayout n)
    {
        var ci = Math.Min(n.Depth, Palette.Length - 1);
        var color = Palette[ci];

        foreach (var k in n.Kids)
        {
            var sx = n.X + n.W;
            var sy = n.Y + n.H / 2;
            var ex = k.X;
            var ey = k.Y + k.H / 2;
            var mx = (sx + ex) / 2;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(sx, sy), false);
                ctx.CubicBezierTo(new Point(mx, sy), new Point(mx, ey), new Point(ex, ey));
            }

            var path = new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(color, 0.45),
                StrokeThickness = 1.8,
            };
            Children.Add(path);

            AddConnections(k);
        }
    }

    private void AddNodeVisuals(NodeLayout n)
    {
        var ci = Math.Min(n.Depth, Palette.Length - 1);
        var color = Palette[ci];
        var fontSize = n.Depth == 0 ? RootFontSize : FontSize;
        var fontWeight = n.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal;

        var border = new Border
        {
            Background = new SolidColorBrush(color, 0.12),
            BorderBrush = new SolidColorBrush(color, 0.8),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(CornerRad),
            Padding = new Thickness(n.Depth == 0 ? NodePadH + 4 : NodePadH, n.Depth == 0 ? NodePadV + 3 : NodePadV),
            Child = new TextBlock
            {
                Text = Truncate(n.Data.Title),
                FontSize = fontSize,
                FontWeight = fontWeight,
                Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        SetLeft(border, n.X);
        SetTop(border, n.Y);
        Children.Add(border);

        foreach (var k in n.Kids)
            AddNodeVisuals(k);
    }

    private static string Truncate(string s) =>
        s.Length > MaxTextLen ? s[..(MaxTextLen - 1)] + "\u2026" : s;
}
