using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RamTrace.Core;

namespace RamTrace;

public sealed class MemoryChart : FrameworkElement
{
    private List<Core.Point> points = new();
    private long from, to;
    private double max = 1073741824;
    private int interval;
    private double hover = -1;
    private bool percent;
    public MemoryChart()
    {
        MouseMove += (_, e) => { hover = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { hover = -1; InvalidateVisual(); };
        ClipToBounds = true;
    }
    public void SetData(List<Core.Point> data, long start, long end, long total, int intervalMs, bool isPercent = false)
    {
        points = data; from = start; to = Math.Max(start + 1000, end); interval = intervalMs;
        percent = isPercent;
        max = isPercent ? 10000 : total > 0 ? total : Math.Max(1048576 * 32, points.Count > 0 ? points.Max(p => p.Peak) * 1.2 : 1073741824);
        InvalidateVisual();
    }
    private string Value(double n) => percent ? (n / 100).ToString("0.0") + "%" : Format.Bytes(n);
    private Brush Brush(string name) => TryFindResource(name) as Brush ?? Brushes.Gray;
    private void Text(DrawingContext dc, string text, double x, double y, string brush = "Muted", double size = 10)
    {
        var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brush(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(t, new System.Windows.Point(x, y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double left = 59, top = 10, w = Math.Max(1, ActualWidth - left - 13), h = Math.Max(1, ActualHeight - 41);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var grid = new Pen(Brush("Stroke"), 1);
        for (int i = 0; i <= 3; i++)
        {
            var y = top + h * i / 3;
            dc.DrawLine(grid, new System.Windows.Point(left, y), new System.Windows.Point(left + w, y));
            Text(dc, Value(max * (3 - i) / 3), 0, y - 6);
        }
        for (int i = 0; i <= 4; i++)
        {
            var time = from + (to - from) * i / 4;
            Text(dc, Format.Local(time).ToString(to - from > 2 * 86400000L ? "dd MMM" : "HH:mm"), left + w * i / 4 - (i == 4 ? 27 : 0), top + h + 10);
        }
        if (points.Count == 0) { Text(dc, "No samples in this range", left + Math.Max(10, w / 2 - 76), top + h / 2 - 8, "Muted", 12); return; }
        double X(Core.Point p) => left + (p.Time - from) * w / (to - from);
        double Y(double b) => top + h * (1 - Math.Min(1, b / max));
        var line = new Pen(Brush("Accent"), 2);
        var peak = new Pen(Brush("Accent"), 1) { DashStyle = DashStyles.Dash };
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            // Avoid drawing across sleep/pause gaps; account for query buckets and older rollups.
            long maxGap = Math.Max(Math.Max(interval * 3L, a.Duration * 2L), (to - from) / 360 * 3);
            if (b.Time - a.Time > maxGap) continue;
            var area = new StreamGeometry();
            using (var c = area.Open()) { c.BeginFigure(new System.Windows.Point(X(a), top + h), true, true); c.LineTo(new System.Windows.Point(X(a), Y(a.Value)), true, false); c.LineTo(new System.Windows.Point(X(b), Y(b.Value)), true, false); c.LineTo(new System.Windows.Point(X(b), top + h), true, false); }
            dc.PushOpacity(.10); dc.DrawGeometry(Brush("Accent"), null, area); dc.Pop();
            dc.DrawLine(line, new System.Windows.Point(X(a), Y(a.Value)), new System.Windows.Point(X(b), Y(b.Value)));
            if (a.Peak != a.Value || b.Peak != b.Value) dc.DrawLine(peak, new System.Windows.Point(X(a), Y(a.Peak)), new System.Windows.Point(X(b), Y(b.Peak)));
        }
        if (points.Count < 3) foreach (var p in points) dc.DrawEllipse(Brush("Accent"), null, new System.Windows.Point(X(p), Y(p.Value)), 3.5, 3.5);
        if (hover >= left && hover <= left + w)
        {
            var point = points.MinBy(p => Math.Abs(X(p) - hover))!;
            var x = X(point);
            if (Math.Abs(x - hover) > Math.Max(10, w * Math.Max(interval * 3L, point.Duration * 2L) / (to - from))) return;
            dc.DrawLine(new Pen(Brush("Muted"), 1) { DashStyle = DashStyles.Dot }, new System.Windows.Point(x, top), new System.Windows.Point(x, top + h));
            dc.DrawEllipse(Brush("Accent"), new Pen(Brush("Surface"), 2), new System.Windows.Point(x, Y(point.Value)), 4, 4);
            var bx = Math.Clamp(x - 110, left, Math.Max(left, ActualWidth - 225));
            dc.DrawRoundedRectangle(Brush("Surface"), new Pen(Brush("Stroke"), 1), new Rect(bx, top + 4, 216, 62), 6, 6);
            Text(dc, Format.Local(point.Time).ToString("dd MMM yyyy · HH:mm:ss"), bx + 10, top + 11, "Ink", 11);
            Text(dc, "Average " + Value(point.Value) + "   Peak " + Value(point.Peak), bx + 10, top + 34, "Accent", 11);
        }
    }
}
