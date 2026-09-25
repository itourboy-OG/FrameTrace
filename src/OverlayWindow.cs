using System.Collections.Immutable;

using System.Windows;

using System.Windows.Controls;
using System.Windows.Documents;

using System.Windows.Input;

using System.Windows.Media;
using System.Windows.Media.Effects;



namespace Frameglass;



public sealed record MetricValue(string Id, string Label, string Text)
{
    public bool Available { get; init; } = true;
}

public sealed record OverlaySectionData(SectionKind Kind, string Name, ImmutableArray<MetricValue> Metrics);



public static class OverlayData

{

    public static ImmutableArray<OverlaySectionData> Build(ImmutableArray<SensorReading> sensors, FrameSummary frames)

    {

        string gpuName = sensors.FirstOrDefault(sensor => sensor.HardwareType.StartsWith("Gpu", StringComparison.Ordinal))?.Device ?? "GPU";

        string cpuName = sensors.FirstOrDefault(sensor => sensor.HardwareType == "Cpu")?.Device ?? "CPU";

        ImmutableArray<SensorReading> gpu = sensors.Where(sensor => sensor.Device == gpuName).ToImmutableArray();

        SensorReading? cpuTemperature = sensors.FirstOrDefault(sensor => sensor.HardwareType == "Cpu" && sensor.Kind == "Temperature" && sensor.Name is "Core (Tctl/Tdie)" or "CPU Package");

        SensorReading? cpuPower = sensors.FirstOrDefault(sensor => sensor.HardwareType == "Cpu" && sensor.Kind == "Power" && sensor.Name is "Package" or "CPU Package");

        return [new(SectionKind.Frames, "FRAMES", [new("app", "App FPS", frames.AppFps?.ToString("0") ?? "—"), new("display", "Display FPS", frames.DisplayFps?.ToString("0") ?? "—"), new("average", "Average FPS", frames.AverageFps?.ToString("0") ?? "—"), new("low", "1% low FPS", frames.LowFps?.ToString("0") ?? "—"), new("upscaler", "Upscaler", "Unsupported") { Available = false }, new("frametime", "Frame time", Readings.Format(frames.FrameTime, "ms")), new("generation", "Frame generation", frames.Generation) { Available = frames.Generation != "Unavailable" }]),

            new(SectionKind.Gpu, gpuName, [new("usage", "Usage", Readings.Sensor(gpu, "Gpu", "Load", "GPU Core")), new("temperature", "Temperature", Readings.Sensor(gpu, "Gpu", "Temperature", "GPU Core")), new("power", "Power draw", Readings.Sensor(gpu, "Gpu", "Power", "GPU Package")), new("clock", "Clock speed", Readings.Sensor(gpu, "Gpu", "Clock", "GPU Core")), new("vram", "VRAM used", Readings.Memory(gpu, "GPU Memory Used"))]),

            new(SectionKind.Cpu, cpuName, [new("usage", "Usage", Readings.Sensor(sensors, "Cpu", "Load", "CPU Total")), new("temperature", "Temperature", Readings.Format(cpuTemperature?.Value, "°C")), new("power", "Power draw", Readings.Format(cpuPower?.Value, "W"))]),

            new(SectionKind.Ram, "RAM", [new("used", "RAM used", Readings.Sensor(sensors, "Memory", "Data", "Memory Used"))])];

    }



    public static SectionStyle Move(SectionStyle section, Point topLeft, Size bounds, Size panel) => section with

    {

        X = Math.Clamp(topLeft.X / Math.Max(1, bounds.Width - panel.Width), 0, 1),

        Y = Math.Clamp(topLeft.Y / Math.Max(1, bounds.Height - panel.Height), 0, 1)

    };

}



/// <summary>Shared editor and overlay renderer. Retains text elements instead of rebuilding the visual tree for each reading.</summary>
public sealed partial class OverlayCanvas : Canvas
{
    private readonly Dictionary<string, Border> panels = new();
    private readonly Dictionary<string, TextBlock> titles = new();
    private readonly Dictionary<(string, string), TextBlock> values = new();
    private readonly Dictionary<string, FrameGraph> graphs = new();
    private readonly Dictionary<(string, string), ImmutableArray<FrameworkElement>> metricElements = new();
    private readonly Dictionary<(string, string), Run> flowValues = new();
    private Preferences preferences = Preferences.Initial;
    private ImmutableArray<OverlaySectionData> data = OverlayData.Build([], FrameMetrics.Summarize([], Environment.TickCount64));
    private Border? dragging;

    public event Action<ImmutableArray<SectionStyle>>? SectionsMoved;
    public event Action<string>? SectionSelected;

    public OverlayCanvas()
    {
        Width = 1920; Height = 1080;
        SizeChanged += (_, _) => PositionPanels();
        Apply(preferences);
    }
    public void Apply(Preferences value)
    {
        preferences = value; values.Clear(); titles.Clear(); graphs.Clear(); flowValues.Clear(); metricElements.Clear();
        foreach (string key in panels.Keys.Except(value.Sections.Select(style => style.Key)).ToArray())
        {
            Children.Remove(panels[key]); panels.Remove(key); selected.Remove(key);
        }
        foreach (SectionStyle style in value.Sections)
        {
            if (panels.ContainsKey(style.Key)) continue;
            Border panel = new() { Tag = style.Key, BorderThickness = new Thickness(1) };
            panels.Add(style.Key, panel); Children.Add(panel);
            if (editing) AttachPanelEditor(panel);
        }
        foreach (SectionStyle style in preferences.Sections)
        {
            Border panel = panels[style.Key];
            panel.Visibility = style.Visible && (style.ShowName || style.Graph || !style.Metrics.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            Color color = (Color)ColorConverter.ConvertFromString(style.NameColor);
            panel.BorderBrush = Brushes.Transparent;
            panel.CornerRadius = new CornerRadius(0);
            panel.LayoutTransform = new ScaleTransform(preferences.OverlayScale, preferences.OverlayScale);
            panel.Padding = new Thickness(style.Padding);
            panel.Background = new SolidColorBrush(Color.FromArgb((byte)(preferences.Opacity * 255), 12, 16, 22));
            StackPanel lines = new();
            TextBlock title = new() { Foreground = new SolidColorBrush(color), FontSize = style.NameSize, FontFamily = new FontFamily(preferences.Font), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6), Visibility = style.ShowName ? Visibility.Visible : Visibility.Collapsed };
            titles.Add(style.Key, title); lines.Children.Add(title);
            Grid metrics = new();
            bool across = style.Layout == MetricLayout.Tiles || (style.Layout == MetricLayout.Flow && style.Horizontal);
            if (style.Layout == MetricLayout.Table)
            {
                double labelWidth = Math.Clamp((style.LabelSize > 0 ? style.LabelSize : style.ValueSize) * 9, 180, 240);
                metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
                metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            for (int i = 0; i < style.Metrics.Length; i++)
            {
                string id = style.Metrics[i];
                MetricValue metric = data.Single(d => d.Kind == style.Kind).Metrics.Single(m => m.Id == id);
                string label = style.Labels.TryGetValue(id, out string? custom) ? custom : metric.Label;
                double size = id == "app" && style.HeroSize > 0 ? style.HeroSize : style.ValueSize;
                TextBlock text = new() { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.ValueColor)), FontSize = size, FontFamily = new FontFamily(preferences.Font), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                values.Add((style.Key, id), text);
                if (across) metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                else metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (style.Layout == MetricLayout.Table)
                {
                    TextBlock caption = new() { Text = label, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(color), FontFamily = new FontFamily(preferences.Font), FontSize = style.LabelSize > 0 ? style.LabelSize : style.ValueSize, Margin = new Thickness(0, 0, 12, 2), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetRow(caption, i); metrics.Children.Add(caption);
                    Grid.SetRow(text, i); Grid.SetColumn(text, 1); text.HorizontalAlignment = HorizontalAlignment.Right;
                    metrics.Children.Add(text);
                    metricElements.Add((style.Key, id), [caption, text]);
                }
                else if (style.Layout == MetricLayout.Tiles)
                {
                    StackPanel tile = new() { Margin = new Thickness(0, 0, i == style.Metrics.Length - 1 ? 0 : 22, 0), VerticalAlignment = VerticalAlignment.Bottom };
                    tile.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(color), FontFamily = new FontFamily(preferences.Font), FontSize = style.LabelSize > 0 ? style.LabelSize : 11, FontWeight = FontWeights.Bold });
                    tile.Children.Add(text); Grid.SetColumn(tile, i); metrics.Children.Add(tile);
                    metricElements.Add((style.Key, id), [tile]);
                }
                else
                {
                    Run reading = new();
                    text.Inlines.Add(new Run(string.IsNullOrEmpty(label) ? "" : label + "  ") { FontSize = style.LabelSize > 0 ? style.LabelSize : size, Foreground = new SolidColorBrush(color) });
                    text.Inlines.Add(reading); flowValues.Add((style.Key, id), reading);
                    text.Margin = across ? new Thickness(0, 0, i == style.Metrics.Length - 1 ? 0 : 14, 0) : new Thickness(0);
                    Grid.SetColumn(text, across ? i : 0); Grid.SetRow(text, across ? 0 : i); metrics.Children.Add(text);
                    metricElements.Add((style.Key, id), [text]);
                }
            }
            lines.Children.Add(metrics);
            StackPanel contents = new() { Orientation = style.GraphBelow ? Orientation.Vertical : Orientation.Horizontal };
            contents.Children.Add(lines);
            if (style.Graph)
            {
                FrameGraph graph = new() { Width = style.GraphWidth, Height = style.GraphHeight, LineBrush = new SolidColorBrush(color), Margin = style.GraphBelow ? new Thickness(0, 8, 0, 0) : new Thickness(18, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                graphs.Add(style.Key, graph); contents.Children.Add(graph);
            }
            if (style.TextShadow) contents.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 };
            panel.Child = contents;
        }
        UpdateData(data);
        UpdateSelectionOutlines();
    }
    public void UpdateData(ImmutableArray<OverlaySectionData> value)
    {
        data = value;
        if (dragging is not null) return;
        foreach (SectionStyle style in preferences.Sections)
        {
            OverlaySectionData section = data.Single(d => d.Kind == style.Kind);
            titles[style.Key].Text = string.IsNullOrWhiteSpace(style.Name) ? section.Name : style.Name;
            foreach (string id in style.Metrics)
            {
                MetricValue metric = section.Metrics.Single(m => m.Id == id);
                string label = style.Labels.TryGetValue(id, out string? custom) ? custom : metric.Label;
                foreach (FrameworkElement element in metricElements[(style.Key, id)])
                    element.Visibility = preferences.HideUnknownTechnology && !editing && !metric.Available ? Visibility.Collapsed : Visibility.Visible;
                if (style.Layout == MetricLayout.Flow) flowValues[(style.Key, id)].Text = metric.Text;
                else values[(style.Key, id)].Text = metric.Text;
            }
            panels[style.Key].Visibility = style.Visible && (style.ShowName || style.Graph || style.Metrics.Any(id => editing || !preferences.HideUnknownTechnology || section.Metrics.Single(metric => metric.Id == id).Available)) ? Visibility.Visible : Visibility.Collapsed;
            panels[style.Key].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        PositionPanels(); UpdateSelectionOutlines();
    }
    public void UpdateGraph(ImmutableArray<FramePoint> samples)
    {
        foreach (FrameGraph graph in graphs.Values) graph.SetSamples(samples);
    }
    private void PositionPanels()
    {
        foreach (SectionStyle section in preferences.Sections)
        {
            Border panel = panels[section.Key];
            SetLeft(panel, section.X * Math.Max(0, Width - panel.DesiredSize.Width));
            SetTop(panel, section.Y * Math.Max(0, Height - panel.DesiredSize.Height));
        }
    }
    public Rect VisibleBounds()
    {
        Rect result = Rect.Empty;
        foreach (Border panel in panels.Values.Where(p => p.Visibility == Visibility.Visible))
            result.Union(new Rect(GetLeft(panel), GetTop(panel), panel.DesiredSize.Width, panel.DesiredSize.Height));
        return result;
    }
    public Preferences ResizeOverlay(double scale)
    {
        Preferences next = Preferences.Validate(preferences with { OverlayScale = scale });
        Rect bounds = VisibleBounds();
        if (bounds.IsEmpty) return next;
        double ratio = scale / preferences.OverlayScale;
        if (bounds.Width * ratio > Width || bounds.Height * ratio > Height)
            throw new ArgumentException("This size would extend past the display. Move items closer together or choose a smaller overlay size.");
        Point origin = new(Math.Min(bounds.X, Width - bounds.Width * ratio), Math.Min(bounds.Y, Height - bounds.Height * ratio));
        return next with { Sections = preferences.Sections.Select(section =>
        {
            Border panel = panels[section.Key];
            if (panel.Visibility != Visibility.Visible) return section;
            Point point = new(origin.X + (GetLeft(panel) - bounds.X) * ratio, origin.Y + (GetTop(panel) - bounds.Y) * ratio);
            return OverlayData.Move(section, point, new Size(Width, Height), new Size(panel.DesiredSize.Width * ratio, panel.DesiredSize.Height * ratio));
        }).ToImmutableArray() };
    }
    public Preferences ArrangePreset(string name)
    {
        double y = 24, x = 24, rowHeight = 0;
        double leftWidth = Math.Max(panels[SectionKind.Gpu.ToString()].DesiredSize.Width, panels[SectionKind.Ram.ToString()].DesiredSize.Width);
        double topHeight = Math.Max(panels[SectionKind.Gpu.ToString()].DesiredSize.Height, panels[SectionKind.Cpu.ToString()].DesiredSize.Height);
        ImmutableArray<SectionStyle>.Builder sections = ImmutableArray.CreateBuilder<SectionStyle>();
        foreach (SectionStyle style in preferences.Sections)
        {
            Size size = panels[style.Key].DesiredSize;
            if (name == "Telemetry bar" && x > 24 && x + size.Width > Width - 24)
            {
                x = 24; y += rowHeight + 12; rowHeight = 0;
            }
            Point point = name == "Benchmark grid"
                ? new Point(style.Kind is SectionKind.Cpu or SectionKind.Frames ? 24 + leftWidth + 32 : 24, style.Kind is SectionKind.Frames or SectionKind.Ram ? 24 + topHeight + 18 : 24)
                : new Point(name == "Telemetry bar" ? x : 24, y);
            sections.Add(OverlayData.Move(style, point, new Size(Width, Height), size));
            if (!style.Visible) continue;
            if (name == "Telemetry bar") { x += size.Width + 28; rowHeight = Math.Max(rowHeight, size.Height); }
            else y += size.Height + (name == "Classic RTSS" ? 2 : 10);
        }
        return preferences with { Sections = sections.ToImmutable() };
    }
}

public static class LayoutPresets
{
    public static ImmutableArray<string> Names => ["Classic RTSS", "Hardware dossier", "Benchmark grid", "FPS spotlight", "Telemetry bar"];
    public static Preferences Create(Preferences existing, string name)
    {
        if (!Names.Contains(name)) throw new ArgumentException("Unknown layout preset.", nameof(name));
        ImmutableArray<SectionKind> order = name == "FPS spotlight"
            ? [SectionKind.Frames, SectionKind.Gpu, SectionKind.Cpu, SectionKind.Ram]
            : [SectionKind.Gpu, SectionKind.Cpu, SectionKind.Ram, SectionKind.Frames];
        return existing with
        {
            Opacity = 0,
            Font = name == "Classic RTSS" ? "Consolas" : "Segoe UI",
            Sections = order.Select(kind => Style(Preferences.Initial.Sections.Single(s => s.Kind == kind), name)).ToImmutableArray()
        };
    }
    private static SectionStyle Style(SectionStyle section, string preset)
    {
        ImmutableArray<string> metrics = (preset, section.Kind) switch
        {
            ("Classic RTSS", SectionKind.Frames) => ["app", "frametime"],
            ("FPS spotlight", SectionKind.Frames) => ["app", "average", "low"],
            ("Telemetry bar", SectionKind.Frames) => ["app", "low"],
            (_, SectionKind.Frames) => ["app", "average", "low", "frametime"],
            ("Hardware dossier", SectionKind.Gpu) => ["usage", "temperature", "clock", "power", "vram"],
            ("Benchmark grid", SectionKind.Gpu) => ["usage", "temperature", "power", "vram"],
            ("Classic RTSS", SectionKind.Gpu) => ["usage", "temperature", "clock", "power"],
            (_, SectionKind.Gpu) => ["usage", "temperature"],
            ("Hardware dossier", SectionKind.Cpu) => ["usage", "temperature", "power"],
            (_, SectionKind.Cpu) => ["usage", "temperature"],
            (_, SectionKind.Ram) => ["used"],
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
        string color = section.Kind switch
        {
            SectionKind.Frames => "#87F36C", SectionKind.Gpu => "#FFAA49",
            SectionKind.Cpu => "#69CDF6", SectionKind.Ram => "#DBB7FA",
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
        MetricLayout layout = preset switch
        {
            "Hardware dossier" or "Benchmark grid" => MetricLayout.Table,
            "Telemetry bar" => MetricLayout.Tiles,
            "FPS spotlight" when section.Kind == SectionKind.Frames => MetricLayout.Tiles,
            _ => MetricLayout.Flow
        };
        return section with
        {
            Name = preset == "Hardware dossier" && section.Kind is SectionKind.Gpu or SectionKind.Cpu ? "" : section.Kind == SectionKind.Frames ? "PERFORMANCE" : section.Kind.ToString().ToUpperInvariant(),
            NameColor = color, ValueColor = layout == MetricLayout.Flow ? color : "#F4F6F8",
            ValueSize = preset == "Telemetry bar" ? 22 : 18, NameSize = 16,
            HeroSize = section.Kind == SectionKind.Frames ? preset switch { "FPS spotlight" => 64, "Benchmark grid" => 36, _ => 0 } : 0,
            Visible = true, Padding = 2, TextShadow = true,
            ShowName = preset is "Hardware dossier" or "Benchmark grid" or "Telemetry bar",
            Horizontal = true, Layout = layout,
            Graph = section.Kind == SectionKind.Frames && preset is "Benchmark grid" or "FPS spotlight",
            GraphBelow = true, GraphWidth = preset == "FPS spotlight" ? 340 : 260, GraphHeight = 64,
            Metrics = metrics,
            Labels = metrics.ToImmutableDictionary(id => id, id => id switch
            {
                "app" => "FPS", "average" => "AVG", "low" => "1% LOW", "frametime" => layout == MetricLayout.Flow ? "" : "FRAME TIME",
                "usage" => layout == MetricLayout.Flow ? section.Kind == SectionKind.Gpu ? "GPU" : "CPU" : "LOAD",
                "temperature" => layout == MetricLayout.Flow ? "" : "TEMP", "power" => layout == MetricLayout.Flow ? "" : "POWER",
                "clock" => layout == MetricLayout.Flow ? "" : "CLOCK", "vram" => "VRAM", "used" => layout == MetricLayout.Flow ? "RAM" : "USED",
                _ => throw new ArgumentException("Preset metric has no label: " + id)
            })
        };
    }
}

/// <summary>Crops the desktop window to visible content instead of covering the whole monitor. No injection or game changes.</summary>
public sealed class OverlayWindow : Window
{
    public OverlayCanvas Surface { get; } = new();
    private DisplayInfo? display;
    private Rect? placed;
    public OverlayWindow(Preferences preferences)
    {
        Title = "Frame Trace overlay"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        IsHitTestVisible = false; Content = new Grid { ClipToBounds = true, Children = { Surface } };
        Surface.HorizontalAlignment = HorizontalAlignment.Left; Surface.VerticalAlignment = VerticalAlignment.Top;
        SourceInitialized += (_, _) => Desktop.MakeClickThrough(this);
        DpiChanged += (_, _) => { display = null; placed = null; };
        Surface.Apply(preferences);
    }
    public void Apply(Preferences preferences) => Surface.Apply(preferences);
    public void UpdateData(ImmutableArray<OverlaySectionData> data) { Surface.UpdateData(data); if (IsVisible) Place(); }
    public void ShowOnMonitor(Rect bounds)
    {
        if (display?.Bounds != bounds)
        {
            display = Desktop.Displays().Single(d => d.Bounds == bounds);
            Surface.Width = display.CanvasSize.Width; Surface.Height = display.CanvasSize.Height; placed = null;
        }
        if (Surface.VisibleBounds().IsEmpty) { Hide(); return; }
        if (!IsVisible) { Show(); placed = null; }
        Place();
    }
    private void Place()
    {
        if (display is null) return;
        Rect content = Surface.VisibleBounds();
        if (content.IsEmpty) { Hide(); return; }
        Surface.RenderTransform = new TranslateTransform(-content.X, -content.Y);
        Rect screen = new(display.Bounds.X + content.X * display.Scale, display.Bounds.Y + content.Y * display.Scale, Math.Ceiling(content.Width * display.Scale), Math.Ceiling(content.Height * display.Scale));
        if (placed != screen) { Desktop.PlaceOverlay(this, screen); placed = screen; }
    }
}

