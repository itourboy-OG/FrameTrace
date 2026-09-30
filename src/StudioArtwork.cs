using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FrameTrace;

public partial class MainWindow
{
    private readonly StackPanel artworkOptions = new() { Margin = new Thickness(0, 16, 0, 0) };
    private readonly Slider artworkWidth = new() { Minimum = 1, Maximum = 2048, Value = 240, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider artworkHeight = new() { Minimum = 1, Maximum = 2048, Value = 120, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider artworkOpacity = new() { Minimum = 0, Maximum = 1, Value = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private readonly Slider artworkRadius = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock artworkSizeText = new() { Foreground = Brushes.LightSlateGray };
    private readonly StackPanel radiusOptions = new();
    private readonly StackPanel animationOptions = new();
    private readonly ComboBox animationPlayback = new() { MaxDropDownHeight = 220 };
    private readonly StackPanel alarmThresholdOptions = new();
    private readonly TextBlock alarmThresholdLabel = new() { Foreground = Brushes.LightSlateGray };
    private readonly Slider alarmThreshold = new() { Minimum = 50, Maximum = 100, Value = 85, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly ComboBox spritePicker = new() { DisplayMemberPath = "Name", MaxDropDownHeight = 320, Visibility = Visibility.Collapsed };
    private readonly Image spritePreview = new() { Height = 100, Stretch = Stretch.Uniform, Margin = new Thickness(0, 9, 0, 9), Visibility = Visibility.Collapsed };
    private readonly TextBlock animationDescription = new() { Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer spritePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private BitmapSource[] spritePreviewFrames = [];
    private int spritePreviewIndex;
    private readonly TextBlock importDescription = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
    private readonly Button addSpriteButton = new() { Content = "Add selected RTSS artwork", Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly ComboBox graphPicker = new() { DisplayMemberPath = "Name", MaxDropDownHeight = 240, Visibility = Visibility.Collapsed };
    private readonly Button addGraphButton = new() { Content = "Add selected live graph or bar", Margin = new Thickness(0, 7, 0, 8), Visibility = Visibility.Collapsed };

    private void InitializeArtworkTools()
    {
        StudioArtworkTab.Visibility = Visibility.Visible;
        artworkOptions.Children.Add(new TextBlock { Text = "SELECTED ARTWORK SIZE", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 6) });
        artworkOptions.Children.Add(artworkSizeText);
        artworkOptions.Children.Add(new TextBlock { Text = "Width", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 8, 0, 3) });
        artworkOptions.Children.Add(artworkWidth);
        artworkOptions.Children.Add(new TextBlock { Text = "Height", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 8, 0, 3) });
        artworkOptions.Children.Add(artworkHeight);
        artworkOptions.Children.Add(new TextBlock { Text = "Opacity", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 8, 0, 3) });
        artworkOptions.Children.Add(artworkOpacity);
        radiusOptions.Children.Add(new TextBlock { Text = "Corner rounding", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 8, 0, 3) });
        radiusOptions.Children.Add(artworkRadius);
        artworkOptions.Children.Add(radiusOptions);
        animationOptions.Children.Add(new TextBlock { Text = "Animation playback", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 12, 0, 5) });
        foreach (string label in new[] { "Loop continuously", "Follow GPU fan speed", "Follow GPU usage", "Follow CPU usage", "Follow RAM usage", "GPU temperature alarm", "CPU temperature alarm" })
            animationPlayback.Items.Add(new ComboBoxItem { Content = label });
        animationPlayback.SetResourceReference(StyleProperty, "CaptureTargetStyle");
        AutomationProperties.SetAutomationId(animationPlayback, "ArtworkPlayback");
        animationPlayback.SelectionChanged += EditSection;
        animationOptions.Children.Add(animationPlayback);
        alarmThresholdOptions.Children.Add(alarmThresholdLabel);
        alarmThresholdOptions.Children.Add(alarmThreshold);
        animationOptions.Children.Add(alarmThresholdOptions);
        AutomationProperties.SetAutomationId(alarmThreshold, "AlarmTemperatureThreshold");
        alarmThreshold.ValueChanged += EditSection;
        alarmThreshold.ValueChanged += (_, _) => alarmThresholdLabel.Text = $"Turn red at {alarmThreshold.Value:0}°C";
        artworkOptions.Children.Add(animationOptions);
        artworkWidth.ValueChanged += EditSection;
        artworkHeight.ValueChanged += EditSection;
        artworkOpacity.ValueChanged += EditSection;
        artworkRadius.ValueChanged += EditSection;
        artworkWidth.ValueChanged += (_, _) => artworkSizeText.Text = $"{artworkWidth.Value:0} × {artworkHeight.Value:0} pixels";
        artworkHeight.ValueChanged += (_, _) => artworkSizeText.Text = $"{artworkWidth.Value:0} × {artworkHeight.Value:0} pixels";
        spritePreviewTimer.Tick += (_, _) =>
        {
            if (!spritePreview.IsVisible || draft.ReduceMotion || spritePreviewFrames.Length < 2) return;
            spritePreviewIndex = (spritePreviewIndex + 1) % spritePreviewFrames.Length;
            spritePreview.Source = spritePreviewFrames[spritePreviewIndex];
        };
        spritePicker.SetResourceReference(StyleProperty, "CaptureTargetStyle");
        AutomationProperties.SetAutomationId(spritePicker, "RtssSpriteSelector");
        spritePicker.SelectionChanged += (_, _) =>
        {
            spritePreviewTimer.Stop();
            spritePreviewFrames = spritePicker.SelectedItem is RtssAnimation animation ? animation.Frames.Select(OverlayCanvas.DecodePng).ToArray() : [];
            spritePreviewIndex = 0;
            spritePreview.Source = spritePreviewFrames.Length > 0 ? spritePreviewFrames[0] : (spritePicker.SelectedItem as RtssSprite)?.Preview;
            animationDescription.Text = spritePicker.SelectedItem is RtssAnimation selected ? selected.Source switch
            {
                ArtworkAnimationSource.GpuFan => "Preview cycles the frames. On the overlay, the fan follows GPU RPM and pauses at 0 RPM.",
                ArtworkAnimationSource.GpuUsage => "Preview cycles the frames. On the overlay, the image follows live GPU usage.",
                ArtworkAnimationSource.CpuUsage => "Preview cycles the frames. On the overlay, the image follows live CPU usage.",
                ArtworkAnimationSource.RamUsage => "Preview cycles the frames. On the overlay, the image follows live RAM usage.",
                ArtworkAnimationSource.GpuTemperatureAlarm => "The GPU flame changes at 70°C by default. Adjust the threshold after adding it.",
                ArtworkAnimationSource.CpuTemperatureAlarm => "The CPU flame changes at 85°C by default. Adjust the threshold after adding it.",
                _ => "Preview cycles the frames. On the overlay, the animation loops while visible."
            } : "";
            animationDescription.Visibility = spritePreviewFrames.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (spritePreviewFrames.Length > 1 && spritePicker.SelectedItem is RtssAnimation { Source: not (ArtworkAnimationSource.GpuTemperatureAlarm or ArtworkAnimationSource.CpuTemperatureAlarm) }) spritePreviewTimer.Start();
            addSpriteButton.IsEnabled = spritePicker.SelectedItem is RtssSprite or RtssAnimation;
        };
        graphPicker.SetResourceReference(StyleProperty, "CaptureTargetStyle");
        AutomationProperties.SetAutomationId(graphPicker, "RtssGraphSelector");
        AutomationProperties.SetAutomationId(addGraphButton, "AddRtssGraph");
        graphPicker.SelectionChanged += (_, _) => addGraphButton.IsEnabled = graphPicker.SelectedItem is RtssLiveGraph or RtssLiveBar;
        PopulateArtworkTools();
    }

    private void PopulateArtworkTools()
    {
        ArtworkTools.Children.Add(new TextBlock { Text = "FREEFORM LAYERS", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 14, 0, 7) });
        StackPanel order = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        Button backward = new() { Content = "Send backward" };
        Button forward = new() { Content = "Bring forward", Margin = new Thickness(7, 0, 0, 0) };
        AutomationProperties.SetAutomationId(backward, "LayerBackward");
        AutomationProperties.SetAutomationId(forward, "LayerForward");
        backward.Click += (_, _) => MoveLayer(-1);
        forward.Click += (_, _) => MoveLayer(1);
        order.Children.Add(backward); order.Children.Add(forward); ArtworkTools.Children.Add(order);
        Button text = new() { Content = "Add text layer", Margin = new Thickness(0, 0, 0, 6) };
        Button game = new() { Content = "Add game info · icon + present API", Margin = new Thickness(0, 0, 0, 6) };
        Button panel = new() { Content = "Add color panel", Margin = new Thickness(0, 0, 0, 6) };
        Button png = new() { Content = "Add PNG layer", Margin = new Thickness(0, 0, 0, 14) };
        Button load = new() { Content = "Open RTSS skin (.ovl)", Margin = new Thickness(0, 0, 0, 7) };
        AutomationProperties.SetAutomationId(text, "AddTextLayer");
        AutomationProperties.SetAutomationId(game, "AddGameInfo");
        AutomationProperties.SetAutomationId(panel, "AddColorPanel");
        AutomationProperties.SetAutomationId(png, "AddPngLayer");
        AutomationProperties.SetAutomationId(load, "OpenRtssSkin");
        AutomationProperties.SetAutomationId(addSpriteButton, "AddRtssSprite");
        text.Click += (_, _) => AddArtworkLayer("Your text", "", 240, 120);
        game.Click += (_, _) => AddGameInfo();
        panel.Click += (_, _) => AddColorPanel();
        png.Click += (_, _) => OpenPngLayer();
        load.Click += (_, _) => OpenRtssSkin();
        addSpriteButton.Click += (_, _) =>
        {
            if (spritePicker.SelectedItem is RtssSprite sprite) AddArtworkLayer(sprite.Name, sprite.ImageData, sprite.Width, sprite.Height);
            else if (spritePicker.SelectedItem is RtssAnimation animation) AddAnimationLayer(animation);
            else StudioStatus.Text = "Open an RTSS skin and choose artwork first.";
        };
        addGraphButton.Click += (_, _) =>
        {
            if (graphPicker.SelectedItem is RtssLiveGraph graph) AddLiveGraph(graph);
            else if (graphPicker.SelectedItem is RtssLiveBar bar) AddLiveBar(bar);
            else StudioStatus.Text = "Open an RTSS skin and choose a supported live graph or bar first.";
        };
        ArtworkTools.Children.Add(text);
        ArtworkTools.Children.Add(game);
        ArtworkTools.Children.Add(panel);
        ArtworkTools.Children.Add(png);
        ArtworkTools.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 12) });
        ArtworkTools.Children.Add(new TextBlock { Text = "RTSS ARTWORK", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 6) });
        ArtworkTools.Children.Add(load);
        ArtworkTools.Children.Add(importDescription);
        ArtworkTools.Children.Add(spritePicker);
        ArtworkTools.Children.Add(spritePreview);
        ArtworkTools.Children.Add(animationDescription);
        ArtworkTools.Children.Add(addSpriteButton);
        ArtworkTools.Children.Add(graphPicker);
        ArtworkTools.Children.Add(addGraphButton);
        ArtworkTools.Children.Add(artworkOptions);
        ArtworkTools.Children.Add(new TextBlock { Text = "Keep the .ovl and its companion PNG together. Add the parts you want, then arrange them on the canvas. Full RTSS skin layouts and formulas are not converted automatically.", Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
    }

    private void OpenRtssSkin()
    {
        OpenFileDialog dialog = new() { Filter = "RTSS overlay (*.ovl)|*.ovl" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            RtssArtwork skin = RtssArtworkImporter.Read(dialog.FileName);
            spritePicker.ItemsSource = skin.Sprites.Cast<object>().Concat(skin.Animations).ToArray();
            spritePicker.SelectedIndex = skin.Sprites.IsEmpty && skin.Animations.IsEmpty ? -1 : 0;
            graphPicker.ItemsSource = skin.LiveGraphs.Cast<object>().Concat(skin.LiveBars).ToArray();
            graphPicker.SelectedIndex = skin.LiveGraphs.IsEmpty && skin.LiveBars.IsEmpty ? -1 : 0;
            importDescription.Text = $"{skin.Sprites.Length} still images, {skin.Animations.Length} animations, {skin.LiveGraphs.Length} live graphs, and {skin.LiveBars.Length} live bars ready. {skin.UnsupportedTables} other tables use RTSS features Frame Trace cannot convert yet.";
            importDescription.Visibility = Visibility.Visible;
            spritePicker.Visibility = skin.Sprites.IsEmpty && skin.Animations.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            spritePreview.Visibility = spritePicker.Visibility;
            addSpriteButton.Visibility = spritePicker.Visibility;
            graphPicker.Visibility = skin.LiveGraphs.IsEmpty && skin.LiveBars.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            addGraphButton.Visibility = graphPicker.Visibility;
            StudioStatus.Text = skin.Sprites.IsEmpty && skin.Animations.IsEmpty && skin.LiveGraphs.IsEmpty && skin.LiveBars.IsEmpty ? "No supported artwork or graphs were found in this skin." : "Choose artwork, a live graph, or a live bar, then place it on the canvas.";
        }
        catch (IOException error) { Report("RTSS skin import failed", error); }
        catch (UnauthorizedAccessException error) { Report("RTSS skin import failed", error); }
        catch (NotSupportedException error) { Report("RTSS skin import failed", error); }
        catch (ArgumentException error) { Report("RTSS skin import failed", error); }
    }

    private void OpenPngLayer()
    {
        OpenFileDialog dialog = new() { Filter = "PNG image (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileInfo file = new(dialog.FileName);
            if (file.Length > 3_000_000) throw new InvalidDataException("Choose a PNG smaller than 3 MB so shared layouts remain manageable.");
            byte[] png = File.ReadAllBytes(dialog.FileName);
            using MemoryStream input = new(png);
            PngBitmapDecoder decoder = new(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource image = decoder.Frames[0];
            if (image.PixelWidth is < 1 or > 2048 || image.PixelHeight is < 1 or > 2048)
                throw new InvalidDataException("PNG dimensions must be between 1 and 2048 pixels.");
            AddArtworkLayer(Path.GetFileNameWithoutExtension(dialog.FileName), Convert.ToBase64String(png), image.PixelWidth, image.PixelHeight);
        }
        catch (IOException error) { Report("PNG import failed", error); }
        catch (UnauthorizedAccessException error) { Report("PNG import failed", error); }
        catch (NotSupportedException error) { Report("PNG import failed", error); }
        catch (ArgumentException error) { Report("PNG import failed", error); }
    }

    private void AddArtworkLayer(string name, string imageData, double width, double height)
    {
        if (draft.Sections.Length >= 64) { StudioStatus.Text = "This layout has reached its 64-item limit."; return; }
        SectionStyle item = new(SectionKind.Artwork, name, "#83EFCD", "#FFFFFF", 24, 24, 0.15, 0.15, true, [])
        {
            Id = Guid.NewGuid().ToString("N"), ImageData = imageData, ImageWidth = width, ImageHeight = height,
            ShowName = imageData.Length == 0, Padding = 0
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = "Layer added to the preview. Drag it on the canvas, adjust its size, then Save & apply.";
    }

    private void AddGameInfo()
    {
        if (draft.Sections.Length >= 64) { StudioStatus.Text = "This layout has reached its 64-item limit."; return; }
        SectionStyle item = new(SectionKind.Game, "GAME", "#83EFCD", "#FFFFFF", 16, 18, 0.15, 0.15, true, ["game", "api"])
        {
            Id = Guid.NewGuid().ToString("N"), ShowName = false, Layout = MetricLayout.Table, Padding = 4
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = "Game info added. The icon and presentation API update when a game is captured; DXGI cannot distinguish DirectX 11 from 12.";
    }

    private void AddLiveGraph(RtssLiveGraph graph)
    {
        if (draft.Sections.Length >= 64) { StudioStatus.Text = "This layout has reached its 64-item limit."; return; }
        SectionKind kind = graph.Source switch { ArtworkGraphSource.GpuUsage => SectionKind.Gpu, ArtworkGraphSource.CpuUsage => SectionKind.Cpu, ArtworkGraphSource.RamUsage => SectionKind.Ram, _ => SectionKind.Frames };
        SectionStyle source = Preferences.Initial.Sections.Single(item => item.Kind == kind);
        SectionStyle item = source with
        {
            Id = Guid.NewGuid().ToString("N"), Name = graph.Name, ShowName = false,
            Metrics = [], Graph = true, GraphSource = graph.Source, GraphWidth = graph.Width, GraphHeight = graph.Height,
            GraphBelow = true, Padding = 0, X = 0.15, Y = 0.15
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = "Live graph added. Drag it on the canvas, then Save & apply.";
    }

    private void AddLiveBar(RtssLiveBar bar)
    {
        if (draft.Sections.Length >= 64) { StudioStatus.Text = "This layout has reached its 64-item limit."; return; }
        string color = bar.Source switch
        {
            ArtworkBarSource.GpuUsage => "#FFAA49", ArtworkBarSource.CpuUsage => "#69CDF6",
            ArtworkBarSource.RamUsage => "#DBB7FA", ArtworkBarSource.GpuTemperature => "#FF7777",
            ArtworkBarSource.CpuTemperature => "#FFBA77", _ => throw new ArgumentOutOfRangeException(nameof(bar))
        };
        SectionStyle item = new(SectionKind.Artwork, bar.Name, color, "#FFFFFF", 24, 24, 0.15, 0.15, true, [])
        {
            Id = Guid.NewGuid().ToString("N"), BarSource = bar.Source, BarMinimum = bar.Minimum, BarMaximum = bar.Maximum,
            ImageWidth = bar.Width, ImageHeight = bar.Height, ShowName = false, Padding = 0
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = "Live bar added. It follows its sensor reading; place it on the canvas, then Save & apply.";
    }

    private void AddAnimationLayer(RtssAnimation animation)
    {
        if (draft.Sections.Length >= 64) { StudioStatus.Text = "This layout has reached its 64-item limit."; return; }
        SectionStyle item = new(SectionKind.Artwork, animation.Name, "#83EFCD", "#FFFFFF", 24, 24, 0.15, 0.15, true, [])
        {
            Id = Guid.NewGuid().ToString("N"), AnimationFrames = animation.Frames,
            AnimationSource = animation.Source, AnimationMinimum = animation.Minimum, AnimationMaximum = animation.Maximum,
            ImageWidth = animation.Width, ImageHeight = animation.Height,
            ShowName = false, Padding = 0
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = animation.Source switch
        {
            ArtworkAnimationSource.GpuFan => "GPU fan animation added. It pauses when the fan reports zero RPM.",
            ArtworkAnimationSource.GpuUsage => "GPU load animation added. It follows the live GPU usage reading.",
            ArtworkAnimationSource.CpuUsage => "CPU load animation added. It follows the live CPU usage reading.",
            ArtworkAnimationSource.RamUsage => "RAM load animation added. It follows the live total-memory reading.",
            ArtworkAnimationSource.GpuTemperatureAlarm => "GPU temperature alarm added. Its flame changes at 70°C. You can adjust the threshold in Layers.",
            ArtworkAnimationSource.CpuTemperatureAlarm => "CPU temperature alarm added. Its flame changes at 85°C. You can adjust the threshold in Layers.",
            _ => "Animation added. It loops while the overlay is visible."
        };
    }

    private void AddColorPanel()
    {
        if (draft.Sections.Length >= 64) { StudioStatus.Text = "This layout has reached its 64-item limit."; return; }
        SectionStyle item = new(SectionKind.Artwork, "Color panel", "#254B48", "#FFFFFF", 24, 24, 0.13, 0.13, true, [])
        {
            Id = Guid.NewGuid().ToString("N"), ArtworkFill = true, ImageWidth = 300, ImageHeight = 150,
            ArtworkOpacity = 0.8, ArtworkRadius = 12, ShowName = false, Padding = 0
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = "Color panel added. Resize it, choose its fill color, then send it behind your readings.";
    }

    private void MoveLayer(int direction)
    {
        if (draft.Sections.IsEmpty) return;
        int index = draft.Sections.IndexOf(Selected);
        int target = index + direction;
        if (target < 0 || target >= draft.Sections.Length) return;
        SectionStyle item = draft.Sections[index];
        draft = draft with { Sections = draft.Sections.RemoveAt(index).Insert(target, item) };
        RefreshSectionSelector();
        preview.Apply(draft); RefreshTestOverlay(); RefreshLayoutNotice();
        StudioStatus.Text = "Layer order updated. Save & apply to keep it.";
    }
}
