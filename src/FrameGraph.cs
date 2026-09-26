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
    private static readonly Pen GuidePen = CreateGuidePen();
    private static readonly Typeface CaptionTypeface = new("Segoe UI");
    private Pen? linePen;
    private FormattedText? captionText;
    private string caption = "";
    private double captionDpi;
    private Window? owner;
    private bool rendering;
    internal bool IsAnimating => rendering;
    public Brush LineBrush { get; set; } = Brushes.Aquamarine;
    public FrameGraph()
    {
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this);
            if (owner is not null) owner.StateChanged += WindowStateChanged;
            RefreshRendering();
        };
        IsVisibleChanged += (_, _) => RefreshRendering();
        Unloaded += (_, _) =>
        {
            if (owner is not null) owner.StateChanged -= WindowStateChanged;
            owner = null;
            if (rendering) { CompositionTarget.Rendering -= Tick; rendering = false; }
        };
    }
    public void SetSamples(ImmutableArray<FramePoint> value)
    {
        bool emptyChanged = samples.IsEmpty != value.IsEmpty;
        samples = value; RefreshRendering();
        if (emptyChanged) InvalidateVisual();
    }
    private void WindowStateChanged(object? sender, EventArgs e) => RefreshRendering();
    private void RefreshRendering()
    {
        bool next = IsLoaded && IsVisible && !samples.IsEmpty && owner?.WindowState != WindowState.Minimized;
        if (next == rendering) return;
        if (next) CompositionTarget.Rendering += Tick; else CompositionTarget.Rendering -= Tick;
        rendering = next;
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || Environment.TickCount64 - lastPaint < 32) return;
        lastPaint = Environment.TickCount64; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double end = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency - 2000;
        double ceiling = 16.7;
        bool hasSamples = false;
        foreach (FramePoint sample in samples)
            if (sample.At >= end - 10000 && sample.At <= end) { ceiling = Math.Max(ceiling, sample.Milliseconds); hasSamples = true; }
        double scale = 16.7 * Math.Pow(2, Math.Ceiling(Math.Log2(ceiling / 16.7)));
        double height = Math.Max(1, ActualHeight - 20);
        drawing.DrawLine(GuidePen, new Point(0, height), new Point(ActualWidth, height));
        drawing.DrawLine(GuidePen, new Point(0, height / 2), new Point(ActualWidth, height / 2));
        StreamGeometry path = new();
        using (StreamGeometryContext context = path.Open())
        {
            FramePoint? previous = null;
            foreach (FramePoint sample in samples)
            {
                if (sample.At < end - 10000 || sample.At > end) continue;
                Point point = new((sample.At - end + 10000) / 10000 * ActualWidth, height - sample.Milliseconds / scale * height);
                if (previous is null || sample.At - previous.At > Math.Max(250, previous.Milliseconds * 4)) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
                previous = sample;
            }
        }
        if (linePen is null || linePen.Brush != LineBrush) linePen = new Pen(LineBrush, 1.25);
        path.Freeze(); drawing.DrawGeometry(null, linePen, path);
        string nextCaption = hasSamples ? $"10 s   ·   0–{scale:0.#} ms" : "Waiting for samples";
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (captionText is null || caption != nextCaption || captionDpi != dpi)
        {
            caption = nextCaption; captionDpi = dpi;
            captionText = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CaptionTypeface, 10, Brushes.LightSlateGray, dpi);
        }
        drawing.DrawText(captionText, new Point(0, height + 3));
    }
    private static Pen CreateGuidePen()
    {
        Pen pen = new(new SolidColorBrush(Color.FromArgb(65, 150, 170, 190)), 1);
        pen.Freeze(); return pen;
    }
}
