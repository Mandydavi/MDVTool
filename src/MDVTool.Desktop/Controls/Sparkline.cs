using System;
using System.Windows;
using System.Windows.Media;

namespace MDVTool.Controls;

/// <summary>实时速度曲线（纯 OnRender 绘制，无第三方依赖）</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Points
    {
        get => (double[]?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var pts = Points;
        var baselinePen = new Pen(new SolidColorBrush(Color.FromArgb(28, 27, 42, 65)), 1);
        baselinePen.Freeze();
        dc.DrawLine(baselinePen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        if (pts == null || pts.Length < 2) return;

        double max = 1;
        foreach (var p in pts) if (p > max) max = p;

        int n = pts.Length;
        double stepX = w / Math.Max(1, n - 1);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < n; i++)
            {
                double y = h - Math.Clamp(pts[i] / max, 0, 1) * (h - 4);
                ctx.LineTo(new Point(i * stepX, y), true, false);
            }
            ctx.LineTo(new Point(w, h), true, false);
        }
        geo.Freeze();

        if (FillBrush != null) dc.DrawGeometry(FillBrush, null, geo);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(0, h - Math.Clamp(pts[0] / max, 0, 1) * (h - 4)), false, false);
            for (int i = 1; i < n; i++)
            {
                double y = h - Math.Clamp(pts[i] / max, 0, 1) * (h - 4);
                ctx.LineTo(new Point(i * stepX, y), true, true);
            }
        }
        line.Freeze();

        var pen = new Pen(LineBrush, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
