using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FrameTrace;

public static class SmokeTest
{
    public static async Task RunArtworkAsync(string skinPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        try
        {
            RtssArtwork skin = RtssArtworkImporter.Read(skinPath);
            Require(skin.Sprites.Length == 26, "The supplied RTSS skin did not yield its decorated and plain still-image sprites.");
            Require(skin.Animations.Length == 7, "The supplied RTSS sprite animations and temperature alarms were not recognized.");
            Require(skin.LiveGraphs.Length == 4, "The supplied RTSS frame-time and hardware-load graphs were not recognized.");
            Require(skin.LiveBars.Length == 5, "The supplied RTSS usage and temperature bars were not recognized.");
            Require(skin.Layout is { TotalLayers: 79 } && skin.Layout.Placements.Length > 30, "The RTSS skin layer positions were not read.");
            RtssLayoutResult placedSkin = RtssLayout.Build(skin, new Size(3440, 1440));
            File.WriteAllLines(Path.Combine(outputDirectory, "rtss-positions.txt"), placedSkin.Sections.Select(item => $"{item.Name}: {item.X * (3440 - item.ImageWidth):0.#}, {item.Y * (1440 - item.ImageHeight):0.#} · {item.ImageWidth:0.#} × {item.ImageHeight:0.#}"));
            Require(placedSkin.PlacedLayers > 60 && placedSkin.Sections.Length <= Preferences.MaxOverlayItems, "The RTSS skin did not place its artwork and live text on the canvas.");
            Require(placedSkin.Sections.Any(item => item.RtssTextSource == RtssTextSource.FrameRate) && placedSkin.Sections.Any(item => item.RtssTextSource == RtssTextSource.GpuTemperature), "The RTSS live FPS and GPU text fields were not placed.");
            Require(placedSkin.Sections.Single(item => item.Name == "FPS  Img_BigIco").Y < placedSkin.Sections.Single(item => item.Name == "GPU  Img_BigIco").Y,
                "The RTSS FPS and GPU layers lost their original vertical order.");
            Require(placedSkin.Sections.Single(item => item.Name == "FPS  Banner_head").X > placedSkin.Sections.Single(item => item.Name == "FPS  Img_BigIco").X,
                "The RTSS banner head lost its right-aligned extent position.");
            Require(placedSkin.Sections.Where(item => item.Graph).All(item => item.GraphTransparent), "Imported RTSS graphs need transparent backgrounds.");
            Preferences placedPreferences = Preferences.Validate(Preferences.Initial with { OverlayScale = 1, Sections = placedSkin.Sections });
            Require(Preferences.Parse(JsonSerializer.Serialize(placedPreferences)).Sections.Length == placedSkin.Sections.Length, "The imported RTSS layout did not survive save and reload.");
            OverlayCanvas placedCanvas = new() { Width = 3440, Height = 1440 };
            placedCanvas.Apply(placedPreferences);
            placedCanvas.UpdateGameInfo("PEAK.exe", "D3D11", null);
            placedCanvas.UpdateData(OverlayData.Build([
                new("Radeon RX 9600 XT", "GpuAmd", "GPU Core", "Load", 99, "%"),
                new("Radeon RX 9600 XT", "GpuAmd", "GPU Core", "Temperature", 63, "°C"),
                new("Radeon RX 9600 XT", "GpuAmd", "GPU Package", "Power", 130, "W"),
                new("Radeon RX 9600 XT", "GpuAmd", "GPU Core", "Clock", 2600, "MHz"),
                new("Radeon RX 9600 XT", "GpuAmd", "GPU Memory Used", "SmallData", 6656, "MB"),
                new("Ryzen 7 5800XT", "Cpu", "CPU Total", "Load", 43, "%"),
                new("Ryzen 7 5800XT", "Cpu", "CPU Package", "Temperature", 72, "°C"),
                new("Ryzen 7 5800XT", "Cpu", "CPU Package", "Power", 75, "W"),
                new("Total Memory", "Memory", "Memory", "Load", 59, "%"),
                new("Total Memory", "Memory", "Memory Used", "Data", 18874, "MB")
            ], FrameMetrics.Summarize([], Environment.TickCount64) with { AppFps = 39, FrameTime = 25.6 }));
            SectionStyle fpsText = placedSkin.Sections.Single(item => item.RtssTextSource == RtssTextSource.FrameRate);
            TextBlock fpsReading = Descendants(placedCanvas).OfType<TextBlock>().Single(item => AutomationProperties.GetAutomationId(item) == "RtssText_" + fpsText.Key);
            Require(fpsReading.Text.Contains("39", StringComparison.Ordinal) && fpsReading.Text.Contains("25.6 ms", StringComparison.Ordinal), "The imported FPS field did not follow live frame data.");
            placedCanvas.Measure(new Size(3440, 1440)); placedCanvas.Arrange(new Rect(0, 0, 3440, 1440)); placedCanvas.UpdateLayout();
            SavePresetImage(placedCanvas, Path.Combine(outputDirectory, "rtss-placed-layout.png"));
            RtssSprite background = skin.Sprites.Single(item => item.Name.Contains("Background_02", StringComparison.Ordinal));
            Require(background.Width == 280 && background.Height == 86 && background.Preview.PixelWidth == 280 && background.Preview.PixelHeight == 86 && skin.UnsupportedTables == 7, "The two-part RTSS background was not combined or empty tables were counted as missing artwork.");
            PngBitmapEncoder backgroundImage = new(); backgroundImage.Frames.Add(BitmapFrame.Create(background.Preview));
            using (FileStream file = File.Create(Path.Combine(outputDirectory, "rtss-combined-background.png"))) backgroundImage.Save(file);
            string fragmentDirectory = Path.Combine(outputDirectory, "fragmented-test");
            Directory.CreateDirectory(fragmentDirectory);
            string fragmentedSkin = Path.Combine(fragmentDirectory, "fragmented.ovl");
            File.WriteAllText(fragmentedSkin, "[Settings]\nEmbeddedImage=Bookmark.png\n[Table0]\nName=Parts\nLines=1\nLine0Name=<I=250,86,2,4,500,172><C><I=30,86,582,4,60,172>\n", System.Text.Encoding.Latin1);
            File.Copy(Path.Combine(Path.GetDirectoryName(skinPath) ?? throw new InvalidDataException("RTSS test skin has no directory."), "Bookmark.png"), Path.Combine(fragmentDirectory, "Bookmark.png"), true);
            RtssArtwork fragments = RtssArtworkImporter.Read(fragmentedSkin);
            Require(fragments.Sprites.Length == 2 && fragments.Sprites[0].Width == 250 && fragments.Sprites[1].Width == 30 && fragments.UnsupportedTables == 0, "RTSS artwork separated by formatting was dropped or miscounted.");
            ComboBox artworkChoices = new() { DisplayMemberPath = "Name", ItemsSource = skin.Sprites.Cast<object>().Concat(skin.Animations).ToArray(), SelectedIndex = 0 };
            ComboBox graphChoices = new() { DisplayMemberPath = "Name", ItemsSource = skin.LiveGraphs.Cast<object>().Concat(skin.LiveBars).ToArray(), SelectedIndex = 0 };
            artworkChoices.SetResourceReference(FrameworkElement.StyleProperty, "CaptureTargetStyle");
            graphChoices.SetResourceReference(FrameworkElement.StyleProperty, "CaptureTargetStyle");
            StackPanel selectors = new(); selectors.Children.Add(artworkChoices); selectors.Children.Add(graphChoices);
            Window selectorHost = new() { Content = selectors, Width = 320, Height = 120, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                selectorHost.Show(); selectorHost.UpdateLayout();
                Require(VisualText(artworkChoices).Contains(skin.Sprites[0].Name), "The selected RTSS artwork showed its encoded image data instead of its name.");
                Require(VisualText(graphChoices).Contains(skin.LiveGraphs[0].Name), "The selected RTSS graph showed record details instead of its name.");
                graphChoices.SelectedItem = skin.LiveBars[0]; selectorHost.UpdateLayout();
                Require(VisualText(graphChoices).Contains(skin.LiveBars[0].Name), "The selected RTSS bar showed record details instead of its name.");
                artworkChoices.SelectedItem = skin.Animations[0]; selectorHost.UpdateLayout();
                Require(VisualText(artworkChoices).Contains(skin.Animations[0].Name), "The selected RTSS animation showed frame data instead of its name.");
            }
            finally { selectorHost.Close(); }
            RtssSprite sprite = skin.Sprites.Single(item => item.Name.Contains("BigIco_GPU", StringComparison.Ordinal));
            SectionStyle item = new(SectionKind.Artwork, sprite.Name, "#83EFCD", "#FFFFFF", 24, 24, 0.02, 0.02, true, [])
            {
                Id = Guid.NewGuid().ToString("N"), ImageData = sprite.ImageData, ImageWidth = sprite.Width, ImageHeight = sprite.Height, ShowName = false
            };
            Preferences layout = Preferences.Validate(Preferences.Initial with { Sections = [item] });
            Preferences restored = Preferences.Parse(JsonSerializer.Serialize(layout));
            Require(restored.Sections[0].ImageData == sprite.ImageData, "Imported artwork did not survive a layout export/import round trip.");
            OverlayCanvas canvas = new(); canvas.Apply(restored);
            canvas.Measure(new Size(1920, 1080)); canvas.Arrange(new Rect(0, 0, 1920, 1080)); canvas.UpdateLayout();
            Border imagePanel = canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == item.Key);
            Require(imagePanel.Child is Image image && image.Source is not null && image.Width == sprite.Width, "The RTSS sprite did not render as an independent layer.");
            RtssAnimation banner = skin.Animations.Single(animation => animation.Name.Contains("Banner_anim", StringComparison.Ordinal));
            RtssAnimation fan = skin.Animations.Single(animation => animation.Name.Contains("Fans_anim", StringComparison.Ordinal));
            Require(banner.Frames.Length == 4 && banner.Source == ArtworkAnimationSource.Loop && banner.Frames.Distinct().Count() > 1, "The animated banner frames were not cropped from the atlas.");
            Require(fan.Frames.Length == 9 && fan.Source == ArtworkAnimationSource.GpuFan && fan.Frames.Distinct().Count() > 1, "The GPU fan frames were not cropped from the atlas.");
            Require(skin.Animations.Count(animation => animation.Source is ArtworkAnimationSource.GpuUsage or ArtworkAnimationSource.CpuUsage or ArtworkAnimationSource.RamUsage) == 3, "The live GPU, CPU, and RAM load animations were not recognized.");
            RtssAnimation gpuAlarm = skin.Animations.Single(animation => animation.Source == ArtworkAnimationSource.GpuTemperatureAlarm);
            Require(gpuAlarm.Frames.Length == 2 && gpuAlarm.Frames[0] != gpuAlarm.Frames[1] && gpuAlarm.Minimum == 69 && gpuAlarm.Maximum == 70, "The white/red GPU alarm flames were not paired at 70°C.");
            RtssAnimation cpuAlarm = skin.Animations.Single(animation => animation.Source == ArtworkAnimationSource.CpuTemperatureAlarm);
            Require(cpuAlarm.Frames.Length == 2 && cpuAlarm.Minimum == 84 && cpuAlarm.Maximum == 85, "The CPU alarm did not use the 85°C default.");
            SectionStyle oldCpuAlarm = new(SectionKind.Artwork, "CPU temperature alarm · 70°C", "#83EFCD", "#FFFFFF", 24, 24, 0.1, 0.1, true, [])
            {
                Id = Guid.NewGuid().ToString("N"), ShowName = false, AnimationFrames = cpuAlarm.Frames,
                AnimationSource = ArtworkAnimationSource.CpuTemperatureAlarm, AnimationMinimum = 69, AnimationMaximum = 70
            };
            SectionStyle upgradedAlarm = Preferences.Parse(JsonSerializer.Serialize(Preferences.Initial with { Sections = [oldCpuAlarm] })).Sections.Single();
            Require(upgradedAlarm.AnimationMaximum == 85 && upgradedAlarm.Name.EndsWith("85°C", StringComparison.Ordinal), "The saved 70°C CPU alarm was not upgraded.");
            SectionStyle customizedAlarm = Preferences.Parse(JsonSerializer.Serialize(Preferences.Initial with { Sections = [oldCpuAlarm with { AlarmThresholdCustomized = true }] })).Sections.Single();
            Require(customizedAlarm.AnimationMaximum == 70, "A user-selected 70°C threshold was overwritten.");
            Require(skin.LiveGraphs.Any(item => item.Source == ArtworkGraphSource.RamUsage), "The live RAM load graph was not recognized.");
            Require(OverlayData.Build([new("Total Memory", "Memory", "Memory", "Load", 95, "%")], FrameMetrics.Summarize([], Environment.TickCount64)).Single(section => section.Kind == SectionKind.Ram).Metrics.Single(metric => metric.Id == "used").Percent == 95, "The RAM graphics did not use total-memory load.");
            RtssAnimation gpuUsage = skin.Animations.Single(animation => animation.Source == ArtworkAnimationSource.GpuUsage);
            Require(gpuUsage.Frames.Length == 21 && gpuUsage.Minimum == 80 && gpuUsage.Maximum == 100, "The GPU load sprite range was not imported.");
            SectionStyle animated = item with
            {
                Id = Guid.NewGuid().ToString("N"), Name = banner.Name, ImageData = "", AnimationFrames = banner.Frames,
                AnimationSource = banner.Source, AnimationMinimum = banner.Minimum, AnimationMaximum = banner.Maximum,
                ImageWidth = banner.Width, ImageHeight = banner.Height, X = 0.04
            };
            Preferences animatedLayout = Preferences.Parse(JsonSerializer.Serialize(Preferences.Validate(restored with { Sections = restored.Sections.Add(animated) })));
            Require(animatedLayout.Sections[1].AnimationFrames.SequenceEqual(banner.Frames), "Imported animation did not survive layout export/import.");
            canvas.Apply(animatedLayout);
            Border animationPanel = canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == animated.Key);
            Require(animationPanel.Child is Image animationImage && animationImage.Source is not null && animationImage.Width == banner.Width, "The RTSS animation did not render as an independent layer.");
            RtssLiveGraph graph = skin.LiveGraphs.Single(item => item.Source == ArtworkGraphSource.FrameTime);
            SectionStyle graphItem = Preferences.Initial.Sections.Single(section => section.Kind == SectionKind.Frames) with
            {
                Id = Guid.NewGuid().ToString("N"), Name = graph.Name, ShowName = false, Metrics = [], Graph = true,
                GraphSource = graph.Source, GraphWidth = graph.Width, GraphHeight = graph.Height, GraphBelow = true, Padding = 0, X = 0.25, Y = 0.02
            };
            Preferences graphLayout = Preferences.Validate(animatedLayout with { Sections = animatedLayout.Sections.Add(graphItem) });
            SectionStyle loadAnimation = animated with
            {
                Id = Guid.NewGuid().ToString("N"), Name = gpuUsage.Name, AnimationFrames = gpuUsage.Frames,
                AnimationSource = gpuUsage.Source, AnimationMinimum = gpuUsage.Minimum, AnimationMaximum = gpuUsage.Maximum,
                ImageWidth = gpuUsage.Width, ImageHeight = gpuUsage.Height, X = 0.07
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(loadAnimation) });
            RtssAnimation ramUsage = skin.Animations.Single(animation => animation.Source == ArtworkAnimationSource.RamUsage);
            SectionStyle ramAnimation = animated with
            {
                Id = Guid.NewGuid().ToString("N"), Name = ramUsage.Name, AnimationFrames = ramUsage.Frames,
                AnimationSource = ramUsage.Source, AnimationMinimum = ramUsage.Minimum, AnimationMaximum = ramUsage.Maximum,
                ImageWidth = ramUsage.Width, ImageHeight = ramUsage.Height, X = 0.1
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(ramAnimation) });
            RtssLiveGraph gpuGraph = skin.LiveGraphs.Single(item => item.Source == ArtworkGraphSource.GpuUsage);
            SectionStyle gpuGraphItem = Preferences.Initial.Sections.Single(section => section.Kind == SectionKind.Gpu) with
            {
                Id = Guid.NewGuid().ToString("N"), Name = gpuGraph.Name, ShowName = false, Metrics = [], Graph = true,
                GraphSource = gpuGraph.Source, GraphWidth = gpuGraph.Width, GraphHeight = gpuGraph.Height,
                GraphBelow = true, Padding = 0, X = 0.31, Y = 0.02
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(gpuGraphItem) });
            RtssLiveGraph ramGraph = skin.LiveGraphs.Single(item => item.Source == ArtworkGraphSource.RamUsage);
            SectionStyle ramGraphItem = Preferences.Initial.Sections.Single(section => section.Kind == SectionKind.Ram) with
            {
                Id = Guid.NewGuid().ToString("N"), Name = ramGraph.Name, ShowName = false, Metrics = [], Graph = true,
                GraphSource = ramGraph.Source, GraphWidth = ramGraph.Width, GraphHeight = ramGraph.Height,
                GraphBelow = true, Padding = 0, X = 0.42, Y = 0.02
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(ramGraphItem) });
            RtssLiveBar gpuBar = skin.LiveBars.Single(item => item.Name == "GPU_ProgressBar_01");
            RtssLiveBar temperatureBar = skin.LiveBars.Single(item => item.Name == "GPU_prbar_thermometer");
            SectionStyle gpuBarItem = new(SectionKind.Artwork, gpuBar.Name, "#FFAA49", "#FFFFFF", 24, 24, 0.53, 0.02, true, [])
            {
                Id = Guid.NewGuid().ToString("N"), ShowName = false, BarSource = gpuBar.Source, BarMinimum = gpuBar.Minimum,
                BarMaximum = gpuBar.Maximum, ImageWidth = gpuBar.Width, ImageHeight = gpuBar.Height
            };
            SectionStyle temperatureBarItem = gpuBarItem with
            {
                Id = Guid.NewGuid().ToString("N"), Name = temperatureBar.Name, BarSource = temperatureBar.Source,
                BarMinimum = temperatureBar.Minimum, BarMaximum = temperatureBar.Maximum,
                ImageWidth = temperatureBar.Width, ImageHeight = temperatureBar.Height, Y = 0.08
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(gpuBarItem).Add(temperatureBarItem) });
            SectionStyle alarmItem = animated with
            {
                Id = Guid.NewGuid().ToString("N"), Name = gpuAlarm.Name, AnimationFrames = gpuAlarm.Frames,
                AnimationSource = gpuAlarm.Source, AnimationMinimum = gpuAlarm.Minimum, AnimationMaximum = gpuAlarm.Maximum,
                ImageWidth = gpuAlarm.Width, ImageHeight = gpuAlarm.Height, Y = 0.13
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(alarmItem) });
            SectionStyle cpuAlarmItem = alarmItem with
            {
                Id = Guid.NewGuid().ToString("N"), Name = cpuAlarm.Name, AnimationSource = cpuAlarm.Source,
                AnimationMinimum = cpuAlarm.Minimum, AnimationMaximum = cpuAlarm.Maximum, Y = 0.18
            };
            graphLayout = Preferences.Validate(graphLayout with { Sections = graphLayout.Sections.Add(cpuAlarmItem) });
            canvas.Apply(graphLayout);
            canvas.Measure(new Size(1920, 1080)); canvas.Arrange(new Rect(0, 0, 1920, 1080)); canvas.UpdateLayout();
            Require(Descendants(canvas).OfType<FrameGraph>().Any(), "The RTSS frame-time graph did not become a live Frame Trace graph.");
            Border gpuFill = Descendants(canvas).OfType<Border>().Single(item => AutomationProperties.GetAutomationId(item) == "RtssBarFill_" + gpuBarItem.Key);
            Border temperatureFill = Descendants(canvas).OfType<Border>().Single(item => AutomationProperties.GetAutomationId(item) == "RtssBarFill_" + temperatureBarItem.Key);
            Image alarmImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == alarmItem.Key).Child;
            BitmapSource coolFlame = (BitmapSource)alarmImage.Source;
            Image cpuAlarmImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == cpuAlarmItem.Key).Child;
            BitmapSource coolCpuFlame = (BitmapSource)cpuAlarmImage.Source;
            canvas.UpdateData(OverlayData.Build([new("Test GPU", "GpuAmd", "GPU Core", "Load", 10, "%"), new("Test GPU", "GpuAmd", "GPU Core", "Temperature", 60, "°C"), new("Test CPU", "Cpu", "CPU Package", "Temperature", 84, "°C")], FrameMetrics.Summarize([], Environment.TickCount64)));
            Require(ReferenceEquals(coolFlame, alarmImage.Source), "The GPU alarm changed below 70°C.");
            Require(ReferenceEquals(coolCpuFlame, cpuAlarmImage.Source), "The CPU alarm changed below 85°C.");
            Require(gpuFill.Width > 0 && gpuFill.Width < gpuBar.Width && temperatureFill.Width > 0 && temperatureFill.Width < temperatureBar.Width, "Imported RTSS usage or thermometer bars did not follow live sensor readings.");
            Image gpuImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == loadAnimation.Key).Child;
            BitmapSource unloadedFrame = (BitmapSource)gpuImage.Source;
            Image ramImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == ramAnimation.Key).Child;
            BitmapSource unloadedRamFrame = (BitmapSource)ramImage.Source;
            canvas.UpdateData(OverlayData.Build([new("Test GPU", "GpuAmd", "GPU Core", "Load", 95, "%"), new("Test GPU", "GpuAmd", "GPU Core", "Temperature", 60, "°C"), new("Total Memory", "Memory", "Memory", "Load", 95, "%")], FrameMetrics.Summarize([], Environment.TickCount64)));
            canvas.UpdateData(OverlayData.Build([new("Test GPU", "GpuAmd", "GPU Core", "Load", 95, "%"), new("Test GPU", "GpuAmd", "GPU Core", "Temperature", 70, "°C"), new("Test CPU", "Cpu", "CPU Package", "Temperature", 85, "°C"), new("Total Memory", "Memory", "Memory", "Load", 95, "%")], FrameMetrics.Summarize([], Environment.TickCount64)));
            Require(!ReferenceEquals(coolFlame, alarmImage.Source), "The GPU alarm did not turn red at 70°C.");
            Require(!ReferenceEquals(coolCpuFlame, cpuAlarmImage.Source), "The CPU alarm did not turn red at 85°C.");
            Require(Math.Abs(gpuFill.Width - gpuBar.Width) < 0.01, "The imported GPU usage bar did not reach its full range at 95% load.");
            Require(!ReferenceEquals(unloadedFrame, gpuImage.Source), "The imported GPU load animation did not follow a live 95% reading.");
            Require(!ReferenceEquals(unloadedRamFrame, ramImage.Source), "The imported RAM load animation did not follow a live 95% reading.");
            Require(Descendants(canvas).OfType<System.Windows.Shapes.Polyline>().Any(line => AutomationProperties.GetAutomationId(line) == "UsageTrend_" + gpuGraphItem.Key && line.Points.Count >= 2), "The RTSS GPU graph did not draw a live 95% load reading.");
            Require(Descendants(canvas).OfType<System.Windows.Shapes.Polyline>().Any(line => AutomationProperties.GetAutomationId(line) == "UsageTrend_" + ramGraphItem.Key && line.Points.Count >= 2), "The RTSS RAM graph did not draw a live 95% load reading.");
            SavePresetImage(canvas, Path.Combine(outputDirectory, "rtss-artwork.png"));
            Image animatedImage = (Image)animationPanel.Child;
            BitmapSource firstFrame = (BitmapSource)animatedImage.Source;
            Window host = new() { Content = canvas, Width = 64, Height = 64, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                host.Show();
                await Task.Delay(650);
                Require(!ReferenceEquals(firstFrame, animatedImage.Source), "The RTSS banner did not advance while its overlay was visible.");
                Preferences loopingLayout = graphLayout with { Sections = graphLayout.Sections.Select(section => section.Key == loadAnimation.Key ? section with { AnimationSource = ArtworkAnimationSource.Loop } : section).ToImmutableArray() };
                canvas.Apply(loopingLayout);
                Image loopingImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == loadAnimation.Key).Child;
                BitmapSource loopStart = (BitmapSource)loopingImage.Source;
                await Task.Delay(650);
                Require(!ReferenceEquals(loopStart, loopingImage.Source), "A sensor-driven RTSS sprite did not animate when changed to continuous playback.");
                Preferences slowLayout = loopingLayout with { Sections = loopingLayout.Sections.Select(section => section.Key == loadAnimation.Key ? section with { AnimationIntervalMs = 1000 } : section).ToImmutableArray() };
                canvas.Apply(Preferences.Validate(slowLayout));
                Image slowImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == loadAnimation.Key).Child;
                BitmapSource slowStart = (BitmapSource)slowImage.Source;
                await Task.Delay(400);
                Require(ReferenceEquals(slowStart, slowImage.Source), "The loop speed control advanced a frame before its selected interval.");
                await Task.Delay(750);
                Require(!ReferenceEquals(slowStart, slowImage.Source), "The loop speed control did not advance after its selected interval.");
                canvas.Apply(graphLayout with { ReduceMotion = true });
                Image reducedImage = (Image)canvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == animated.Key).Child;
                BitmapSource reducedFrame = (BitmapSource)reducedImage.Source;
                await Task.Delay(400);
                Require(ReferenceEquals(reducedFrame, reducedImage.Source), "Reduce motion did not pause the imported animation.");
            }
            finally { host.Close(); }
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), $"PASS: {skin.Sprites.Length} still images, {skin.Animations.Length} sprite animations, {skin.LiveGraphs.Length} live graphs, and {skin.LiveBars.Length} live bars loaded; {placedSkin.PlacedLayers} of {placedSkin.PlacedLayers + placedSkin.SkippedLayers} RTSS layers placed; {skin.UnsupportedTables} advanced tables left unconverted; portable layout round trip and visible layers verified.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "FAIL\n" + error);
            Environment.ExitCode = 1;
        }
    }
    public static async Task RunProbeAsync(string title)
    {
        TextBlock content = new() { Text = title, FontSize = 32 };
        Window probe = new() { Title = title, Width = 600, Height = 300, Content = content };
        Application.Current.MainWindow = probe;
        probe.Show();
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        await Task.Delay(14000);
        probe.Close();
    }

    public static async Task RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        MainWindow? window = null;
        try
        {
            string[] header = FrameMetrics.SplitCsv("Application,ProcessID,PresentRuntime,SwapChainAddress,FrameType,PresentMode,FrameTime,DisplayedTime,CPUStartQPCTime");
            FrameMetrics.ValidateHeader(header);
            FrameReading a = FrameMetrics.Parse(header, "\"game, test.exe\",123,DXGI,0x1,Application,Composed: Flip,10,5,100", 100);
            FrameReading b = a with { ReceivedAt = 110 };
            FrameReading generated = a with { ReceivedAt = 115, FrameType = "AMD AFMF", FrameTime = null };
            FrameSummary result = FrameMetrics.Summarize([a, b, generated], 120);
            Require(result.AppFps == 100 && result.DisplayFps == 200 && result.Generation == "AFMF 2.1", "Frame calculation failed.");
            Require(FrameMetrics.Summarize([a, b], 120).Generation == "Unavailable", "Application tags must not prove FG is off.");
            Require(FrameMetrics.Summarize([a, b], 2000).AppFps == 100, "Normal ETW batch delay discarded valid FPS.");

            Require(FrameMetrics.Summarize([a, b], 3200).AppFps is null, "Stale readings did not expire.");
            Require(FrameMetrics.Summarize([a, b, a with { SwapChain = "0x2", FrameTime = 100 }], 120).AppFps == 100, "Secondary swap chain contaminated FPS.");
            ImmutableArray<FrameReading> percentileFrames = Enumerable.Range(0, 100).Select(i => a with { ReceivedAt = 100 + i, StartedAt = 100 + i * 10, FrameTime = i == 99 ? 100 : 10 }).ToImmutableArray();
            FrameSummary percentile = FrameMetrics.Summarize(percentileFrames, 250);
            Require(percentile.LowFps == 10 && Math.Abs(percentile.AverageFps!.Value - 100000d / 1090) < 0.001, "Average / 1% low calculation failed.");
            Require(FrameMetrics.Summarize(percentileFrames.Take(99).ToImmutableArray(), 250).LowFps is null, "1% low requires 100 frames.");
            Preferences custom = Preferences.Initial with { Sections = Preferences.Initial.Sections.Select(section => section with { Labels = section.Kind == SectionKind.Frames ? section.Labels.SetItem("app", "My FPS") : section.Labels }).ToImmutableArray() };
            Preferences.Write(custom, Path.Combine(outputDirectory, "custom.json"));
            Require(Preferences.Parse(File.ReadAllText(Path.Combine(outputDirectory, "custom.json"))).Sections[0].Labels["app"] == "My FPS", "Custom labels did not survive saving.");
            Preferences sensorRate = Preferences.Initial with { SensorRefreshMs = 2500 };
            Preferences.Write(sensorRate, Path.Combine(outputDirectory, "sensor-refresh.json"));
            Require(Preferences.Parse(File.ReadAllText(Path.Combine(outputDirectory, "sensor-refresh.json"))).SensorRefreshMs == 2500, "Sensor refresh preference did not survive saving.");
            Preferences startup = Preferences.Initial with { StartMinimized = true, RunAtLogin = true, ReduceMotion = true, CheckUpdatesOnStartup = false };
            Preferences.Write(startup, Path.Combine(outputDirectory, "settings.json"));
            Preferences restoredSettings = Preferences.Parse(File.ReadAllText(Path.Combine(outputDirectory, "settings.json")));
            Require(restoredSettings.StartMinimized && restoredSettings.RunAtLogin && restoredSettings.ReduceMotion && !restoredSettings.CheckUpdatesOnStartup, "Startup and accessibility preferences did not survive saving.");
            Preferences localSettings = Preferences.Initial with { Shortcut = new Hotkey(3, 0x4B), OverlayEnabled = false, IgnoredApps = ["notepad.exe"], SensorRefreshMs = 1500, StartMinimized = true, RunAtLogin = true, ReduceMotion = true, CheckUpdatesOnStartup = false };
            Preferences sharedLayout = Preferences.ForLayoutExport(localSettings);
            Require(!sharedLayout.StartMinimized && !sharedLayout.RunAtLogin && !sharedLayout.ReduceMotion && sharedLayout.CheckUpdatesOnStartup && sharedLayout.IgnoredApps.SequenceEqual(Preferences.Initial.IgnoredApps), "Layout export included local startup, accessibility, update, or ignore-list settings.");
            Preferences importedLayout = Preferences.ApplyImportedLayout(localSettings, Preferences.Initial with { Font = "Arial" });
            Require(importedLayout.Font == "Arial" && importedLayout.Shortcut == localSettings.Shortcut && !importedLayout.OverlayEnabled && importedLayout.IgnoredApps.SequenceEqual(localSettings.IgnoredApps) && importedLayout.SensorRefreshMs == 1500 && importedLayout.StartMinimized && importedLayout.RunAtLogin && importedLayout.ReduceMotion && !importedLayout.CheckUpdatesOnStartup, "Importing a layout changed local app settings.");
            Require(!Preferences.HasLayoutChanges(localSettings, Preferences.Initial), "Local app settings incorrectly marked the overlay layout as edited.");
            Require(!Preferences.HasLayoutChanges(Preferences.Parse(JsonSerializer.Serialize(custom)), custom) && Preferences.HasLayoutChanges(custom, Preferences.Initial), "Layout edit detection missed customization or marked identical layouts as edited.");
            try { Preferences.Validate(Preferences.Initial with { SensorRefreshMs = 251 }); throw new InvalidDataException("Invalid sensor refresh interval was accepted."); }
            catch (InvalidDataException error) when (error.Message.StartsWith("Sensor refresh interval", StringComparison.Ordinal)) { }
            foreach (string preset in LayoutPresets.Names)
            {
                Preferences styled = Preferences.Validate(LayoutPresets.Create(Preferences.Initial, preset));
                Preferences restored = Preferences.Parse(JsonSerializer.Serialize(styled));
                Require(restored.Sections.Zip(styled.Sections).All(pair => pair.First.Layout == pair.Second.Layout && pair.First.HeroSize == pair.Second.HeroSize && pair.First.Padding == pair.Second.Padding && pair.First.GraphBelow == pair.Second.GraphBelow && pair.First.TextShadow == pair.Second.TextShadow), "Preset styling did not survive serialization.");
                Require(restored.Sections.Zip(styled.Sections).All(pair => pair.First.ThemeCard == pair.Second.ThemeCard && pair.First.UsageGauge == pair.Second.UsageGauge && pair.First.UsageBar == pair.Second.UsageBar && pair.First.FanWidget == pair.Second.FanWidget), "A graphical theme lost its widgets when exported.");
            }
            Require(FrameMetrics.Summarize([a, b, generated with { FrameType = "Intel XeSS-FG" }], 120).Generation == "Intel XeSS-FG reported", "XeSS-FG tags were not identified.");
            ImmutableArray<SensorReading> realSensors;
            using (HardwareMonitor hardware = await Task.Run(() => new HardwareMonitor()))
            {
                await Task.Delay(1100);
                realSensors = await Task.Run(hardware.Read);
                Require(realSensors.Any(s => s.Value.HasValue), "No real hardware sensor values.");
                File.WriteAllText(Path.Combine(outputDirectory, "sensors.json"), JsonSerializer.Serialize(realSensors));
            }
            window = new MainWindow();
            Application.Current.MainWindow = window;
            window.Loaded += (_, _) =>
            {
                if (Preferences.Load().OverlayEnabled)
                    ((Button)window.FindName("OverlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            window.Show();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.Activate();
            bool reduceMotion = Preferences.Load().ReduceMotion;
            Require(window.AnimatedBackground.ReducedMotion == reduceMotion && window.AnimatedBackground.IsAnimating == !reduceMotion, "The background motion setting was not applied on startup.");
            window.AnimatedBackground.SetReducedMotion(!reduceMotion);
            Require(window.AnimatedBackground.ReducedMotion != reduceMotion && window.AnimatedBackground.IsAnimating == reduceMotion, "The background motion setting did not apply live.");
            window.AnimatedBackground.SetReducedMotion(reduceMotion);
            window.AnimatedBackground.SetReducedMotion(false);
            window.WindowState = WindowState.Minimized;
            await Task.Delay(100);
            double pausedPhase = window.AnimatedBackground.Phase;
            Require(!window.AnimatedBackground.IsAnimating, "The background timer still runs while minimized.");
            await Task.Delay(120);
            Require(window.AnimatedBackground.Phase == pausedPhase, "Minimizing did not pause the animation clock.");
            window.WindowState = WindowState.Normal; window.Activate();
            Require(window.AnimatedBackground.IsAnimating, "The background animation did not resume after restoring.");
            window.AppFps.Text = "paused dashboard";
            window.WindowState = WindowState.Minimized;
            await Task.Delay(250);
            Require(window.AppFps.Text == "paused dashboard", "The minimized dashboard is still refreshing values.");
            window.WindowState = WindowState.Normal; window.Activate();
            Require(window.AppFps.Text != "paused dashboard", "Restoring did not refresh the dashboard immediately.");
            window.Pages.SelectedIndex = 2;
            window.AppFps.Text = "hidden dashboard";
            await Task.Delay(250);
            Require(window.AppFps.Text == "hidden dashboard", "The dashboard is still refreshing behind Settings.");
            window.Pages.SelectedIndex = 0;
            Require(window.AppFps.Text != "hidden dashboard", "Returning to the dashboard did not refresh its values.");
            window.Hide();
            Require(!window.AnimatedBackground.IsAnimating, "The background timer still runs while hidden.");
            window.Show(); window.Activate();
            window.AnimatedBackground.SetReducedMotion(reduceMotion);
            Size contentSize = ((FrameworkElement)window.Content).RenderSize;
            Require(window.AnimatedBackground.ActualWidth >= contentSize.Width - 2 && window.AnimatedBackground.ActualHeight >= contentSize.Height - 2, "The gaming background does not cover the application window.");
            Require(GamingBackground.PhaseAt(GamingBackground.LoopSeconds) == GamingBackground.PhaseAt(0) && GamingBackground.PhaseAt(GamingBackground.LoopSeconds - 0.001) > 0.999, "The background animation does not end on its starting frame.");
            if (!reduceMotion)
            {
                double tracePosition = window.AnimatedBackground.Phase;
                await Task.Delay(150);
                Require(Math.Abs(window.AnimatedBackground.Phase - tracePosition) > 0.001, "The full-window gaming background did not move.");
            }
            CheckCanvasEditing();
            CheckUsageBar(outputDirectory);
            CheckTableLabelStability();
            CheckSavedPresets(outputDirectory);
            CheckDataMigration(outputDirectory);
            TextBlock title = (TextBlock)window.FindName("HardwareStatus");
            title.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            FrameSummary captured;
            await using (FrameCapture capture = new())
            {
                await Task.Delay(6500);
                capture.CheckHealth(); captured = capture.ReadSummary(Environment.ProcessId);
                ImmutableArray<CaptureCandidate> candidates = capture.Candidates();
                File.WriteAllText(Path.Combine(outputDirectory, "capture-startup.json"), JsonSerializer.Serialize(new { Captured = captured, Candidates = candidates, Foreground = Desktop.Foreground()?.ProcessId, OwnPid = Environment.ProcessId, Messages = capture.Messages }));
                Require(FrameMetrics.SelectForeground(candidates, Environment.ProcessId, [], -1)?.ProcessId == Environment.ProcessId, "Foreground renderer was not selected.");
                Require(FrameMetrics.SelectForeground(candidates, Environment.ProcessId, [], Environment.ProcessId) is null, "Dashboard was selected as a game.");
                Require(FrameMetrics.SelectForeground(candidates, Environment.ProcessId, [Path.GetFileName(Environment.ProcessPath!)], -1) is null, "Ignore rule failed.");
                Require(CaptureRules.Ignore(["peak.exe"], "PEAK.EXE").Length == 1 && CaptureRules.Allow(["peak.exe", "game.exe"], "PEAK.EXE").SequenceEqual(["game.exe"]), "Dashboard capture rules must match executable names without case sensitivity.");
                Require(captured.AppFps > 0, "Real ETW capture returned no FPS.");
            }
            int foregroundChecks = 0, observedSamples = 0;
            await using FrameCapture probeCapture = new();
            foreach (string probeName in new[] { "Frame Trace probe one", "Frame Trace probe two" })
            {
                ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(App).Assembly.Location);
                start.ArgumentList.Add("--capture-probe"); start.ArgumentList.Add(probeName);
                using Process probe = Process.Start(start) ?? throw new InvalidOperationException("Cannot start renderer probe.");
                try
                {
                    await Task.Delay(4500);
                    int processSamples = 0;
                    for (int sample = 0; sample < 20; sample++)
                    {
                        window.WriteDiagnostics(Path.Combine(outputDirectory, probeName + ".json"));
                        if (probeCapture.ReadSummary(probe.Id).AppFps > 0)
                        {
                            observedSamples++; processSamples++;
                            Require(FrameMetrics.SelectForeground(probeCapture.Candidates(), probe.Id, [], Environment.ProcessId)?.ProcessId == probe.Id, "Renderer process routing failed.");
                        }
                        if (Desktop.Foreground()?.ProcessId == probe.Id)
                        {
                            foregroundChecks++;
                            Require(((TextBlock)window.FindName("GameName")).Text == probeName, "Automatic foreground switching failed for " + probeName);
                        }
                        await Task.Delay(250);
                    }
                    Require(processSamples > 0, "No real frames from " + probeName);
                    window.RefreshCaptureTargets();
                    ComboBox targetSelector = (ComboBox)window.FindName("CaptureTargetSelector");
                    CaptureTarget manual = targetSelector.Items.Cast<CaptureTarget>().Single(item => item.ProcessId == probe.Id);
                    Require(!targetSelector.Items.Cast<CaptureTarget>().Any(item => item.ProcessId == Environment.ProcessId), "Process picker included its own dashboard.");
                    targetSelector.SelectedItem = manual;
                    Require(FindButton(window, "IgnoreCaptureButton").IsEnabled && !FindButton(window, "AllowCaptureButton").IsEnabled, "Dashboard capture-rule actions did not reflect the selected application.");
                    window.Activate();
                    await Task.Delay(400);
                    string manualPath = Path.Combine(outputDirectory, probeName + "-manual.json");
                    window.WriteDiagnostics(manualPath);
                    using JsonDocument manualState = JsonDocument.Parse(File.ReadAllText(manualPath));
                    Require(manualState.RootElement.GetProperty("TargetProcess").GetInt32() == probe.Id, "Manual capture did not stay pinned while editing the dashboard.");
                    Require(manualState.RootElement.GetProperty("ManualTarget").GetProperty("ProcessId").GetInt32() == probe.Id, "Manual selection was lost.");
                }
                finally { await probe.WaitForExitAsync(); }
                await Task.Delay(300);
                string closedPath = Path.Combine(outputDirectory, probeName + "-closed.json");
                window.WriteDiagnostics(closedPath);
                using JsonDocument closedState = JsonDocument.Parse(File.ReadAllText(closedPath));
                Require(closedState.RootElement.GetProperty("TargetProcess").GetInt32() == 0, "Closed manual target retained capture.");
                ComboBox selector = (ComboBox)window.FindName("CaptureTargetSelector");
                selector.SelectedIndex = 0;
                window.WriteDiagnostics(closedPath);
                using JsonDocument automaticState = JsonDocument.Parse(File.ReadAllText(closedPath));
                Require(automaticState.RootElement.GetProperty("ManualTarget").GetProperty("ProcessId").GetInt32() == 0, "Automatic capture was not restored.");
            }
            string beforeRestart = Path.Combine(outputDirectory, "before-restart.json");
            window.WriteDiagnostics(beforeRestart);
            using JsonDocument before = JsonDocument.Parse(File.ReadAllText(beforeRestart));
            string previousSession = before.RootElement.GetProperty("CaptureActivity").GetProperty("Session").GetString()!;
            ((Button)window.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(6500);
            string afterRestart = Path.Combine(outputDirectory, "after-restart.json");
            window.WriteDiagnostics(afterRestart);
            using JsonDocument after = JsonDocument.Parse(File.ReadAllText(afterRestart));
            Require(after.RootElement.GetProperty("CaptureActivity").GetProperty("Session").GetString() != previousSession, "Restart capture retained the old session.");
            Require(after.RootElement.GetProperty("CaptureActivity").GetProperty("TotalRows").GetInt64() > 0, "Restart capture did not receive real frames.");
            Require(!CaptureSessions.Names().Contains(previousSession), "Restart capture leaked its previous Windows trace session.");
            title.BeginAnimation(UIElement.OpacityProperty, null);
            File.WriteAllText(Path.Combine(outputDirectory, "capture.json"), JsonSerializer.Serialize(captured));
            await CheckGraphLifecycleAsync(captured.Points, outputDirectory);
            OverlayWindow overlay = new(Preferences.Initial);
            overlay.UpdateData(OverlayData.Build([], captured));
            DisplayInfo display = Desktop.Displays().Single(d => d.Primary);
            overlay.ShowOnMonitor(display.Bounds);
            overlay.Apply(Preferences.Initial with { Opacity = 0.4 }); overlay.UpdateLayout();
            Require(overlay.Topmost && overlay.ActualWidth < display.CanvasSize.Width, "Overlay must be topmost and cropped to content.");
            SaveImage(overlay, Path.Combine(outputDirectory, "overlay.png")); overlay.Close();
            window.Activate();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ComboBox capturePicker = (ComboBox)window.FindName("CaptureTargetSelector");
            window.RefreshCaptureTargets();
            capturePicker.IsDropDownOpen = true;
            await Task.Delay(200);
            Popup popup = (Popup)capturePicker.Template.FindName("PART_Popup", capturePicker);
            Require(Math.Abs(popup.Child.RenderSize.Width - capturePicker.ActualWidth) < 2, $"Capture popup width differs from its control: open={popup.IsOpen}, pickerOpen={capturePicker.IsDropDownOpen}, visible={popup.Child.IsVisible}, popup={popup.Child.RenderSize.Width:0.##}, control={capturePicker.ActualWidth:0.##}.");
            Point popupOrigin = popup.Child.PointToScreen(new Point());
            Point pickerOrigin = capturePicker.PointToScreen(new Point());
            Require(Math.Abs(popupOrigin.X - pickerOrigin.X) < 2, "Capture popup is offset outside its control.");
            capturePicker.IsDropDownOpen = false;
            ScrollViewer dashboard = (ScrollViewer)window.FindName("DashboardScroll");
            window.UpdateLayout();
            Require(window.Height <= SystemParameters.WorkArea.Height, "The startup window extends beyond the screen's usable height.");
            if (SystemParameters.WorkArea.Height >= 1120)
                Require(dashboard.ScrollableHeight < 1, $"The startup dashboard still clips its hardware cards: overflow={dashboard.ScrollableHeight:0.##}.");
            SaveImage(window, Path.Combine(outputDirectory, "dashboard.png"));
            ((TabControl)window.FindName("Pages")).SelectedIndex = 1;
            await Task.Delay(400);
            await CheckCustomPresetDropdownAsync(window, outputDirectory);
            ComboBox addMetric = (ComboBox)window.FindName("AddMetricSelector");
            ComboBox items = (ComboBox)window.FindName("SectionSelector");
            int originalCount = items.Items.Count;
            foreach (string metric in new[] { "usage", "temperature" })
            {
                addMetric.SelectedItem = addMetric.Items.Cast<MetricChoice>().Single(item => item.Kind == SectionKind.Gpu && item.Id == metric);
                FindButton(window, "AddSeparateMetricButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Require(items.Items.Count == originalCount + 2, "Separate metrics were not added.");
            SectionStyle separate = (SectionStyle)items.SelectedItem;
            Require(separate.Metrics.SequenceEqual(new[] { "temperature" }) && separate.Id.Length == 32, "Separate metric identity or data is wrong.");
            ((Slider)window.FindName("LabelSize")).Value = 31;
            OverlayCanvas itemCanvas = (OverlayCanvas)((ScrollViewer)window.FindName("PreviewViewport")).Content;
            Border itemPanel = itemCanvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == separate.Key);
            TextBlock metricText = Descendants(itemPanel).OfType<TextBlock>().Single(text => text.Inlines.OfType<System.Windows.Documents.Run>().Count() == 2);
            Require(metricText.Inlines.OfType<System.Windows.Documents.Run>().First().FontSize == 31 && metricText.FontSize == 20, "Label and value sizes are not independent.");
            itemCanvas.SelectRegion(new Rect(Canvas.GetLeft(itemPanel), Canvas.GetTop(itemPanel), itemPanel.DesiredSize.Width, itemPanel.DesiredSize.Height));
            Require(itemCanvas.Selection.Contains(separate.Key), "Separate metric could not be selected on the canvas.");
            FindButton(window, "RemoveOverlayItemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(items.Items.Count == originalCount + 1 && !itemCanvas.Children.OfType<Border>().Any(panel => (string)panel.Tag == separate.Key), "Removing a separate metric left its renderer behind.");
            ((TabControl)window.FindName("StudioInspectorTabs")).SelectedItem = ((TabControl)window.FindName("StudioInspectorTabs")).Items.Cast<TabItem>().Single(tab => AutomationProperties.GetAutomationId(tab) == "StudioArtworkTab");
            window.UpdateLayout();
            ComboBox artworkPicker = Descendants(window).OfType<ComboBox>().Single(picker => AutomationProperties.GetAutomationId(picker) == "RtssSpriteSelector");
            TextBlock artworkChoice = (TextBlock)(artworkPicker.ItemTemplate ?? throw new InvalidOperationException("RTSS artwork has no preview template.")).LoadContent();
            BitmapSource samplePreview = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
            artworkChoice.DataContext = new RtssSprite("Preview check", "", 2, 2, samplePreview);
            ToolTip artworkTip = (ToolTip)artworkChoice.ToolTip;
            artworkTip.PlacementTarget = artworkChoice;
            artworkTip.IsOpen = true;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Image artworkImage = ((StackPanel)artworkTip.Content).Children.OfType<Image>().Single();
            Require(artworkImage.Source == samplePreview, "Hovering over RTSS artwork did not bind its image preview.");
            artworkTip.IsOpen = false;
            Slider studioZoom = (Slider)window.FindName("ZoomSlider");
            studioZoom.Value = 100;
            studioZoom.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.PreviewMouseDoubleClickEvent });
            Require(studioZoom.Value < 100, "Double-clicking canvas zoom did not restore fit-to-canvas.");
            Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AddTextLayer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SectionStyle textLayer = (SectionStyle)items.SelectedItem;
            Require(textLayer.Kind == SectionKind.Artwork && textLayer.ImageData.Length == 0 && items.Items.Count == originalCount + 2, "A freeform text layer was not added.");
            ((TextBox)window.FindName("SectionName")).Text = "My custom caption";
            Border textPanel = itemCanvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == textLayer.Key);
            Require(textPanel.Child is TextBlock caption && caption.Text == "My custom caption", "Text layer edits did not reach the canvas.");
            Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "LayerBackward").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(((SectionStyle)items.SelectedItem).Key == textLayer.Key, "Reordering a layer changed the selected item.");
            FindButton(window, "RemoveOverlayItemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(items.Items.Count == originalCount + 1, "Removing an artwork layer left it in the layout.");
            Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AddColorPanel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SectionStyle colorPanel = (SectionStyle)items.SelectedItem;
            Border shape = itemCanvas.Children.OfType<Border>().Single(panel => (string)panel.Tag == colorPanel.Key);
            Require(colorPanel.ArtworkFill && shape.Width == 300 && shape.Height == 150 && shape.Background is SolidColorBrush, "A resizable color panel was not rendered.");
            Require(itemCanvas.SectionAt(shape) == colorPanel.Key, "The canvas did not identify the selected artwork.");
            ContextMenu itemMenu = window.CreateCanvasMenu(colorPanel.Key);
            itemMenu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "CanvasDuplicateItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            SectionStyle duplicate = (SectionStyle)items.SelectedItem;
            Require(duplicate.Key != colorPanel.Key && items.Items.Count == originalCount + 3, "The canvas menu did not duplicate the selected artwork.");
            itemMenu = window.CreateCanvasMenu(colorPanel.Key);
            itemMenu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "CanvasRemoveItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(items.Items.Count == originalCount + 2 && !itemCanvas.Children.OfType<Border>().Any(panel => (string)panel.Tag == colorPanel.Key), "The canvas menu did not remove the clicked artwork.");
            itemMenu = window.CreateCanvasMenu(duplicate.Key);
            itemMenu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "CanvasRemoveItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await CheckUnsavedLayoutAsync(window, outputDirectory);
            ComboBox presets = (ComboBox)window.FindName("PresetSelector");
            ((Slider)window.FindName("ZoomSlider")).Value = 100;
            foreach (string preset in LayoutPresets.Names)
            {
                presets.SelectedItem = presets.Items.Cast<LayoutPresets.Choice>().Single(item => item.Name == preset);
                Button apply = FindButton(window, "ApplyPresetButton");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FindButton(window, "FitItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                SaveImage(window, Path.Combine(outputDirectory, preset.Replace(" ", "-") + ".png"));
                OverlayCanvas presetCanvas = (OverlayCanvas)((ScrollViewer)window.FindName("PreviewViewport")).Content;
                foreach (double width in new[] { 1920d, 3440d })
                {
                    OverlayCanvas check = new() { Width = width, Height = 1080 };
                    check.Apply(LayoutPresets.Create(Preferences.Initial, preset));
                    check.Apply(check.ArrangePreset(preset));
                    Rect[] bounds = check.Children.Cast<Border>().Where(panel => panel.Visibility == Visibility.Visible)
                        .Select(panel => new Rect(Canvas.GetLeft(panel), Canvas.GetTop(panel), panel.DesiredSize.Width, panel.DesiredSize.Height)).ToArray();
                    Require(bounds.All(rect => rect.Left >= 0 && rect.Top >= 0 && rect.Right <= width && rect.Bottom <= 1080), "Preset exceeds display: " + preset);
                    for (int i = 0; i < bounds.Length; i++)
                        for (int j = i + 1; j < bounds.Length; j++)
                            Require(!bounds[i].IntersectsWith(bounds[j]), "Preset sections overlap: " + preset);
                }
                if (preset == "Signal panels")
                {
                    FindButton(window, "FitItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ScrollViewer itemViewport = (ScrollViewer)window.FindName("PreviewViewport");
                    Rect content = presetCanvas.VisibleBounds();
                    double scale = ((ScaleTransform)presetCanvas.LayoutTransform).ScaleX;
                    Require(content.Width * scale <= itemViewport.ActualWidth && content.Height * scale <= itemViewport.ActualHeight, "Fit items did not show the complete graphical layout.");
                }
            }
            presets.SelectedIndex = 0; FindButton(window, "ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ScrollViewer viewport = (ScrollViewer)window.FindName("PreviewViewport");
            OverlayCanvas canvas = (OverlayCanvas)viewport.Content;
            DisplayInfo selectedDisplay = (DisplayInfo)((ComboBox)window.FindName("DisplaySelector")).SelectedItem;
            Require(canvas.Width == selectedDisplay.CanvasSize.Width && canvas.Height == selectedDisplay.CanvasSize.Height, "Studio does not match monitor geometry.");
            ((Slider)window.FindName("ZoomSlider")).Value = 100;
            Require(((ScaleTransform)canvas.LayoutTransform).ScaleX == 1, "100% studio zoom failed.");
            await Task.Delay(200);
            window.Width = 1050; window.Height = 760;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            window.UpdateLayout();
            Button testControl = FindButton(window, "TestOverlayButton");
            double testControlBottom;
            ScrollViewer workspace = (ScrollViewer)window.FindName("StudioWorkspaceScroll");
            workspace.ScrollToVerticalOffset(workspace.ScrollableHeight);
            await Task.Delay(100);
            window.UpdateLayout();
            testControlBottom = testControl.TransformToAncestor(window).Transform(new Point(0, testControl.ActualHeight)).Y;
            Require(testControlBottom < window.ActualHeight - 20, $"Studio action buttons are clipped at minimum window size: button bottom={testControlBottom:0}, window height={window.ActualHeight:0}.");
            window.Width = 1280; window.Height = 940; window.UpdateLayout();
            workspace.ScrollToVerticalOffset(0);
            await Task.Delay(100);
            window.UpdateLayout();
            SaveImage(window, Path.Combine(outputDirectory, "studio.png"));
            await CheckLiveEditingAsync(window, outputDirectory);
            ((TabControl)window.FindName("Pages")).SelectedIndex = 1;
            ComboBox allItems = (ComboBox)window.FindName("SectionSelector");
            Button removeItem = FindButton(window, "RemoveOverlayItemButton");
            while (allItems.Items.Count > 0)
            {
                int previous = allItems.Items.Count;
                removeItem.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(allItems.Items.Count == previous - 1, "The final overlay item could not be removed.");
            }
            OverlayCanvas emptyCanvas = (OverlayCanvas)((ScrollViewer)window.FindName("PreviewViewport")).Content;
            Require(emptyCanvas.VisibleBounds().IsEmpty && emptyCanvas.Children.OfType<Border>().Count() == 0, "The empty layout still rendered an overlay item.");
            SaveImage(window, Path.Combine(outputDirectory, "empty-canvas.png"));
            Preferences emptyLayout = Preferences.Parse(JsonSerializer.Serialize(Preferences.Validate(Preferences.Initial with { Sections = [] })));
            Require(emptyLayout.Sections.IsEmpty, "An empty overlay did not survive saving and loading.");
            Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AddGameInfo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(allItems.Items.Count == 1 && ((SectionStyle)allItems.SelectedItem).Kind == SectionKind.Game, "A new game-info item could not be added after clearing the canvas.");
            emptyCanvas.UpdateGameInfo("PEAK", "DXGI", RunningApplications.Icon(Environment.ProcessId));
            Require(VisualText(emptyCanvas).Contains("PEAK") && VisualText(emptyCanvas).Contains("DXGI"), "The game name or verified presentation API was not shown.");
            window.UpdateLayout();
            Border gamePanel = emptyCanvas.Children.OfType<Border>().Single();
            Require(gamePanel.Visibility == Visibility.Visible && gamePanel.DesiredSize.Width > 0 && !emptyCanvas.VisibleBounds().IsEmpty, "The game-info item did not become visible on the canvas.");
            SaveImage(window, Path.Combine(outputDirectory, "game-info-layer.png"));
            ((TabControl)window.FindName("Pages")).SelectedIndex = 2;
            await Task.Delay(150);
            SaveImage(window, Path.Combine(outputDirectory, "settings.png"));
            ScrollViewer settingsPage = (ScrollViewer)((TabControl)window.FindName("Pages")).SelectedContent;
            settingsPage.ScrollToVerticalOffset(settingsPage.ScrollableHeight);
            await Task.Delay(150);
            Require(FindButton(window, "ReportProblemButton").IsVisible && FindButton(window, "SendFeedbackButton").IsVisible, "The feedback actions are missing from Settings.");
            SaveImage(window, Path.Combine(outputDirectory, "settings-bottom.png"));
            ((TabControl)window.FindName("Pages")).SelectedIndex = 3;
            await Task.Delay(150);
            SaveImage(window, Path.Combine(outputDirectory, "about.png"));
            Preferences.Write(Preferences.Initial, Path.Combine(outputDirectory, "layout.json"));
            Require(Preferences.Parse(File.ReadAllText(Path.Combine(outputDirectory, "layout.json"))).Sections.Length == 4, "Layout round trip failed.");
            await CloseAsync(window); window = null;
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), $"PASS: frame calculations, stale data, swap-chain isolation, real sensors, real ETW capture ({captured.AppFps:0.0} FPS), capture restart with fresh frames and released old session, two real renderer processes, manual selection while dashboard is active, process exit and return to Automatic, {observedSamples}/40 nonempty FPS snapshots (occluded windows may stop rendering), {foregroundChecks} foreground routing checks (focus-dependent), ignore rules, custom labels, {LayoutPresets.Names.Length} presets with table/tile/hero styling round trips and non-overlap checks at 1920/3440 widths, average/1% lows, monitor-sized canvas, 100% zoom, cropped overlay, marquee/group movement, grid snapping and Ctrl bypass, saved overlay scaling, bounded popup placement, named preset save/replace/reload, separate metric add/remove, independent label sizes, schema migration, dynamic technology fields, minimum-size studio controls, UI renders, clean shutdown.\nPEAK/OptiScaler, Cyberpunk, AFMF, NVIDIA hardware, and exclusive fullscreen have not been validated.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "FAIL\n" + error);
            if (window is not null) await CloseAsync(window);
            Environment.ExitCode = 1;
        }
    }
    private static async Task CheckGraphLifecycleAsync(ImmutableArray<FramePoint> samples, string outputDirectory)
    {
        Require(!samples.IsEmpty, "Graph lifecycle testing requires real captured frame samples.");
        FrameGraph graph = new();
        Window host = new() { Title = "Frame Trace graph check", Width = 480, Height = 200, ShowActivated = false, Content = graph };
        host.Show();
        try
        {
            Require(!graph.IsAnimating, "An empty graph is still requesting redraws.");
            graph.SetSamples(samples);
            Require(graph.IsAnimating, "A visible graph with real frames did not start drawing.");
            await Task.Delay(100);
            SaveImage(host, Path.Combine(outputDirectory, "frame-graph.png"));
            host.WindowState = WindowState.Minimized;
            Require(!graph.IsAnimating, "The graph is still subscribed to rendering while minimized.");
            host.WindowState = WindowState.Normal;
            Require(graph.IsAnimating, "The graph did not resume after restoring.");
            graph.Visibility = Visibility.Collapsed;
            Require(!graph.IsAnimating, "A hidden graph is still requesting redraws.");
            graph.Visibility = Visibility.Visible;
            Require(graph.IsAnimating, "The graph did not resume after becoming visible.");
            graph.SetSamples([]);
            Require(!graph.IsAnimating, "The graph did not stop redrawing when samples were cleared.");
        }
        finally { host.Close(); }
        Require(!graph.IsAnimating, "Closing the graph window left a rendering subscription active.");
    }

    public static async Task RunUpdateAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        MainWindow window = new(); Application.Current.MainWindow = window;
        window.Show();
        try
        {
            await Task.Delay(1500);
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
            UpdateCheckResult result = await UpdateChecker.CheckAsync(new Version(0, 0, 0), timeout.Token);
            Require(result.Availability == UpdateAvailability.Available && result.Installer is not null, "The real GitHub release has no verified installer.");
            window.ShowAvailableUpdate(result);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Border banner = (Border)window.FindName("UpdateBanner");
            Require(banner.ActualWidth <= 640 && banner.ActualWidth < window.ActualWidth - 200 && banner.ActualHeight <= 64, "The update notice is not compact.");
            SaveImage(window, Path.Combine(outputDirectory, "update-available.png"));
            FindButton(window, "DismissUpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(banner.Visibility == Visibility.Collapsed, "Not now did not dismiss the update notice.");
            Require(!Directory.Exists(Path.Combine(outputDirectory, "download")), "Dismissing created an installer download.");
            window.ShowAvailableUpdate(result);
            ProgressBar progress = (ProgressBar)window.FindName("UpdateDownloadProgress");
            bool sawProgress = false;
            progress.ValueChanged += (_, e) =>
            {
                if (sawProgress || e.NewValue < 8 || e.NewValue >= 100) return;
                sawProgress = true;
                window.UpdateLayout();
                SaveImage(window, Path.Combine(outputDirectory, "update-downloading.png"));
            };
            string installer = await window.DownloadUpdateAsync(Path.Combine(outputDirectory, "download"), timeout.Token);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Require(sawProgress && progress.Value == 100, "A real installer download did not show percentage progress and completion.");
            Require(!File.Exists(installer + ".part"), "A partial download remained after verification.");
            await UpdateChecker.VerifyInstallerAsync(installer, result.Installer!, timeout.Token);
            SaveImage(window, Path.Combine(outputDirectory, "update-verified.png"));
            await using (FileStream altered = File.Open(installer, FileMode.Open, FileAccess.Write, FileShare.None))
                altered.WriteByte(0);
            try
            {
                await UpdateChecker.VerifyInstallerAsync(installer, result.Installer!, timeout.Token);
                throw new InvalidOperationException("The updater accepted a modified installer.");
            }
            catch (InvalidDataException) { }
            File.Delete(installer);
            using CancellationTokenSource canceled = new();
            string canceledDirectory = Path.Combine(outputDirectory, "canceled");
            Progress<int> cancelProgress = new(percent => { if (percent >= 1) canceled.Cancel(); });
            try
            {
                await UpdateChecker.DownloadAsync(result.Installer!, canceledDirectory, cancelProgress, canceled.Token);
                throw new InvalidOperationException("The updater ignored download cancellation.");
            }
            catch (OperationCanceledException) { }
            Require(!Directory.EnumerateFiles(canceledDirectory).Any(), "A canceled download left an installer or partial file behind.");
            canceled.Cancel();
            try { await window.DownloadUpdateAsync(Path.Combine(outputDirectory, "already-canceled"), canceled.Token); }
            catch (OperationCanceledException) { }
            Require(FindButton(window, "CheckForUpdatesButton").IsEnabled && FindButton(window, "InstallUpdateButton").IsEnabled, "Update controls were not restored after cancellation.");
            Require((await UpdateChecker.CheckAsync(typeof(App).Assembly.GetName().Version!, timeout.Token)).Availability == UpdateAvailability.UpToDate, "The current app version was offered an older release.");
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "PASS: real GitHub release and installer download, compact banner, Not now dismissal without downloading, percentage progress, SHA-256 verification, rejection of a modified installer, cancellation during streaming with partial-file cleanup, restored controls, and no downgrade offered. The installer was not executed.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "FAIL\n" + error);
            Environment.ExitCode = 1;
        }
        finally { await CloseAsync(window); }
    }

    public static async Task RunStudioAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        MainWindow window = new(); Application.Current.MainWindow = window;
        window.Show();
        try
        {
            await Task.Delay(2000);
            ((TabControl)window.FindName("Pages")).SelectedIndex = 1;
            await Task.Delay(200);
            ComboBox preset = (ComboBox)window.FindName("PresetSelector");
            preset.SelectedItem = preset.Items.Cast<LayoutPresets.Choice>().Single(item => item.Name == "Classic RTSS");
            FindButton(window, "ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CheckUsageBar(outputDirectory);
            CheckSignalPanels(outputDirectory);
            await CheckLiveEditingAsync(window, outputDirectory);
            CheckThemeWidgetsEditor(window, outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "PASS: live test window, overlay visibility while editing, label updates before saving, closing and reopening, Stop test, restoring saved layout, and preserving preferences. FPS capture is verified separately.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "FAIL\n" + error);
            Environment.ExitCode = 1;
        }
        finally { await CloseAsync(window); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void CheckSavedPresets(string directory)
    {
        string path = Path.Combine(directory, "saved-layouts-" + Guid.NewGuid().ToString("N") + ".json");
        SectionStyle metric = Preferences.Initial.Sections[1] with { Id = Guid.NewGuid().ToString("N"), Metrics = ["temperature"], LabelSize = 31, X = 0.7 };
        Preferences custom = Preferences.Initial with { OverlayScale = 1.5, Sections = Preferences.Initial.Sections.Add(metric) };
        SavedLayouts.Save("My custom layout", custom, path);
        SavedLayouts.Save("Second layout", Preferences.Initial, path);
        SavedLayouts.Save("My custom layout", custom with { Opacity = 0.3 }, path);
        ImmutableArray<NamedLayout> saved = SavedLayouts.Read(path);
        Require(saved.Length == 2, "Updating a named preset created a duplicate.");
        Preferences current = Preferences.Initial with { Shortcut = new Hotkey(3, 0x4B) };
        Preferences restored = SavedLayouts.Apply(current, saved.Single(item => item.Name == "My custom layout"));
        Require(restored.Sections.Length == 5 && restored.Sections.Last().Key == metric.Key && restored.Sections.Last().LabelSize == 31 && restored.OverlayScale == 1.5 && restored.Opacity == 0.3 && restored.Shortcut == current.Shortcut, "Saved preset lost customization or changed the user's shortcut.");
        SavedLayouts.Delete("MY CUSTOM LAYOUT", path);
        ImmutableArray<NamedLayout> remaining = SavedLayouts.Read(path);
        Require(remaining.Length == 1 && JsonSerializer.Serialize(remaining[0]) == JsonSerializer.Serialize(saved.Single(item => item.Name == "Second layout")), "Deleting a preset did not persist or changed another saved preset.");
        try { SavedLayouts.Delete("My custom layout", path); throw new InvalidOperationException("Deleting a missing preset was accepted."); }
        catch (KeyNotFoundException) { }
        SavedLayouts.Delete("Second layout", path);
        Require(SavedLayouts.Read(path).IsEmpty, "Deleting the final custom preset did not leave a valid empty preset list.");
        SavedLayouts.Save("Replacement layout", custom, path);
        Require(SavedLayouts.Read(path) is [{ Name: "Replacement layout" }], "A custom preset could not be saved after deleting all presets.");
        Preferences migrated = Preferences.Parse(JsonSerializer.Serialize(Preferences.Initial with { SchemaVersion = 3 }));
        Require(migrated.SchemaVersion == 4 && migrated.Sections.Length == 4, "Existing layouts did not migrate.");
        string stablePath = Path.Combine(directory, "stable", "saved-layouts.json");
        string previewPath = Path.Combine(directory, "preview", "saved-layouts.json");
        SavedLayouts.Save("OG", Preferences.Initial, stablePath);
        string stableContents = File.ReadAllText(stablePath);
        ImmutableArray<NamedLayout> previewLayouts = SavedLayouts.ReadPreview(previewPath, stablePath);
        Require(previewLayouts is [{ Name: "OG" }] && SavedLayouts.Read(previewPath).Single().Name == "OG", "Preview did not import the existing stable custom preset.");
        Require(File.ReadAllText(stablePath) == stableContents, "Importing a preset changed the stable profile file.");
    }
    private static void CheckDataMigration(string directory)
    {
        string root = Path.Combine(directory, "migration", Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "Frameglass"), current = Path.Combine(root, "FrameTrace");
        Preferences.Write(Preferences.Initial with { StartMinimized = true, SensorRefreshMs = 250 }, Path.Combine(legacy, "preferences.json"));
        SavedLayouts.Save("OG", Preferences.Initial with { OverlayScale = 1.25 }, Path.Combine(legacy, "saved-layouts.json"));
        File.WriteAllText(Path.Combine(legacy, "diagnostics.jsonl"), "retained diagnostic data");
        byte[] preferences = File.ReadAllBytes(Path.Combine(legacy, "preferences.json"));
        byte[] presets = File.ReadAllBytes(Path.Combine(legacy, "saved-layouts.json"));
        DataMigration.MoveLegacyDirectory(legacy, current);
        Require(!Directory.Exists(legacy) && File.ReadAllBytes(Path.Combine(current, "preferences.json")).SequenceEqual(preferences) && File.ReadAllBytes(Path.Combine(current, "saved-layouts.json")).SequenceEqual(presets), "Data migration changed preferences or custom presets.");
        Require(Preferences.Parse(File.ReadAllText(Path.Combine(current, "preferences.json"))).SensorRefreshMs == 250 && SavedLayouts.Read(Path.Combine(current, "saved-layouts.json")) is [{ Name: "OG" }] && File.ReadAllText(Path.Combine(current, "diagnostics.jsonl")) == "retained diagnostic data", "Migrated settings, presets, or diagnostics could not be read.");
        DataMigration.MoveLegacyDirectory(legacy, current);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "preferences.json"), "conflicting legacy data");
        try { DataMigration.MoveLegacyDirectory(legacy, current); throw new InvalidOperationException("Conflicting data folders were overwritten."); }
        catch (IOException error) when (error.Message.StartsWith("Cannot migrate local data", StringComparison.Ordinal)) { }
        Require(File.ReadAllText(Path.Combine(legacy, "preferences.json")) == "conflicting legacy data" && File.ReadAllBytes(Path.Combine(current, "preferences.json")).SequenceEqual(preferences), "Migration conflict handling changed either data folder.");
    }

    private static async Task CheckUnsavedLayoutAsync(MainWindow window, string directory)
    {
        ComboBox section = (ComboBox)window.FindName("SectionSelector");
        section.SelectedIndex = 0;
        TextBox name = (TextBox)window.FindName("SectionName");
        TextBlock notice = (TextBlock)window.FindName("UnsavedLayoutNotice");
        name.Text = "KEEP MY EDITS";
        Require(notice.Visibility == Visibility.Visible, "Editing a layout did not show the unsaved indicator.");
        FindButton(window, "UndoStudioButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(name.Text != "KEEP MY EDITS", "Undo did not restore the preceding overlay layout.");
        name.Text = "KEEP MY EDITS";
        ComboBox presets = (ComboBox)window.FindName("PresetSelector");
        presets.SelectedItem = presets.Items.Cast<LayoutPresets.Choice>().Single(item => item.Name == "Classic RTSS");
        Task keep = RespondToLayoutPromptAsync("7");
        FindButton(window, "ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await keep;
        Require(name.Text == "KEEP MY EDITS" && notice.Visibility == Visibility.Visible, "Declining preset replacement lost the current edits.");
        SaveImage(window, Path.Combine(directory, "unsaved-layout.png"));
        Task discard = RespondToLayoutPromptAsync("6");
        FindButton(window, "ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await discard;
        Require(name.Text != "KEEP MY EDITS" && notice.Visibility == Visibility.Collapsed, "Confirming preset replacement did not load a clean layout.");
        name.Text = "KEEP CUSTOM EDITS";
        Task keepCustom = RespondToLayoutPromptAsync("7");
        FindButton(window, "LoadCustomPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await keepCustom;
        Require(name.Text == "KEEP CUSTOM EDITS", "Declining a saved preset replacement lost the current edits.");
        Task discardCustom = RespondToLayoutPromptAsync("6");
        FindButton(window, "LoadCustomPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await discardCustom;
        Require(notice.Visibility == Visibility.Collapsed && ((TextBox)window.FindName("CustomPresetName")).Text == "OG", "Loading a saved preset did not establish a clean layout.");
    }

    private static Task RespondToLayoutPromptAsync(string buttonId) => Task.Run(async () =>
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            AutomationElement? dialog = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId),
                new PropertyCondition(AutomationElement.ClassNameProperty, "#32770")));
            AutomationElement? button = dialog?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, buttonId));
            if (button is not null)
            {
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"The unsaved-layout dialog did not expose button ID {buttonId}.");
    });
    private static async Task CheckCustomPresetDropdownAsync(MainWindow window, string directory)
    {
        TabControl inspector = (TabControl)window.FindName("StudioInspectorTabs");
        inspector.SelectedItem = inspector.Items.Cast<TabItem>().Single(item => AutomationProperties.GetAutomationId(item) == "StudioLayoutsTab");
        window.UpdateLayout();
        ComboBox selector = (ComboBox)window.FindName("CustomPresetSelector");
        Button delete = (Button)window.FindName("DeleteCustomPresetButton");
        Button load = (Button)window.FindName("LoadCustomPresetButton");
        selector.SelectedIndex = -1;
        Require(!delete.IsEnabled && !load.IsEnabled, "Saved preset actions are enabled without a selection.");
        ImmutableArray<NamedLayout> presets = [new("OG", Preferences.Initial)];
        SavedLayouts.Save("OG", presets[0].Layout, Path.Combine(directory, "saved-layouts-ui.json"));
        selector.ItemsSource = presets;
        selector.SelectedIndex = 0;
        Require(delete.IsEnabled && load.IsEnabled, "Saved preset actions are unavailable after selecting a custom preset.");
        selector.IsDropDownOpen = true;
        await Task.Delay(150);
        Popup popup = (Popup)selector.Template.FindName("PART_Popup", selector);
        Require(selector.ItemTemplate is not null && popup.Child is FrameworkElement { ActualHeight: >= 64, ActualWidth: >= 220 }, "Saved preset popup did not open at a selectable size.");
        string[] labels = VisualText(popup.Child).ToArray();
        Require(labels.Contains("OG"), "Saved preset popup did not show the saved name.");
        selector.SelectedIndex = 0;
        Require(selector.SelectedItem is NamedLayout { Name: "OG" }, "Saved preset could not be selected from the popup list.");
        selector.IsDropDownOpen = false;
    }
    private static void CheckCanvasEditing()
    {
        OverlayCanvas canvas = new() { Width = 3440, Height = 1440 };
        canvas.EnableEditing(); canvas.Apply(Preferences.Initial);
        Border[] panels = canvas.Children.OfType<Border>().ToArray();
        Border first = panels[0];
        Require(canvas.SectionAt(first) == (string)first.Tag, "Canvas hit testing missed the selected item.");
        canvas.SelectItem((string)first.Tag);
        Require(canvas.Selection.SequenceEqual([(string)first.Tag]), "Right-click selection did not isolate its item.");
        canvas.SelectRegion(new Rect(Canvas.GetLeft(first), Canvas.GetTop(first), first.DesiredSize.Width, first.DesiredSize.Height));
        Require(canvas.Selection.Contains((string)first.Tag), "Marquee did not select its section.");
        canvas.SelectAll(); Require(canvas.Selection.Length == 4, "Select all missed sections.");
        Point[] before = panels.Select(panel => new Point(Canvas.GetLeft(panel), Canvas.GetTop(panel))).ToArray();
        canvas.BeginSelectionMove(); canvas.DragSelection(new Vector(33, 25), ModifierKeys.Control);
        for (int i = 0; i < panels.Length; i++)
            Require((new Point(Canvas.GetLeft(panels[i]), Canvas.GetTop(panels[i])) - before[i] - new Vector(33, 25)).Length < 0.01, "Group movement changed relative spacing or ignored Ctrl free movement.");
        canvas.BeginSelectionMove(); canvas.DragSelection(new Vector(11, 11), ModifierKeys.None);
        Rect bounds = canvas.VisibleBounds();
        Require(Math.Abs(bounds.X / 16 - Math.Round(bounds.X / 16)) < 0.001 && Math.Abs(bounds.Y / 16 - Math.Round(bounds.Y / 16)) < 0.001, "Group did not snap to the grid.");
        canvas.BeginSelectionMove(); canvas.DragSelection(new Vector(-10000, -10000), ModifierKeys.Control);
        Require(canvas.VisibleBounds().X >= 0 && canvas.VisibleBounds().Y >= 0, "Group escaped the canvas.");
        canvas.Apply(Preferences.Initial);
        Size normal = first.DesiredSize;
        Preferences enlarged = Preferences.Initial with { OverlayScale = 2 };
        Require(Preferences.Parse(JsonSerializer.Serialize(enlarged)).OverlayScale == 2, "Overlay size did not survive saving.");
        canvas.Apply(enlarged);
        OverlayCanvas compact = new() { Width = 3440, Height = 1440 };
        compact.Apply(LayoutPresets.Create(Preferences.Initial, "Classic RTSS"));
        compact.Apply(compact.ArrangePreset("Classic RTSS"));
        Rect compactBounds = compact.VisibleBounds();
        compact.Apply(compact.ResizeOverlay(2));
        Require(Math.Abs(compact.VisibleBounds().Height / compactBounds.Height - 2) < 0.01 && Math.Abs(compact.VisibleBounds().Width / compactBounds.Width - 2) < 0.01, "Whole-overlay resizing did not preserve layout proportions.");
        OverlayCanvas technology = new();
        Preferences technologyLayout = Preferences.Initial with { Sections = [Preferences.Initial.Sections[0] with { ShowName = false, Metrics = ["generation"] }] };
        technology.Apply(technologyLayout);
        Require(technology.VisibleBounds().IsEmpty, "Unverified technology should be hidden in the live overlay.");
        technology.UpdateData(OverlayData.Build([], FrameMetrics.Summarize([], 0) with { Generation = "AFMF 2.1" }));
        Require(!technology.VisibleBounds().IsEmpty, "Verified generation did not appear.");
        technology.UpdateData(OverlayData.Build([], FrameMetrics.Summarize([], 0)));
        Require(technology.VisibleBounds().IsEmpty, "Stale generation did not hide again.");
        Require(Math.Abs(first.DesiredSize.Width / normal.Width - 2) < 0.01 && Math.Abs(first.DesiredSize.Height / normal.Height - 2) < 0.01, "Overlay size did not scale real content.");
    }
    private static void CheckSignalPanels(string outputDirectory)
    {
        Preferences layout = Preferences.Validate(LayoutPresets.Create(Preferences.Initial, "Signal panels"));
        OverlayCanvas canvas = new() { Width = 1920, Height = 1080 };
        canvas.Apply(layout);
        canvas.Apply(canvas.ArrangePreset("Signal panels"));
        ImmutableArray<SensorReading> sensors =
        [
            new("Test GPU", "GpuAmd", "GPU Core", "Load", 30, "%"),
            new("Test GPU", "GpuAmd", "GPU Core", "Temperature", 62, "°C"),
            new("Test GPU", "GpuAmd", "GPU Package", "Power", 158, "W"),
            new("Test GPU", "GpuAmd", "GPU Core", "Clock", 2440, "MHz"),
            new("Test GPU", "GpuAmd", "GPU Memory Used", "SmallData", 6144, "MB"),
            new("Test CPU", "Cpu", "CPU Total", "Load", 48, "%"),
            new("Test CPU", "Cpu", "Core (Tctl/Tdie)", "Temperature", 66, "°C"),
            new("Test CPU", "Cpu", "Package", "Power", 82, "W"),
            new("Total Memory", "Memory", "Memory Used", "Data", 17.2, "GB")
        ];
        FrameSummary frames = new(142, 142, 7.0, "Unavailable", "", []) { AverageFps = 136, LowFps = 91 };
        canvas.UpdateData(OverlayData.Build(sensors, frames));
        canvas.UpdateData(OverlayData.Build(sensors.SetItem(0, sensors[0] with { Value = 75 }).SetItem(5, sensors[5] with { Value = 56 }), frames));
        System.Windows.Shapes.Polyline gpuTrend = Descendants(canvas).OfType<System.Windows.Shapes.Polyline>().Single(item => AutomationProperties.GetAutomationId(item) == "UsageTrend_Gpu");
        Require(gpuTrend.Points.Count == 2 && gpuTrend.Points[1].Y < gpuTrend.Points[0].Y, "The graphical GPU trace did not reflect changing sensor load.");
        double end = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency - 2000;
        canvas.Apply(canvas.ArrangePreset("Signal panels"));
        canvas.UpdateGraph(Enumerable.Range(0, 50).Select(index => new FramePoint(end - 9800 + index * 200, 7 + Math.Sin(index * 0.6) * 2)).ToImmutableArray());
        Rect[] cards = canvas.Children.OfType<Border>().Where(panel => panel.Visibility == Visibility.Visible).Select(panel => new Rect(Canvas.GetLeft(panel), Canvas.GetTop(panel), panel.DesiredSize.Width, panel.DesiredSize.Height)).ToArray();
        Require(cards.Length == 4 && cards.All(card => card.Right <= canvas.Width && card.Bottom <= canvas.Height), "Graphical cards exceed the display.");
        for (int i = 0; i < cards.Length; i++)
            for (int j = i + 1; j < cards.Length; j++)
                Require(!cards[i].IntersectsWith(cards[j]), "Graphical cards overlap.");
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        SavePresetImage(canvas, Path.Combine(outputDirectory, "Signal-panels-live-sample.png"));
    }
    private static void CheckUsageBar(string outputDirectory)
    {
        SectionStyle gpu = Preferences.Initial.Sections.Single(section => section.Kind == SectionKind.Gpu) with
        {
            UsageBar = true, UsageBarWidth = 220, ShowName = false, Metrics = []
        };
        Preferences layout = Preferences.Initial with { Sections = [gpu] };
        Require(Preferences.Parse(JsonSerializer.Serialize(layout)).Sections[0].UsageBarWidth == 220, "Live bar settings did not survive a saved layout.");
        OverlayCanvas canvas = new(); canvas.Apply(layout);
        Require(canvas.VisibleBounds().IsEmpty, "Live bar showed a fabricated reading without a sensor.");
        ImmutableArray<SensorReading> readings = [new("Test GPU", "GpuAmd", "GPU Core", "Load", 65, "%")];
        canvas.UpdateData(OverlayData.Build(readings, FrameMetrics.Summarize([], 0)));
        Border track = Descendants(canvas).OfType<Border>().Single(item => (string?)item.Tag == "UsageBar:" + gpu.Key);
        Border fill = (Border)track.Child;
        Require(Math.Abs(fill.Width - 143) < 0.01 && !canvas.VisibleBounds().IsEmpty, "Live bar did not render 65% from the actual GPU load reading.");
        canvas.Apply(layout with { Sections = [gpu with { ShowName = true, Metrics = ["usage"] }] });
        canvas.UpdateData(OverlayData.Build(readings, FrameMetrics.Summarize([], 0)));
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        SavePresetImage(canvas, Path.Combine(outputDirectory, "live-usage-bar.png"));
        SectionStyle withFan = gpu with { ShowName = true, Metrics = ["usage"], FanWidget = true, FanSize = 50 };
        Require(Preferences.Parse(JsonSerializer.Serialize(layout with { Sections = [withFan] })).Sections[0].FanWidget, "Animated fan setting did not survive a saved layout.");
        OverlayWindow live = new(layout with { Sections = [withFan] });
        live.UpdateData(OverlayData.Build([readings[0], new("Test GPU", "GpuAmd", "GPU Fan", "Fan", 1200, "RPM")], FrameMetrics.Summarize([], 0)));
        Border livePanel = live.Surface.Children.OfType<Border>().Single();
        Border liveTrack = Descendants(livePanel).OfType<Border>().Single(item => (string?)item.Tag == "UsageBar:" + withFan.Key);
        TextBlock liveFanReading = Descendants(livePanel).OfType<TextBlock>().Single(text => text.Text.StartsWith("FAN  ", StringComparison.Ordinal));
        live.ShowOnMonitor(Desktop.Displays().Single(display => display.Primary).Bounds);
        live.UpdateLayout();
        StackPanel liveWidget = (StackPanel)liveFanReading.Parent;
        Require(liveFanReading.Text == "FAN  1200 RPM" && liveWidget.DesiredSize.Width >= 50 + liveFanReading.DesiredSize.Width && livePanel.DesiredSize.Width >= liveTrack.Width + livePanel.Padding.Left + livePanel.Padding.Right && live.ActualWidth >= livePanel.DesiredSize.Width, "The live overlay clipped its usage bar or fan reading.");
        SaveImage(live, Path.Combine(outputDirectory, "live-fan.png"));
        live.Close();
        canvas.Apply(layout);
        canvas.UpdateData(OverlayData.Build([], FrameMetrics.Summarize([], 0)));
        Require(canvas.VisibleBounds().IsEmpty, "Live bar did not hide when its sensor became unavailable.");
        try { Preferences.Validate(layout with { Sections = [gpu with { UsageBarWidth = 501 }] }); throw new InvalidDataException("Invalid live bar width was accepted."); }
        catch (InvalidDataException error) when (error.Message.StartsWith("Live usage bars", StringComparison.Ordinal)) { }
        try { Preferences.Validate(layout with { Sections = [Preferences.Initial.Sections.Single(section => section.Kind == SectionKind.Cpu) with { FanWidget = true }] }); throw new InvalidDataException("Animated fan was accepted on a CPU item."); }
        catch (InvalidDataException error) when (error.Message.StartsWith("Animated fans", StringComparison.Ordinal)) { }
    }
    private static void CheckThemeWidgetsEditor(MainWindow window, string outputDirectory)
    {
        TabControl inspector = (TabControl)window.FindName("StudioInspectorTabs");
        ComboBox sections = (ComboBox)window.FindName("SectionSelector");
        sections.SelectedItem = sections.Items.Cast<SectionStyle>().First(item => item.Kind == SectionKind.Gpu);
        foreach ((string tabId, string controlId) in new[]
        {
            ("StudioItemTab", "SectionName"), ("StudioMetricsTab", "AddMetricSelector"),
            ("StudioVisualTab", "AnimatedFan"), ("StudioCanvasTab", "OverlaySize"), ("StudioLayoutsTab", "PresetSelector")
        })
        {
            inspector.SelectedItem = inspector.Items.Cast<TabItem>().Single(item => AutomationProperties.GetAutomationId(item) == tabId);
            window.UpdateLayout();
            Require(Descendants(window).OfType<FrameworkElement>().Any(item => item.IsVisible && (item.Name == controlId || AutomationProperties.GetAutomationId(item) == controlId)), "An Overlay Studio settings tab hid its controls: " + tabId);
        }
        inspector.SelectedItem = inspector.Items.Cast<TabItem>().Single(item => AutomationProperties.GetAutomationId(item) == "StudioVisualTab");
        window.UpdateLayout();
        CheckBox themeCard = Descendants(window).OfType<CheckBox>().Single(item => AutomationProperties.GetAutomationId(item) == "ThemeCard");
        CheckBox usageGauge = Descendants(window).OfType<CheckBox>().Single(item => AutomationProperties.GetAutomationId(item) == "LiveUsageGauge");
        themeCard.IsChecked = true; themeCard.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        usageGauge.IsChecked = true; usageGauge.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        CheckBox toggle = Descendants(window).OfType<CheckBox>().Single(item => AutomationProperties.GetAutomationId(item) == "LiveUsageBar");
        Slider width = Descendants(window).OfType<Slider>().Single(item => AutomationProperties.GetAutomationId(item) == "LiveUsageBarWidth");
        Require(toggle.Visibility == Visibility.Visible, "The live bar control did not appear for the GPU item.");
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        width.Value = 300;
        OverlayCanvas canvas = (OverlayCanvas)((ScrollViewer)window.FindName("PreviewViewport")).Content;
        Border themedPanel = canvas.Children.OfType<Border>().Single(item => (string)item.Tag == ((SectionStyle)sections.SelectedItem).Key);
        Require(themedPanel.CornerRadius.TopLeft == 12 && Descendants(themedPanel).OfType<Grid>().Any(item => AutomationProperties.GetAutomationId(item) == "UsageGauge_" + ((SectionStyle)sections.SelectedItem).Key), "The themed card and live gauge did not appear in Studio.");
        Border track = Descendants(canvas).OfType<Border>().Single(item => (string?)item.Tag == "UsageBar:" + ((SectionStyle)sections.SelectedItem).Key);
        Require(track.Width == 300 && track.Visibility == Visibility.Visible, "The width control did not update the visible Studio bar.");
        width.Value = 500;
        Require(themedPanel.Width >= 534, "A wide live bar extends beyond its graphical card.");
        width.Value = 300;
        CheckBox fanToggle = Descendants(window).OfType<CheckBox>().Single(item => AutomationProperties.GetAutomationId(item) == "AnimatedFan");
        Slider fanSize = Descendants(window).OfType<Slider>().Single(item => AutomationProperties.GetAutomationId(item) == "AnimatedFanSize");
        Require(fanSize.Template.FindName("PART_Track", fanSize) is Track { Thumb: not null }, "The themed slider cannot be dragged.");
        TextBlock fanState = Descendants(window).OfType<TextBlock>().Single(item => AutomationProperties.GetAutomationId(item) == "FanStatus");
        Require(fanState.Text.Contains("RPM", StringComparison.Ordinal) || fanState.Text.Contains("unavailable", StringComparison.Ordinal), "The fan control does not explain why its animation is stopped.");
        fanToggle.IsChecked = true; fanToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        fanSize.Value = 50;
        string key = ((SectionStyle)sections.SelectedItem).Key;
        StackPanel widget = Descendants(canvas).OfType<StackPanel>().Single(item => AutomationProperties.GetAutomationId(item) == "FanWidget_" + key);
        Canvas blades = Descendants(widget).OfType<Canvas>().Single(item => AutomationProperties.GetAutomationId(item) == "FanBlades_" + key);
        Require(widget.Children.OfType<Viewbox>().Single().Width == 50, "The fan size control did not update the Studio widget.");
        ImmutableArray<SensorReading> running = [new("Test GPU", "GpuAmd", "GPU Core", "Load", 65, "%"), new("Test GPU", "GpuAmd", "GPU Fan", "Fan", 1200, "RPM")];
        canvas.UpdateData(OverlayData.Build(running, FrameMetrics.Summarize([], 0)));
        Grid gauge = Descendants(themedPanel).OfType<Grid>().Single(item => AutomationProperties.GetAutomationId(item) == "UsageGauge_" + key);
        Require(VisualText(gauge).Contains("65%") && gauge.Children.OfType<System.Windows.Shapes.Path>().Single().Data != Geometry.Empty, "The gauge did not follow the GPU load reading.");
        Require(VisualText(widget).Contains("FAN  1200 RPM") && ((RotateTransform)blades.RenderTransform).HasAnimatedProperties, "The GPU fan did not animate from its reported speed.");
        window.UpdateLayout();
        SaveImage(window, Path.Combine(outputDirectory, "live-fan-in-studio.png"));
        canvas.UpdateData(OverlayData.Build([running[0], running[1] with { Value = 0 }], FrameMetrics.Summarize([], 0)));
        Require(!((RotateTransform)blades.RenderTransform).HasAnimatedProperties, "The GPU fan kept moving at zero RPM.");
    }
    private static void CheckTableLabelStability()
    {
        Rect gameDisplay = new(0, 0, 2560, 1440);
        Rect secondDisplay = new(2560, 0, 1920, 1080);
        Require(MainWindow.SelectOverlayMonitor(gameDisplay, true, secondDisplay) == gameDisplay, "Automatic overlay display did not follow the game.");
        Require(MainWindow.SelectOverlayMonitor(gameDisplay, false, secondDisplay) == secondDisplay, "Manual overlay display did not use the selected monitor.");
        SectionStyle frames = Preferences.Initial.Sections[0] with { Layout = MetricLayout.Table, Metrics = ["app", "display", "average", "low", "frametime"] };
        OverlayCanvas canvas = new() { Width = 1920, Height = 1080 };
        canvas.Apply(Preferences.Initial with { Sections = [frames] });
        double initialLabelWidth = FrameMetricsGrid(canvas).ColumnDefinitions[0].Width.Value;
        SectionStyle withGeneration = frames with
        {
            Metrics = frames.Metrics.Add("generation"),
            Labels = frames.Labels.SetItem("generation", "AFMF 2.1 generated display FPS")
        };
        canvas.Apply(Preferences.Initial with { Sections = [withGeneration] });
        Grid updatedGrid = FrameMetricsGrid(canvas);
        Require(updatedGrid.ColumnDefinitions[0].Width.Value == initialLabelWidth, "Adding or renaming a table metric shifted the FPS value column.");
        Require(updatedGrid.Children.OfType<TextBlock>().Any(text => text.Text == "AFMF 2.1 generated display FPS" && text.TextWrapping == TextWrapping.NoWrap), "Long metric labels must stay on one line within the stable label column.");
    }
    private static Grid FrameMetricsGrid(OverlayCanvas canvas)
    {
        Border panel = canvas.Children.OfType<Border>().Single(item => (string)item.Tag == SectionKind.Frames.ToString());
        StackPanel contents = (StackPanel)panel.Child;
        StackPanel lines = contents.Children.OfType<StackPanel>().Single();
        return lines.Children.OfType<Grid>().Single();
    }
    private static async Task CheckLiveEditingAsync(MainWindow window, string outputDirectory)
    {
            string savedBeforeTest = File.Exists(Preferences.FilePath) ? File.ReadAllText(Preferences.FilePath) : "";
            Button testButton = FindButton(window, "TestOverlayButton");
            testButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(4000);
            OverlayTestWindow testScene = Application.Current.Windows.OfType<OverlayTestWindow>().Single();
            OverlayWindow liveOverlay = Application.Current.Windows.OfType<OverlayWindow>().Single();
            Require(liveOverlay.IsVisible, "Live test overlay hid while editing the dashboard.");
            string liveLabel = "LIVE TEST " + Guid.NewGuid().ToString("N")[..8];
            ((TextBox)window.FindName("SectionName")).Text = liveLabel;
            CheckBox showTitle = (CheckBox)window.FindName("ShowSectionName");
            showTitle.IsChecked = true; showTitle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Task.Delay(200);
            Require(VisualText(liveOverlay.Surface).Contains(liveLabel), "Label edits did not reach the live overlay before saving.");
            Require(((TextBlock)window.FindName("UnsavedLayoutNotice")).Visibility == Visibility.Visible, "Live edits did not mark the layout as unsaved.");
            ComboBox preset = (ComboBox)window.FindName("PresetSelector");
            preset.SelectedItem = preset.Items.Cast<LayoutPresets.Choice>().Single(item => item.Name == "Classic RTSS");
            Task discard = RespondToLayoutPromptAsync("6");
            FindButton(window, "ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await discard;
            Slider size = (Slider)window.FindName("OverlaySize");
            double oldScale = size.Value;
            double oldHeight = liveOverlay.Surface.VisibleBounds().Height;
            size.Value = oldScale > 50 ? Math.Max(50, oldScale * 0.8) : 75;
            await Task.Delay(200);
            Require(Math.Abs(liveOverlay.Surface.VisibleBounds().Height / oldHeight - size.Value / oldScale) < 0.03 && size.Value != oldScale, "Overlay size slider did not resize the live overlay proportionally.");
            SaveImage(testScene, Path.Combine(outputDirectory, "test-scene.png"));
            testScene.Close();
            Require(!liveOverlay.IsVisible && !VisualText(liveOverlay.Surface).Contains(liveLabel), "Closing the test did not restore the saved overlay.");
            Require((File.Exists(Preferences.FilePath) ? File.ReadAllText(Preferences.FilePath) : "") == savedBeforeTest, "Live testing changed saved preferences without Save & apply.");
            testButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            testButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!Application.Current.Windows.OfType<OverlayTestWindow>().Any(), "Stop test left a test window open.");
    }
    private static Button FindButton(Window window, string name) => (Button)window.FindName(name);
    private static IEnumerable<string> VisualText(DependencyObject parent)
    {
        if (parent is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (string value in VisualText(VisualTreeHelper.GetChild(parent, i))) yield return value;
    }
    private static void SavePresetImage(OverlayCanvas canvas, string path)
    {
        Rect bounds = canvas.VisibleBounds();
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(23, 32, 43)), null, new Rect(0, 0, bounds.Width + 32, bounds.Height + 32));
            VisualBrush brush = new(canvas) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill };
            drawing.DrawRectangle(brush, null, new Rect(16, 16, bounds.Width, bounds.Height));
        }
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(bounds.Width + 32), (int)Math.Ceiling(bounds.Height + 32), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path); encoder.Save(file);
    }
    private static async Task CloseAsync(Window window)
    {
        TaskCompletionSource completion = new();
        window.Closed += (_, _) => completion.SetResult();
        window.Close(); await completion.Task;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void SaveImage(Window window, string path)
    {
        RenderTargetBitmap bitmap = new((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path); encoder.Save(file);
    }
}




