using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Frameglass;

internal sealed class GamingBackground : FrameworkElement
{
    internal const double LoopSeconds = 32;
    private const int FramesPerSecond = 30;
    private static readonly Color Teal = Color.FromRgb(131, 239, 205);
    private static readonly Color Blue = Color.FromRgb(96, 151, 255);
    private static readonly Color Violet = Color.FromRgb(168, 135, 255);
    private static readonly Pen TealGlow = CreatePen(Teal, 7, 20);
    private static readonly Pen TealCore = CreatePen(Teal, 3, 8);
    private static readonly Pen TealEdge = CreatePen(Teal, 1.2, 25);
    private static readonly Pen BlueGlow = CreatePen(Blue, 6, 19);
    private static readonly Pen BlueCore = CreatePen(Blue, 2.5, 9);
    private static readonly Pen BlueEdge = CreatePen(Blue, 1.2, 24);
    private static readonly Pen VioletGlow = CreatePen(Violet, 5, 17);
    private static readonly Pen VioletEdge = CreatePen(Violet, 1.1, 20);
    private static readonly Geometry Spark = CreateSpark();
    private static readonly Brush TealSpark = CreateBrush(Teal, 190);
    private static readonly Brush BlueSpark = CreateBrush(Blue, 185);
    private static readonly Brush VioletSpark = CreateBrush(Violet, 175);
    private static readonly Brush TealSparkGlow = CreateBrush(Teal, 32);
    private static readonly Brush BlueSparkGlow = CreateBrush(Blue, 30);
    private static readonly Brush VioletSparkGlow = CreateBrush(Violet, 28);
    private static readonly Brush Backdrop = CreateBackdrop();
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render);
    private bool reducedMotion;
    private Window? owner;

    public GamingBackground()
    {
        IsHitTestVisible = false;
        timer.Interval = TimeSpan.FromMilliseconds(1000d / FramesPerSecond);
        timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this) ?? throw new InvalidOperationException("The gaming background needs a window host.");
            owner.StateChanged += WindowStateChanged;
            owner.IsVisibleChanged += WindowVisibilityChanged;
            RefreshAnimation();
        };
        Unloaded += (_, _) =>
        {
            timer.Stop(); clock.Stop();
            if (owner is not null) { owner.StateChanged -= WindowStateChanged; owner.IsVisibleChanged -= WindowVisibilityChanged; owner = null; }
        };
    }

    internal bool IsAnimating => timer.IsEnabled;
    internal double Phase => PhaseAt(clock.Elapsed.TotalSeconds);
    internal bool ReducedMotion => reducedMotion;

    internal void SetReducedMotion(bool enabled)
    {
        reducedMotion = enabled;
        RefreshAnimation();
        InvalidateVisual();
    }

    private void WindowStateChanged(object? sender, EventArgs e) => RefreshAnimation();
    private void WindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshAnimation();
    private void RefreshAnimation()
    {
        if (IsLoaded && !reducedMotion && owner is { IsVisible: true, WindowState: not WindowState.Minimized }) { clock.Start(); timer.Start(); }
        else { timer.Stop(); clock.Stop(); }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        context.DrawRectangle(Backdrop, null, new Rect(0, 0, width, height));
        double phase = (reducedMotion ? 0 : Phase) * Math.PI * 2;
        DrawWave(context, width, height, 0.27, 0.055, 1.8, phase, 0.0, TealGlow, TealCore, TealEdge);
        DrawWave(context, width, height, 0.53, 0.075, 2.35, phase * 2, 2.1, BlueGlow, BlueCore, BlueEdge);
        DrawWave(context, width, height, 0.79, 0.045, 1.55, phase * 3, 4.2, VioletGlow, null, VioletEdge);
        DrawSparks(context, width, height, phase);
    }

    internal static double PhaseAt(double elapsedSeconds)
    {
        double wrapped = elapsedSeconds % LoopSeconds;
        return (wrapped < 0 ? wrapped + LoopSeconds : wrapped) / LoopSeconds;
    }

    private static void DrawWave(DrawingContext context, double width, double height, double center, double amplitude, double cycles, double phase, double offset, Pen glow, Pen? core, Pen edge)
    {
        Geometry geometry = CreateWave(width, height * center, height * amplitude, cycles, phase + offset);
        context.DrawGeometry(null, glow, geometry);
        if (core is not null) context.DrawGeometry(null, core, geometry);
        context.DrawGeometry(null, edge, geometry);
    }

    private static void DrawSparks(DrawingContext context, double width, double height, double phase)
    {
        for (int index = 0; index < 18; index++)
        {
            int lane = index % 3;
            int speed = lane + 1;
            double seed = index * 0.6180339887498948 % 1;
            double progress = (seed + phase * speed) % 1;
            double edgeFade = Math.Clamp(Math.Min(progress, 1 - progress) * 16, 0, 1);
            double x = progress * width;
            (double center, double amplitude, double cycles, double phaseOffset) = lane switch
            {
                0 => (0.27, 0.055, 1.8, 0.0),
                1 => (0.53, 0.075, 2.35, 2.1),
                _ => (0.79, 0.045, 1.55, 4.2)
            };
            double wavePhase = phase * speed * Math.PI * 2 + phaseOffset;
            double y = WaveY(x, width, height * center, height * amplitude, cycles, wavePhase);
            double size = 0.78 + 0.28 * (0.5 + 0.5 * Math.Sin(wavePhase + seed * Math.PI * 2));
            Brush spark = lane switch { 0 => TealSpark, 1 => BlueSpark, _ => VioletSpark };
            Brush glow = lane switch { 0 => TealSparkGlow, 1 => BlueSparkGlow, _ => VioletSparkGlow };
            context.PushTransform(new TranslateTransform(x, y));
            context.PushOpacity(edgeFade * 0.45);
            context.PushTransform(new ScaleTransform(size * 1.7, size * 1.7));
            context.DrawGeometry(glow, null, Spark);
            context.Pop();
            context.PushOpacity(edgeFade * 0.78);
            context.PushTransform(new ScaleTransform(size, size));
            context.DrawGeometry(spark, null, Spark);
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }
    }

    private static Geometry CreateSpark()
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, -6), true, true);
            context.LineTo(new Point(1.1, -1.1), true, false);
            context.LineTo(new Point(6, 0), true, false);
            context.LineTo(new Point(1.1, 1.1), true, false);
            context.LineTo(new Point(0, 6), true, false);
            context.LineTo(new Point(-1.1, 1.1), true, false);
            context.LineTo(new Point(-6, 0), true, false);
            context.LineTo(new Point(-1.1, -1.1), true, false);
            context.Close();
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateWave(double width, double center, double amplitude, double cycles, double phase)
    {
        StreamGeometry geometry = new();
        double left = -width * 0.04;
        double right = width * 1.04;
        int sections = Math.Max(8, (int)Math.Ceiling(cycles * 12));
        double step = (right - left) / sections;
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(left, WaveY(left, width, center, amplitude, cycles, phase)), false, false);
            for (int section = 0; section < sections; section++)
            {
                double x0 = left + step * section;
                double x1 = x0 + step;
                double y0 = WaveY(x0, width, center, amplitude, cycles, phase);
                double y1 = WaveY(x1, width, center, amplitude, cycles, phase);
                double slope0 = WaveSlope(x0, width, amplitude, cycles, phase);
                double slope1 = WaveSlope(x1, width, amplitude, cycles, phase);
                context.BezierTo(
                    new Point(x0 + step / 3, y0 + slope0 * step / 3),
                    new Point(x1 - step / 3, y1 - slope1 * step / 3),
                    new Point(x1, y1), true, false);
            }
        }
        return geometry;
    }

    private static double WaveY(double x, double width, double center, double amplitude, double cycles, double phase) =>
        center + Math.Sin(x / width * cycles * Math.PI * 2 + phase) * amplitude;

    private static double WaveSlope(double x, double width, double amplitude, double cycles, double phase) =>
        amplitude * cycles * Math.PI * 2 / width * Math.Cos(x / width * cycles * Math.PI * 2 + phase);

    private static Pen CreatePen(Color color, double thickness, byte opacity)
    {
        Pen pen = new(CreateBrush(color, opacity), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateBrush(Color color, byte opacity)
    {
        SolidColorBrush brush = new(Color.FromArgb(opacity, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateBackdrop()
    {
        LinearGradientBrush brush = new(new GradientStopCollection
        {
            new(Color.FromRgb(13, 19, 28), 0),
            new(Color.FromRgb(16, 19, 24), 0.52),
            new(Color.FromRgb(14, 23, 31), 1)
        }, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}
