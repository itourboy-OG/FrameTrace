using System.Collections.Immutable;

using System.Windows;

using System.Windows.Controls;
using System.Windows.Documents;

using System.Windows.Input;

using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Automation;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;



namespace FrameTrace;



public sealed record MetricValue(string Id, string Label, string Text)
{
    public bool Available { get; init; } = true;
    public double? Percent { get; init; }
}

public sealed record OverlaySectionData(SectionKind Kind, string Name, ImmutableArray<MetricValue> Metrics)
{
    public double? FanRpm { get; init; }
    public double? Temperature { get; init; }
}



public static class OverlayData

{

    public static ImmutableArray<OverlaySectionData> Build(ImmutableArray<SensorReading> sensors, FrameSummary frames)

    {

        string gpuName = sensors.FirstOrDefault(sensor => sensor.HardwareType.StartsWith("Gpu", StringComparison.Ordinal))?.Device ?? "GPU";

        string cpuName = sensors.FirstOrDefault(sensor => sensor.HardwareType == "Cpu")?.Device ?? "CPU";

        ImmutableArray<SensorReading> gpu = sensors.Where(sensor => sensor.Device == gpuName).ToImmutableArray();

        SensorReading? cpuTemperature = sensors.FirstOrDefault(sensor => sensor.HardwareType == "Cpu" && sensor.Kind == "Temperature" && sensor.Name is "Core (Tctl/Tdie)" or "CPU Package");

        SensorReading? cpuPower = sensors.FirstOrDefault(sensor => sensor.HardwareType == "Cpu" && sensor.Kind == "Power" && sensor.Name is "Package" or "CPU Package");
        OverlaySectionData gpuSection = new(SectionKind.Gpu, gpuName, [Usage(gpu, "Gpu", "GPU Core"), new("temperature", "Temperature", Readings.Sensor(gpu, "Gpu", "Temperature", "GPU Core")), new("power", "Power draw", Readings.Sensor(gpu, "Gpu", "Power", "GPU Package")), new("clock", "Clock speed", Readings.Sensor(gpu, "Gpu", "Clock", "GPU Core")), new("vram", "VRAM used", Readings.Memory(gpu, "GPU Memory Used"))]);
        gpuSection = gpuSection with { FanRpm = Readings.Find(gpu, "Gpu", "Fan", "GPU Fan")?.Value, Temperature = Readings.Find(gpu, "Gpu", "Temperature", "GPU Core")?.Value };
        MetricValue ramUsed = new("used", "RAM used", Readings.Sensor(sensors, "Memory", "Data", "Memory Used"))
        {
            Percent = sensors.FirstOrDefault(sensor => sensor.Device == "Total Memory" && sensor.HardwareType == "Memory" && sensor.Kind == "Load" && sensor.Name == "Memory")?.Value
        };

        OverlaySectionData cpuSection = new(SectionKind.Cpu, cpuName, [Usage(sensors, "Cpu", "CPU Total"), new("temperature", "Temperature", Readings.Format(cpuTemperature?.Value, "°C")), new("power", "Power draw", Readings.Format(cpuPower?.Value, "W"))]);
        cpuSection = cpuSection with { Temperature = cpuTemperature?.Value };
        return [new(SectionKind.Frames, "FRAMES", [new("app", "App FPS", frames.AppFps?.ToString("0") ?? "—"), new("display", "Display FPS", frames.DisplayFps?.ToString("0") ?? "—"), new("average", "Average FPS", frames.AverageFps?.ToString("0") ?? "—"), new("low", "1% low FPS", frames.LowFps?.ToString("0") ?? "—"), new("upscaler", "Upscaler", frames.Upscaler.Label) { Available = frames.Upscaler.HasEvidence }, new("frametime", "Frame time", Readings.Format(frames.FrameTime, "ms")), new("generation", "Frame generation", frames.Generation) { Available = frames.Generation != "Unavailable" }]),

            gpuSection,

            cpuSection,

            new(SectionKind.Ram, "RAM", [ramUsed])
            , new(SectionKind.Game, "GAME", [new("game", "Game", "Waiting for game"), new("api", "Present API", "—")])
            ];

    }

    private static MetricValue Usage(ImmutableArray<SensorReading> sensors, string hardware, string name)
    {
        MetricValue metric = new("usage", "Usage", Readings.Sensor(sensors, hardware, "Load", name));
        return metric with { Percent = Readings.Find(sensors, hardware, "Load", name)?.Value };
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
    private readonly Dictionary<string, (Border Track, Border Fill)> usageBars = new();
    private readonly Dictionary<string, (System.Windows.Shapes.Path Arc, TextBlock Label)> usageGauges = new();
    private readonly Dictionary<string, (FrameworkElement Widget, RotateTransform Spin, TextBlock Label)> fanWidgets = new();
    private readonly Dictionary<string, double?> fanRpms = new();
    private readonly Dictionary<string, double> animatedRpms = new();
    private readonly Dictionary<string, Polyline> usageTrends = new();
    private readonly Dictionary<string, ImmutableArray<(long At, double Percent)>> usageSamples = new();
    private readonly Dictionary<string, (Image Image, BitmapSource[] Frames, ArtworkAnimationSource Source, double Minimum, double Maximum, int Index, long UpdatedAt)> artworkAnimations = new();
    private readonly Dictionary<string, (Border Fill, double Width, ArtworkBarSource Source, double Minimum, double Maximum)> artworkBars = new();
    private readonly Dictionary<string, TextBlock> rtssTexts = new();
    private readonly Dictionary<string, Image> gameIcons = new();
    private string gameName = "Waiting for game";
    private string presentApi = "—";
    private ImageSource? gameIconSource;
    private readonly DispatcherTimer artworkTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Window? fanOwner;
    private readonly Dictionary<(string, string), ImmutableArray<FrameworkElement>> metricElements = new();
    private readonly Dictionary<(string, string), Run> flowValues = new();
    private Preferences preferences = Preferences.Initial;
    private ImmutableArray<OverlaySectionData> data = OverlayData.Build([], FrameMetrics.Summarize([], Environment.TickCount64));
    private Border? dragging;

    public event Action<ImmutableArray<SectionStyle>>? SectionsMoved;
    public event Action? SelectionMoveStarted;
    public event Action? SelectionMoveEnded;
    public event Action<string>? SectionSelected;

    public OverlayCanvas()
    {
        Width = 1920; Height = 1080;
        SizeChanged += (_, _) => PositionPanels();
        artworkTimer.Tick += (_, _) => AdvanceArtworkAnimations();
        Loaded += (_, _) =>
        {
            fanOwner = Window.GetWindow(this);
            if (fanOwner is not null) fanOwner.StateChanged += FanWindowStateChanged;
            RefreshFanAnimations();
            RefreshArtworkAnimations();
        };
        IsVisibleChanged += (_, _) => { RefreshFanAnimations(); RefreshArtworkAnimations(); };
        Unloaded += (_, _) =>
        {
            if (fanOwner is not null) fanOwner.StateChanged -= FanWindowStateChanged;
            fanOwner = null;
            RefreshFanAnimations();
            RefreshArtworkAnimations();
        };
        Apply(preferences);
    }
    public void Apply(Preferences value)
    {
        preferences = value; values.Clear(); titles.Clear(); graphs.Clear(); flowValues.Clear(); metricElements.Clear();
        foreach ((FrameworkElement Widget, RotateTransform Spin, TextBlock Label) fan in fanWidgets.Values)
            fan.Spin.BeginAnimation(RotateTransform.AngleProperty, null);
        usageBars.Clear();
        usageGauges.Clear();
        fanWidgets.Clear(); fanRpms.Clear(); animatedRpms.Clear();
        usageTrends.Clear();
        artworkTimer.Stop(); artworkAnimations.Clear(); artworkBars.Clear(); rtssTexts.Clear();
        gameIcons.Clear();
        foreach (string key in usageSamples.Keys.Except(value.Sections.Select(style => style.Key)).ToArray()) usageSamples.Remove(key);
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
        for (int layer = 0; layer < preferences.Sections.Length; layer++)
        {
            SectionStyle style = preferences.Sections[layer];
            Border panel = panels[style.Key];
            SetZIndex(panel, layer);
            if (style.Kind == SectionKind.Artwork)
            {
                panel.Visibility = style.Visible && (style.ImageData.Length > 0 || !style.AnimationFrames.IsEmpty || style.ArtworkFill || style.ShowName || style.BarSource != ArtworkBarSource.None) ? Visibility.Visible : Visibility.Collapsed;
                panel.Background = Brushes.Transparent;
                panel.BorderBrush = Brushes.Transparent;
                panel.BorderThickness = new Thickness(0);
                panel.Padding = new Thickness(0);
                panel.Width = double.NaN; panel.Height = double.NaN;
                panel.Opacity = style.ArtworkOpacity;
                panel.CornerRadius = new CornerRadius(style.ArtworkRadius);
                panel.LayoutTransform = new ScaleTransform(preferences.OverlayScale, preferences.OverlayScale);
                if (style.BarSource != ArtworkBarSource.None)
                {
                    Color barColor = (Color)ColorConverter.ConvertFromString(style.NameColor);
                    Grid track = new() { Width = style.ImageWidth, Height = style.ImageHeight, Background = new SolidColorBrush(Color.FromArgb(60, barColor.R, barColor.G, barColor.B)), ClipToBounds = true };
                    Border fill = new() { Width = 0, Height = style.ImageHeight, Background = new SolidColorBrush(barColor), HorizontalAlignment = HorizontalAlignment.Left };
                    AutomationProperties.SetAutomationId(fill, "RtssBarFill_" + style.Key);
                    track.Children.Add(fill); panel.Child = track;
                    artworkBars.Add(style.Key, (fill, style.ImageWidth, style.BarSource, style.BarMinimum, style.BarMaximum));
                }
                else if (!style.AnimationFrames.IsEmpty)
                {
                    BitmapSource[] frames = style.AnimationFrames.Select(DecodePng).ToArray();
                    Image image = new() { Source = frames[0], Width = style.ImageWidth, Height = style.ImageHeight, Stretch = Stretch.Fill };
                    panel.Child = image;
                    artworkAnimations.Add(style.Key, (image, frames, style.AnimationSource, style.AnimationMinimum, style.AnimationMaximum, 0, Environment.TickCount64));
                }
                else if (style.ImageData.Length > 0)
                {
                    panel.Child = new Image { Source = DecodePng(style.ImageData), Width = style.ImageWidth, Height = style.ImageHeight, Stretch = Stretch.Fill };
                }
                else if (style.ArtworkFill)
                {
                    panel.Width = style.ImageWidth; panel.Height = style.ImageHeight;
                    panel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.NameColor));
                    panel.Child = null;
                }
                else if (style.RtssTextSource != RtssTextSource.None)
                {
                    TextBlock reading = new() { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.NameColor)), FontFamily = new FontFamily(style.RtssFontFace.Length == 0 ? preferences.Font : style.RtssFontFace), FontSize = style.NameSize, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = style.ImageWidth };
                    AutomationProperties.SetAutomationId(reading, "RtssText_" + style.Key);
                    panel.Child = reading;
                    rtssTexts.Add(style.Key, reading);
                }
                else
                    panel.Child = new TextBlock { Text = style.Name, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.NameColor)), FontFamily = new FontFamily(preferences.Font), FontSize = style.NameSize, FontWeight = FontWeights.Bold };
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                continue;
            }
            bool hasContent = style.ShowName || style.Graph || !style.Metrics.IsEmpty;
            hasContent |= style.UsageBar || style.UsageGauge || style.FanWidget;
            panel.Visibility = style.Visible && hasContent ? Visibility.Visible : Visibility.Collapsed;
            Color color = (Color)ColorConverter.ConvertFromString(style.NameColor);
            panel.BorderBrush = Brushes.Transparent;
            panel.CornerRadius = new CornerRadius(0);
            panel.Width = double.NaN;
            panel.LayoutTransform = new ScaleTransform(preferences.OverlayScale, preferences.OverlayScale);
            panel.Padding = new Thickness(style.Padding);
            panel.Background = style.GraphTransparent ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb((byte)(preferences.Opacity * 255), 12, 16, 22));
            if (style.ThemeCard)
            {
                panel.Width = Math.Max(style.UsageGauge ? 390 : 340, Math.Max(style.Graph ? style.GraphWidth + 34 : 0, style.UsageBar ? style.UsageBarWidth + 34 : 0));
                panel.BorderBrush = new SolidColorBrush(Color.FromArgb(170, color.R, color.G, color.B));
                panel.BorderThickness = new Thickness(1);
                panel.CornerRadius = new CornerRadius(12);
                panel.Padding = new Thickness(14);
                byte alpha = (byte)(preferences.Opacity * 255);
                panel.Background = new LinearGradientBrush(Color.FromArgb(alpha, 20, 30, 43), Color.FromArgb(alpha, 7, 12, 22), 90);
            }
            StackPanel lines = new();
            if (style.Kind == SectionKind.Game)
            {
                Image icon = new() { Source = gameIconSource, Width = 26, Height = 26, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0), Visibility = gameIconSource is null ? Visibility.Collapsed : Visibility.Visible };
                gameIcons.Add(style.Key, icon);
            }
            TextBlock title = new() { Foreground = new SolidColorBrush(color), FontSize = style.NameSize, FontFamily = new FontFamily(preferences.Font), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6), Visibility = style.ShowName ? Visibility.Visible : Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
            titles.Add(style.Key, title);
            if (style.ThemeCard)
            {
                Grid header = new() { Margin = new Thickness(0, 0, 0, 10) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Border badge = new() { Width = 38, Height = 32, Background = new SolidColorBrush(Color.FromArgb(45, color.R, color.G, color.B)), BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = style.Kind switch { SectionKind.Frames => "FPS", SectionKind.Gpu => "GPU", SectionKind.Cpu => "CPU", SectionKind.Game => "APP", _ => "RAM" }, Foreground = new SolidColorBrush(color), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                header.Children.Add(badge);
                title.VerticalAlignment = VerticalAlignment.Center;
                title.Margin = new Thickness(0);
                Grid.SetColumn(title, 1); header.Children.Add(title);
                lines.Children.Add(header);
                lines.Children.Add(new Border { Height = 2, Background = new SolidColorBrush(color), Opacity = 0.9, Margin = new Thickness(0, 0, 0, 10) });
                if ((style.Kind is SectionKind.Gpu or SectionKind.Cpu) && (!style.Graph || style.GraphSource == ArtworkGraphSource.FrameTime))
                    lines.Children.Add(CreateUsageTrend(style, color));
            }
            else
                lines.Children.Add(title);
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
                double size = style.HeroSize > 0 && (id == "app"
                    || style.ThemeCard && id is "usage" or "used"
                    ) ? style.HeroSize : style.ValueSize;
                TextBlock text = new() { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.ValueColor)), FontSize = size, FontFamily = new FontFamily(preferences.Font), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                values.Add((style.Key, id), text);
                if (across) metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                else metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (style.Layout == MetricLayout.Table)
                {
                    TextBlock caption = new() { Text = label, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(color), FontFamily = new FontFamily(preferences.Font), FontSize = style.LabelSize > 0 ? style.LabelSize : style.ValueSize, Margin = new Thickness(0, 0, 12, 2), VerticalAlignment = VerticalAlignment.Center };
                    FrameworkElement captionElement = caption;
                    if (style.ThemeCard)
                    {
                        Grid captionRow = new() { Margin = new Thickness(0, 0, 12, 2) };
                        captionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                        captionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        caption.Margin = new Thickness(0);
                        captionRow.Children.Add(MetricIcon(id, color));
                        Grid.SetColumn(caption, 1); captionRow.Children.Add(caption);
                        captionElement = captionRow;
                    }
                    Grid.SetRow(captionElement, i); metrics.Children.Add(captionElement);
                    Grid.SetRow(text, i); Grid.SetColumn(text, 1); text.HorizontalAlignment = HorizontalAlignment.Right;
                    metrics.Children.Add(text);
                    metricElements.Add((style.Key, id), [captionElement, text]);
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
            if (style.Kind == SectionKind.Game)
            {
                StackPanel gameRow = new() { Orientation = Orientation.Horizontal };
                gameRow.Children.Add(gameIcons[style.Key]); gameRow.Children.Add(metrics);
                lines.Children.Add(gameRow);
            }
            else if (style.UsageGauge)
            {
                StackPanel readings = new() { Orientation = Orientation.Horizontal };
                readings.Children.Add(CreateUsageGauge(style, color));
                readings.Children.Add(metrics);
                lines.Children.Add(readings);
            }
            else
                lines.Children.Add(metrics);
            if (style.UsageBar)
            {
                Border fill = new() { Width = 0, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left };
                Border track = new() { Tag = "UsageBar:" + style.Key, Width = style.UsageBarWidth, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(47, 63, 76)), Child = fill, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
                usageBars.Add(style.Key, (track, fill)); lines.Children.Add(track);
            }
            if (style.FanWidget)
                lines.Children.Add(CreateFanWidget(style, color));
            StackPanel contents = new() { Orientation = style.GraphBelow ? Orientation.Vertical : Orientation.Horizontal };
            contents.Children.Add(lines);
            if (style.Graph)
            {
                FrameworkElement graph;
                if (style.GraphSource == ArtworkGraphSource.FrameTime)
                {
                    FrameGraph frameGraph = new() { Width = style.GraphWidth, Height = style.GraphHeight, LineBrush = new SolidColorBrush(color), LineOnly = style.GraphTransparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                    graphs.Add(style.Key, frameGraph);
                    graph = frameGraph;
                }
                else
                    graph = CreateUsagePlot(style, color, style.GraphWidth, style.GraphHeight);
                graph.Margin = style.GraphTransparent ? new Thickness(0) : style.GraphBelow ? new Thickness(0, 8, 0, 0) : new Thickness(18, 0, 0, 0);
                if (style.ThemeCard && style.GraphBelow) lines.Children.Insert(2, graph);
                else
                    contents.Children.Add(graph);
            }
            if (style.TextShadow) contents.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 };
            panel.Child = contents;
        }
        UpdateData(data);
        PositionPanels();
        UpdateSelectionOutlines();
        RefreshArtworkAnimations();
    }
    public void UpdateData(ImmutableArray<OverlaySectionData> value)
    {
        value = value.Select(section => section.Kind == SectionKind.Game ? section with
        {
            Metrics = [new("game", "Game", gameName), new("api", "Present API", presentApi)]
        } : section).ToImmutableArray();
        data = value;
        UpdateArtworkReadings();
        UpdateArtworkBars();
        if (dragging is not null) return;
        bool resized = false;
        foreach ((string key, TextBlock reading) in rtssTexts)
        {
            SectionStyle style = preferences.Sections.Single(item => item.Key == key);
            string text = RtssReading(style.RtssTextSource);
            if (reading.Text == text) continue;
            reading.Text = text;
            Border panel = panels[key];
            Size previous = panel.DesiredSize;
            panel.InvalidateMeasure();
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            resized |= previous != panel.DesiredSize;
        }
        foreach (SectionStyle style in preferences.Sections)
        {
            if (style.Kind == SectionKind.Artwork) continue;
            OverlaySectionData section = data.Single(d => d.Kind == style.Kind);
            Border panel = panels[style.Key];
            bool changed = false;
            string title = string.IsNullOrWhiteSpace(style.Name) ? section.Name : style.Name;
            if (titles[style.Key].Text != title) { titles[style.Key].Text = title; changed = true; }
            foreach (string id in style.Metrics)
            {
                MetricValue metric = section.Metrics.Single(m => m.Id == id);
                Visibility visibility = preferences.HideUnknownTechnology && !editing && !metric.Available ? Visibility.Collapsed : Visibility.Visible;
                foreach (FrameworkElement element in metricElements[(style.Key, id)])
                    if (element.Visibility != visibility) { element.Visibility = visibility; changed = true; }
                if (style.Layout == MetricLayout.Flow)
                {
                    Run reading = flowValues[(style.Key, id)];
                    if (reading.Text != metric.Text) { reading.Text = metric.Text; changed = true; }
                }
                else if (values[(style.Key, id)].Text != metric.Text) { values[(style.Key, id)].Text = metric.Text; changed = true; }
            }
            if (usageGauges.TryGetValue(style.Key, out (System.Windows.Shapes.Path Arc, TextBlock Label) gauge))
            {
                double? percent = section.Metrics.Single(metric => metric.Id == "usage").Percent;
                gauge.Arc.Data = GaugeArc(percent);
                gauge.Label.Text = percent is double reading ? $"{reading:0}%" : "—";
            }
            if (usageTrends.TryGetValue(style.Key, out Polyline? trend))
                UpdateUsageTrend(style.Key, section.Metrics.Single(metric => metric.Id == (style.Kind == SectionKind.Ram ? "used" : "usage")).Percent, trend,
                    !style.Graph || style.GraphSource == ArtworkGraphSource.FrameTime ? 306 : style.GraphWidth,
                    !style.Graph || style.GraphSource == ArtworkGraphSource.FrameTime ? 46 : style.GraphHeight);
            if (usageBars.TryGetValue(style.Key, out (Border Track, Border Fill) bar))
            {
                double? percent = section.Metrics.Single(metric => metric.Id == "usage").Percent;
                double fillWidth = percent.HasValue ? style.UsageBarWidth * Math.Clamp(percent.Value, 0, 100) / 100 : 0;
                if (bar.Fill.Width != fillWidth) bar.Fill.Width = fillWidth;
                Visibility barVisibility = percent.HasValue || editing ? Visibility.Visible : Visibility.Collapsed;
                if (bar.Track.Visibility != barVisibility) { bar.Track.Visibility = barVisibility; changed = true; }
            }
            if (fanWidgets.TryGetValue(style.Key, out (FrameworkElement Widget, RotateTransform Spin, TextBlock Label) fan))
            {
                fanRpms[style.Key] = section.FanRpm;
                string fanLabel = section.FanRpm is double rpm ? $"FAN  {rpm:0} RPM" : "FAN  —";
                if (fan.Label.Text != fanLabel) { fan.Label.Text = fanLabel; fan.Widget.InvalidateMeasure(); changed = true; }
            }
            bool hasVisibleContent = style.ShowName || style.Graph || style.Metrics.Any(id => editing || !preferences.HideUnknownTechnology || section.Metrics.Single(metric => metric.Id == id).Available);
            hasVisibleContent |= usageBars.TryGetValue(style.Key, out (Border Track, Border Fill) visibleBar) && visibleBar.Track.Visibility == Visibility.Visible;
            hasVisibleContent |= style.UsageGauge || style.FanWidget;
            Visibility panelVisibility = style.Visible && hasVisibleContent ? Visibility.Visible : Visibility.Collapsed;
            if (panel.Visibility != panelVisibility) { panel.Visibility = panelVisibility; changed = true; }
            if (changed || !panel.IsMeasureValid)
            {
                Size previous = panel.DesiredSize;
                if (changed) { panel.Child?.InvalidateMeasure(); panel.InvalidateMeasure(); }
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                resized |= previous != panel.DesiredSize;
            }
        }
        if (resized) { PositionPanels(); UpdateSelectionOutlines(); }
        RefreshFanAnimations();
        RefreshArtworkAnimations();
    }
    private string RtssReading(RtssTextSource source)
    {
        MetricValue Metric(SectionKind kind, string id) => data.Single(section => section.Kind == kind).Metrics.Single(metric => metric.Id == id);
        string Reading(SectionKind kind, string id) => Metric(kind, id).Text is "Unavailable" ? "—" : Metric(kind, id).Text.Replace(".0 ", " ", StringComparison.Ordinal);
        string Percent(SectionKind kind) => Metric(kind, "usage").Percent is double value ? $"{value:0}%" : "—";
        return source switch
        {
            RtssTextSource.FrameRate => $"{Reading(SectionKind.Frames, "app")}  {Reading(SectionKind.Frames, "frametime")}",
            RtssTextSource.GameInfo => $"{gameName}\n{presentApi}",
            RtssTextSource.Clock => DateTime.Now.ToString("HH:mm"),
            RtssTextSource.GpuUsage => Percent(SectionKind.Gpu),
            RtssTextSource.GpuClock => Reading(SectionKind.Gpu, "clock"),
            RtssTextSource.GpuVram => Reading(SectionKind.Gpu, "vram"),
            RtssTextSource.GpuTemperature => Reading(SectionKind.Gpu, "temperature"),
            RtssTextSource.GpuName => data.Single(section => section.Kind == SectionKind.Gpu).Name,
            RtssTextSource.GpuPower => Reading(SectionKind.Gpu, "power"),
            RtssTextSource.CpuUsage => Percent(SectionKind.Cpu),
            RtssTextSource.CpuTemperature => Reading(SectionKind.Cpu, "temperature"),
            RtssTextSource.CpuName => data.Single(section => section.Kind == SectionKind.Cpu).Name,
            RtssTextSource.CpuPower => Reading(SectionKind.Cpu, "power"),
            RtssTextSource.RamUsage => data.Single(section => section.Kind == SectionKind.Ram).Metrics.Single(metric => metric.Id == "used").Percent is double percent ? $"{percent:0}%" : "—",
            RtssTextSource.RamUsed => Reading(SectionKind.Ram, "used"),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Select a supported RTSS text field.")
        };
    }
    public void UpdateGameInfo(string name, string runtime, ImageSource? icon)
    {
        gameName = name;
        presentApi = runtime;
        gameIconSource = icon;
        foreach (Image image in gameIcons.Values)
        {
            image.Source = icon;
            image.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateData(data);
    }

    private static FrameworkElement MetricIcon(string id, Color color)
    {
        string drawing = id switch
        {
            "usage" => "M 2,13 A 6,6 0 0 1 14,13 M 8,9 L 11,5",
            "temperature" => "M 6,3 L 6,10 A 4,4 0 1 0 10,10 L 10,3 A 2,2 0 0 0 6,3 M 8,5 L 8,12",
            "power" => "M 9,1 L 3,9 L 8,9 L 6,15 L 13,6 L 8,6 Z",
            "clock" => "M 8,2 A 6,6 0 1 1 7.99,2 M 8,5 L 8,8 L 11,10",
            "vram" or "used" => "M 3,4 L 13,4 L 13,12 L 3,12 Z M 5,2 L 5,4 M 8,2 L 8,4 M 11,2 L 11,4 M 5,12 L 5,14 M 8,12 L 8,14 M 11,12 L 11,14",
            _ => "M 1,10 L 4,10 L 6,4 L 8,13 L 10,7 L 15,7"
        };
        return new System.Windows.Shapes.Path { Data = Geometry.Parse(drawing), Stroke = new SolidColorBrush(color), StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    }
    private FrameworkElement CreateUsageTrend(SectionStyle style, Color color)
    {
        StackPanel container = new() { Margin = new Thickness(0, 0, 0, 10) };
        container.Children.Add(new TextBlock { Text = "RECENT LOAD", Foreground = new SolidColorBrush(color), FontSize = 10, FontFamily = new FontFamily(preferences.Font), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        container.Children.Add(CreateUsagePlot(style, color, 306, 46));
        return container;
    }
    private Canvas CreateUsagePlot(SectionStyle style, Color color, double width, double height)
    {
        Canvas plot = new() { Width = width, Height = height, ClipToBounds = true, Background = style.GraphTransparent ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(32, color.R, color.G, color.B)) };
        if (!style.GraphTransparent)
            plot.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height / 2, Y2 = height / 2, Stroke = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), StrokeThickness = 1 });
        Polyline trend = new() { Stroke = new SolidColorBrush(color), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        AutomationProperties.SetAutomationId(trend, "UsageTrend_" + style.Key);
        plot.Children.Add(trend);
        usageTrends.Add(style.Key, trend);
        return plot;
    }
    private void UpdateUsageTrend(string key, double? percent, Polyline trend, double width, double height)
    {
        if (percent is null)
        {
            usageSamples.Remove(key);
            trend.Points = new PointCollection();
            return;
        }
        long now = Environment.TickCount64;
        ImmutableArray<(long At, double Percent)> samples = usageSamples.GetValueOrDefault(key, []).Where(sample => now - sample.At <= 20000).ToImmutableArray();
        if (samples.IsEmpty || now - samples[^1].At >= 500 || Math.Abs(samples[^1].Percent - percent.Value) >= 0.1)
            samples = samples.Add((samples.IsEmpty ? now : Math.Max(now, samples[^1].At + 1), Math.Clamp(percent.Value, 0, 100)));
        usageSamples[key] = samples;
        PointCollection points = new();
        if (samples.Length == 1)
        {
            double y = height - samples[0].Percent * height / 100;
            points.Add(new Point(width - 4, y));
            points.Add(new Point(width, y));
        }
        else
            foreach ((long at, double value) in samples)
                points.Add(new Point((at - samples[0].At) * width / (samples[^1].At - samples[0].At), height - value * height / 100));
        trend.Points = points;
    }
    private FrameworkElement CreateUsageGauge(SectionStyle style, Color color)
    {
        Grid widget = new() { Width = 90, Height = 90, Margin = new Thickness(0, 0, 18, 0) };
        AutomationProperties.SetAutomationId(widget, "UsageGauge_" + style.Key);
        widget.Children.Add(new Ellipse { Width = 74, Height = 74, Stroke = new SolidColorBrush(Color.FromRgb(48, 65, 79)), StrokeThickness = 7 });
        System.Windows.Shapes.Path arc = new() { Stroke = new SolidColorBrush(color), StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        widget.Children.Add(arc);
        TextBlock label = new() { Text = "—", Foreground = Brushes.White, FontFamily = new FontFamily(preferences.Font), FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        StackPanel center = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(label);
        center.Children.Add(new TextBlock { Text = "LOAD", Foreground = new SolidColorBrush(color), FontFamily = new FontFamily(preferences.Font), FontSize = 9, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        widget.Children.Add(center);
        usageGauges.Add(style.Key, (arc, label));
        return widget;
    }
    private static Geometry GaugeArc(double? percent)
    {
        if (percent is null or <= 0) return Geometry.Empty;
        double degrees = Math.Min(359.9, Math.Clamp(percent.Value, 0, 100) * 3.6);
        double radians = (degrees - 90) * Math.PI / 180;
        StreamGeometry arc = new();
        using (StreamGeometryContext context = arc.Open())
        {
            context.BeginFigure(new Point(45, 8), false, false);
            context.ArcTo(new Point(45 + 37 * Math.Cos(radians), 45 + 37 * Math.Sin(radians)), new Size(37, 37), 0, degrees > 180, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        return arc;
    }
    private FrameworkElement CreateFanWidget(SectionStyle style, Color color)
    {
        SolidColorBrush brush = new(color);
        Canvas blades = new() { Width = 36, Height = 36, RenderTransformOrigin = new Point(0.5, 0.5) };
        AutomationProperties.SetAutomationId(blades, "FanBlades_" + style.Key);
        RotateTransform spin = new(); blades.RenderTransform = spin;
        Geometry bladeShape = Geometry.Parse("M 18,16 C 14,9 17,3 24,4 C 29,5 30,10 26,13 C 23,15 20,16 18,16 Z");
        bladeShape.Freeze();
        for (int index = 0; index < 3; index++)
            blades.Children.Add(new System.Windows.Shapes.Path { Data = bladeShape, Fill = brush, RenderTransform = new RotateTransform(index * 120, 18, 18) });
        blades.Children.Add(new Ellipse { Width = 34, Height = 34, Stroke = brush, StrokeThickness = 1.5, Opacity = 0.6 });
        Ellipse hub = new() { Width = 7, Height = 7, Fill = brush };
        Canvas.SetLeft(hub, 14.5); Canvas.SetTop(hub, 14.5); blades.Children.Add(hub);
        TextBlock label = new() { Text = "FAN  —", Foreground = brush, FontFamily = new FontFamily(preferences.Font), FontSize = style.ValueSize, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        StackPanel widget = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(widget, "FanWidget_" + style.Key);
        widget.Children.Add(new Viewbox { Width = style.FanSize, Height = style.FanSize, Child = blades }); widget.Children.Add(label);
        fanWidgets.Add(style.Key, (widget, spin, label));
        widget.IsVisibleChanged += (_, _) => RefreshFanAnimations();
        return widget;
    }
    private void FanWindowStateChanged(object? sender, EventArgs e) { RefreshFanAnimations(); RefreshArtworkAnimations(); }
    internal static BitmapSource DecodePng(string data)
    {
        using System.IO.MemoryStream stream = new(Convert.FromBase64String(data));
        BitmapImage image = new();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    private void RefreshArtworkAnimations()
    {
        bool active = artworkAnimations.Values.Any(item => item.Source is ArtworkAnimationSource.Loop or ArtworkAnimationSource.GpuFan) && IsLoaded && IsVisible && fanOwner is { WindowState: not WindowState.Minimized } && !preferences.ReduceMotion;
        if (active) artworkTimer.Start(); else artworkTimer.Stop();
        if (active) return;
        foreach ((string key, (Image Image, BitmapSource[] Frames, ArtworkAnimationSource Source, double Minimum, double Maximum, int Index, long UpdatedAt) item) in artworkAnimations.ToArray())
        {
            if (item.Source is not (ArtworkAnimationSource.Loop or ArtworkAnimationSource.GpuFan)) continue;
            item.Image.Source = item.Frames[0];
            artworkAnimations[key] = (item.Image, item.Frames, item.Source, item.Minimum, item.Maximum, 0, Environment.TickCount64);
        }
    }
    private void UpdateArtworkReadings()
    {
        foreach ((string key, (Image Image, BitmapSource[] Frames, ArtworkAnimationSource Source, double Minimum, double Maximum, int Index, long UpdatedAt) item) in artworkAnimations.ToArray())
        {
            SectionKind kind = item.Source switch { ArtworkAnimationSource.GpuUsage or ArtworkAnimationSource.GpuTemperatureAlarm => SectionKind.Gpu, ArtworkAnimationSource.CpuUsage or ArtworkAnimationSource.CpuTemperatureAlarm => SectionKind.Cpu, ArtworkAnimationSource.RamUsage => SectionKind.Ram, _ => SectionKind.Artwork };
            if (kind == SectionKind.Artwork) continue;
            string metricId = kind == SectionKind.Ram ? "used" : "usage";
            OverlaySectionData? section = data.FirstOrDefault(section => section.Kind == kind);
            double? percent = item.Source is ArtworkAnimationSource.GpuTemperatureAlarm or ArtworkAnimationSource.CpuTemperatureAlarm
                ? section?.Temperature : section?.Metrics.FirstOrDefault(metric => metric.Id == metricId)?.Percent;
            int index = percent is double value ? Math.Clamp((int)Math.Round((value - item.Minimum) / (item.Maximum - item.Minimum) * (item.Frames.Length - 1)), 0, item.Frames.Length - 1) : 0;
            if (index == item.Index) continue;
            item.Image.Source = item.Frames[index];
            artworkAnimations[key] = (item.Image, item.Frames, item.Source, item.Minimum, item.Maximum, index, item.UpdatedAt);
        }
    }
    private void UpdateArtworkBars()
    {
        foreach ((Border fill, double width, ArtworkBarSource source, double minimum, double maximum) in artworkBars.Values)
        {
            SectionKind kind = source switch
            {
                ArtworkBarSource.GpuUsage or ArtworkBarSource.GpuTemperature => SectionKind.Gpu,
                ArtworkBarSource.CpuUsage or ArtworkBarSource.CpuTemperature => SectionKind.Cpu,
                ArtworkBarSource.RamUsage => SectionKind.Ram,
                _ => throw new InvalidOperationException("An unsupported sensor was assigned to an RTSS live bar.")
            };
            OverlaySectionData section = data.Single(item => item.Kind == kind);
            double? value = source is ArtworkBarSource.GpuTemperature or ArtworkBarSource.CpuTemperature
                ? section.Temperature
                : section.Metrics.Single(metric => metric.Id == (kind == SectionKind.Ram ? "used" : "usage")).Percent;
            double next = value is double reading ? width * Math.Clamp((reading - minimum) / (maximum - minimum), 0, 1) : 0;
            if (fill.Width != next) fill.Width = next;
        }
    }
    private void AdvanceArtworkAnimations()
    {
        double? rpm = data.FirstOrDefault(section => section.Kind == SectionKind.Gpu)?.FanRpm;
        long now = Environment.TickCount64;
        foreach ((string key, (Image Image, BitmapSource[] Frames, ArtworkAnimationSource Source, double Minimum, double Maximum, int Index, long UpdatedAt) item) in artworkAnimations.ToArray())
        {
            if (!item.Image.IsVisible || item.Source is not (ArtworkAnimationSource.Loop or ArtworkAnimationSource.GpuFan)) continue;
            if (item.Source == ArtworkAnimationSource.GpuFan && rpm is not > 0)
            {
                if (item.Index != 0) item.Image.Source = item.Frames[0];
                artworkAnimations[key] = (item.Image, item.Frames, item.Source, item.Minimum, item.Maximum, 0, now);
                continue;
            }
            double interval = item.Source == ArtworkAnimationSource.GpuFan ? Math.Clamp(12000 / rpm!.Value, 100, 500) : preferences.Sections.Single(section => section.Key == key).AnimationIntervalMs;
            if (now - item.UpdatedAt < interval) continue;
            int next = (item.Index + 1) % item.Frames.Length;
            item.Image.Source = item.Frames[next];
            artworkAnimations[key] = (item.Image, item.Frames, item.Source, item.Minimum, item.Maximum, next, now);
        }
    }
    private void RefreshFanAnimations()
    {
        foreach ((string key, (FrameworkElement Widget, RotateTransform Spin, TextBlock Label) fan) in fanWidgets)
        {
            double rpm = fanRpms.TryGetValue(key, out double? value) && value is > 0 && IsLoaded && IsVisible && fan.Widget.IsVisible && fanOwner is { WindowState: not WindowState.Minimized } && !preferences.ReduceMotion
                ? Math.Max(100, Math.Round(value.Value / 100) * 100) : 0;
            if (animatedRpms.TryGetValue(key, out double previous) && previous == rpm) continue;
            animatedRpms[key] = rpm;
            fan.Spin.BeginAnimation(RotateTransform.AngleProperty, rpm == 0 ? null : new DoubleAnimation(fan.Spin.Angle, fan.Spin.Angle + 360, TimeSpan.FromSeconds(Math.Clamp(2000 / rpm, 0.6, 3))) { RepeatBehavior = RepeatBehavior.Forever });
        }
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
        if (name is "Minimal quartet" or "Compact strip" or "Sensor tower")
        {
            ImmutableArray<SectionStyle>.Builder placed = ImmutableArray.CreateBuilder<SectionStyle>();
            double quartetWidth = Math.Max(panels[SectionKind.Frames.ToString()].DesiredSize.Width, panels[SectionKind.Ram.ToString()].DesiredSize.Width);
            double quartetHeight = Math.Max(panels[SectionKind.Frames.ToString()].DesiredSize.Height, panels[SectionKind.Gpu.ToString()].DesiredSize.Height);
            double bandX = 12;
            double towerY = 24;
            foreach (SectionStyle section in preferences.Sections)
            {
                Size size = panels[section.Key].DesiredSize;
                Point point = name switch
                {
                    "Minimal quartet" => section.Kind switch
                    {
                        SectionKind.Frames => new Point(24, 24),
                        SectionKind.Gpu => new Point(24 + quartetWidth + 36, 24),
                        SectionKind.Cpu => new Point(24 + quartetWidth + 36, 24 + quartetHeight + 28),
                        _ => new Point(24, 24 + quartetHeight + 28)
                    },
                    "Compact strip" => new Point(bandX, 12),
                    _ => new Point(24, towerY)
                };
                placed.Add(OverlayData.Move(section, point, new Size(Width, Height), size));
                bandX += size.Width + 6;
                towerY += size.Height + 12;
            }
            return preferences with { Sections = placed.ToImmutable() };
        }
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
    public sealed record Choice(string Name, bool IsNew);
    public static ImmutableArray<Choice> Choices => Names.Select(name => new Choice(name, name is "Signal panels" or "Minimal quartet" or "Compact strip" or "Sensor tower")).ToImmutableArray();
    public static ImmutableArray<string> Names => ["Classic RTSS", "Hardware dossier", "Benchmark grid", "FPS spotlight", "Telemetry bar"
        , "Signal panels", "Minimal quartet", "Compact strip", "Sensor tower"
    ];
    public static Preferences Create(Preferences existing, string name)
    {
        if (!Names.Contains(name)) throw new ArgumentException("Unknown layout preset.", nameof(name));
        ImmutableArray<SectionKind> order = name is "FPS spotlight" or "Signal panels" or "Minimal quartet" or "Compact strip" or "Sensor tower"
            ? [SectionKind.Frames, SectionKind.Gpu, SectionKind.Cpu, SectionKind.Ram]
            : [SectionKind.Gpu, SectionKind.Cpu, SectionKind.Ram, SectionKind.Frames];
        return existing with
        {
            Opacity = name == "Compact strip" ? 0.68 : name is "Signal panels" or "Sensor tower" ? 0.86 : 0,
            Font = name == "Classic RTSS" ? "Consolas" : "Segoe UI",
            Sections = order.Select(kind => Style(Preferences.Initial.Sections.Single(s => s.Kind == kind), name)).ToImmutableArray()
        };
    }
    private static SectionStyle Style(SectionStyle section, string preset)
    {
        ImmutableArray<string> metrics = (preset, section.Kind) switch
        {
            ("Minimal quartet", SectionKind.Frames) => ["app", "low"],
            ("Minimal quartet", SectionKind.Gpu or SectionKind.Cpu) => ["usage", "temperature"],
            ("Compact strip", SectionKind.Frames) => ["app", "average", "low", "frametime"],
            ("Compact strip", SectionKind.Gpu) => ["usage", "temperature", "power"],
            ("Compact strip", SectionKind.Cpu) => ["usage", "temperature"],
            ("Sensor tower", SectionKind.Frames) => ["app", "frametime"],
            ("Sensor tower", SectionKind.Gpu) => ["usage", "temperature", "vram"],
            ("Sensor tower", SectionKind.Cpu) => ["usage", "temperature"],
            ("Classic RTSS", SectionKind.Frames) => ["app", "frametime"],
            ("FPS spotlight", SectionKind.Frames) => ["app", "average", "low"],
            ("Telemetry bar", SectionKind.Frames) => ["app", "low"],
            ("Signal panels", SectionKind.Frames) => ["app", "average", "low", "frametime"],
            (_, SectionKind.Frames) => ["app", "average", "low", "frametime"],
            ("Hardware dossier" or "Signal panels", SectionKind.Gpu) => ["usage", "temperature", "clock", "power", "vram"],
            ("Benchmark grid", SectionKind.Gpu) => ["usage", "temperature", "power", "vram"],
            ("Classic RTSS", SectionKind.Gpu) => ["usage", "temperature", "clock", "power"],
            (_, SectionKind.Gpu) => ["usage", "temperature"],
            ("Hardware dossier" or "Signal panels", SectionKind.Cpu) => ["usage", "temperature", "power"],
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
            "Minimal quartet" when section.Kind == SectionKind.Frames => MetricLayout.Tiles,
            "Sensor tower" when section.Kind == SectionKind.Frames => MetricLayout.Tiles,
            "Hardware dossier" or "Benchmark grid" or "Signal panels" => MetricLayout.Table,
            "Telemetry bar" => MetricLayout.Tiles,
            "FPS spotlight" when section.Kind == SectionKind.Frames => MetricLayout.Tiles,
            _ => MetricLayout.Flow
        };
        SectionStyle styled = section with
        {
            Name = preset == "Hardware dossier" && section.Kind is SectionKind.Gpu or SectionKind.Cpu ? "" : section.Kind == SectionKind.Frames ? "PERFORMANCE" : section.Kind.ToString().ToUpperInvariant(),
            NameColor = color, ValueColor = layout == MetricLayout.Flow ? color : "#F4F6F8",
            ValueSize = preset == "Compact strip" ? 14 : preset is "Telemetry bar" or "Minimal quartet" ? 22 : 18, NameSize = 16,
            HeroSize = preset == "Signal panels" ? 34 : section.Kind == SectionKind.Frames ? preset switch { "FPS spotlight" => 64, "Minimal quartet" => 48, "Sensor tower" => 42, "Benchmark grid" => 36, _ => 0 } : 0,
            Visible = true, Padding = preset == "Compact strip" ? 4 : 2, TextShadow = true,
            ShowName = preset is "Hardware dossier" or "Benchmark grid" or "Telemetry bar" or "Signal panels" or "Sensor tower",
            Horizontal = true, Layout = layout,
            Graph = section.Kind == SectionKind.Frames && preset is "Benchmark grid" or "FPS spotlight" or "Signal panels",
            GraphBelow = true, GraphWidth = preset is "FPS spotlight" or "Signal panels" ? 306 : 260, GraphHeight = 64,
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
        if (preset == "Signal panels") styled = styled with { ThemeCard = true, TextShadow = false };
        if (preset == "Sensor tower") styled = styled with { ThemeCard = true, UsageGauge = section.Kind == SectionKind.Gpu, TextShadow = false };
        return styled;
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
    public void UpdateGameInfo(string name, string runtime, ImageSource? icon) => Surface.UpdateGameInfo(name, runtime, icon);
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
        if (Surface.RenderTransform is not TranslateTransform translation || translation.X != -content.X || translation.Y != -content.Y)
            Surface.RenderTransform = new TranslateTransform(-content.X, -content.Y);
        Rect screen = new(display.Bounds.X + content.X * display.Scale, display.Bounds.Y + content.Y * display.Scale, Math.Ceiling(content.Width * display.Scale), Math.Ceiling(content.Height * display.Scale));
        if (placed != screen) { Desktop.PlaceOverlay(this, screen); placed = screen; }
    }
}

