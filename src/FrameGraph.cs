using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Frameglass;

/// <summary>Scrolls actual QPC-timestamped samples with a two-second ETW delivery delay; never synthesizes frames.</summary>
public sealed class FrameGraph : FrameworkElement
{
    private ImmutableArray<FramePoint> samples = [];
    private long lastPaint;
    public Brush LineBrush { get; set; } = Brushes.Aquamarine;
    public FrameGraph()
    {
        ClipToBounds = true;
        Loaded += (_, _) => CompositionTarget.Rendering += Tick;
        Unloaded += (_, _) => CompositionTarget.Rendering -= Tick;
    }
    public void SetSamples(ImmutableArray<FramePoint> value) => samples = value;
    private void Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || Environment.TickCount64 - lastPaint < 32) return;
        lastPaint = Environment.TickCount64; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double end = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency - 2000;
        FramePoint[] visible = samples.Where(p => p.At >= end - 10000 && p.At <= end).ToArray();
        double ceiling = Math.Max(16.7, visible.Select(p => p.Milliseconds).DefaultIfEmpty(0).Max());
        double scale = 16.7 * Math.Pow(2, Math.Ceiling(Math.Log2(ceiling / 16.7)));
        double height = Math.Max(1, ActualHeight - 20);
        Pen guide = new(new SolidColorBrush(Color.FromArgb(65, 150, 170, 190)), 1);
        drawing.DrawLine(guide, new Point(0, height), new Point(ActualWidth, height));
        drawing.DrawLine(guide, new Point(0, height / 2), new Point(ActualWidth, height / 2));
        StreamGeometry path = new();
        using (StreamGeometryContext context = path.Open())
        {
            FramePoint? previous = null;
            foreach (FramePoint sample in visible)
            {
                Point point = new((sample.At - end + 10000) / 10000 * ActualWidth, height - sample.Milliseconds / scale * height);
                if (previous is null || sample.At - previous.At > Math.Max(250, previous.Milliseconds * 4)) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
                previous = sample;
            }
        }
        path.Freeze(); drawing.DrawGeometry(null, new Pen(LineBrush, 1.25), path);
        string caption = visible.Length == 0 ? "Waiting for samples" : $"10 s   ·   0–{scale:0.#} ms";
        drawing.DrawText(new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.LightSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(0, height + 3));
    }
}
