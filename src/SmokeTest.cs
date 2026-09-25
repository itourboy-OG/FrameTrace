using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Frameglass;

public static class SmokeTest
{
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
            try { Preferences.Validate(Preferences.Initial with { SensorRefreshMs = 251 }); throw new InvalidDataException("Invalid sensor refresh interval was accepted."); }
            catch (InvalidDataException error) when (error.Message.StartsWith("Sensor refresh interval", StringComparison.Ordinal)) { }
            foreach (string preset in LayoutPresets.Names)
            {
                Preferences styled = Preferences.Validate(LayoutPresets.Create(Preferences.Initial, preset));
                Preferences restored = Preferences.Parse(JsonSerializer.Serialize(styled));
                Require(restored.Sections.Zip(styled.Sections).All(pair => pair.First.Layout == pair.Second.Layout && pair.First.HeroSize == pair.Second.HeroSize && pair.First.Padding == pair.Second.Padding && pair.First.GraphBelow == pair.Second.GraphBelow && pair.First.TextShadow == pair.Second.TextShadow), "Preset styling did not survive serialization.");
            }
            Require(FrameMetrics.Summarize([a, b, generated with { FrameType = "Intel XeSS-FG" }], 120).Generation == "Intel XeSS-FG reported", "XeSS-FG tags were not identified.");
            using (HardwareMonitor hardware = await Task.Run(() => new HardwareMonitor()))
            {
                await Task.Delay(1100);
                ImmutableArray<SensorReading> sensors = await Task.Run(hardware.Read);
                Require(sensors.Any(s => s.Value.HasValue), "No real hardware sensor values.");
                File.WriteAllText(Path.Combine(outputDirectory, "sensors.json"), JsonSerializer.Serialize(sensors));
            }
            window = new MainWindow();
            Application.Current.MainWindow = window;
            window.Loaded += (_, _) =>
            {
                if (Preferences.Load().OverlayEnabled)
                    ((Button)window.FindName("OverlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            window.Show();
            window.Activate();
            bool reduceMotion = Preferences.Load().ReduceMotion;
            Require(window.AnimatedBackground.ReducedMotion == reduceMotion && window.AnimatedBackground.IsAnimating == !reduceMotion, "The background motion setting was not applied on startup.");
            window.AnimatedBackground.SetReducedMotion(!reduceMotion);
            Require(window.AnimatedBackground.ReducedMotion != reduceMotion && window.AnimatedBackground.IsAnimating == reduceMotion, "The background motion setting did not apply live.");
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
            CheckTableLabelStability();
            CheckSavedPresets(outputDirectory);
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
                Require(captured.AppFps > 0, "Real ETW capture returned no FPS.");
            }
            int foregroundChecks = 0, observedSamples = 0;
            await using FrameCapture probeCapture = new();
            foreach (string probeName in new[] { "Frame Trace probe one", "Frame Trace probe two" })
            {
                ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false };
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
            OverlayWindow overlay = new(Preferences.Initial);
            overlay.UpdateData(OverlayData.Build([], captured));
            DisplayInfo display = Desktop.Displays().Single(d => d.Primary);
            overlay.ShowOnMonitor(display.Bounds);
            overlay.Apply(Preferences.Initial with { Opacity = 0.4 }); overlay.UpdateLayout();
            Require(overlay.Topmost && overlay.ActualWidth < display.CanvasSize.Width, "Overlay must be topmost and cropped to content.");
            SaveImage(overlay, Path.Combine(outputDirectory, "overlay.png")); overlay.Close();
            ComboBox capturePicker = (ComboBox)window.FindName("CaptureTargetSelector");
            capturePicker.IsDropDownOpen = true;
            await Task.Delay(200);
            Popup popup = (Popup)capturePicker.Template.FindName("PART_Popup", capturePicker);
            Require(Math.Abs(popup.Child.RenderSize.Width - capturePicker.ActualWidth) < 2, $"Capture popup width differs from its control: popup={popup.Child.RenderSize.Width:0.##}, control={capturePicker.ActualWidth:0.##}.");
            Point popupOrigin = popup.Child.PointToScreen(new Point());
            Point pickerOrigin = capturePicker.PointToScreen(new Point());
            Require(Math.Abs(popupOrigin.X - pickerOrigin.X) < 2, "Capture popup is offset outside its control.");
            capturePicker.IsDropDownOpen = false;
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
            ComboBox presets = (ComboBox)window.FindName("PresetSelector");
            ((Slider)window.FindName("ZoomSlider")).Value = 100;
            foreach (string preset in LayoutPresets.Names)
            {
                presets.SelectedItem = preset;
                Button apply = FindButton(window, "ApplyPresetButton");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
                SavePresetImage(presetCanvas, Path.Combine(outputDirectory, preset.Replace(" ", "-") + "-detail.png"));
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
            ((TabControl)window.FindName("Pages")).SelectedIndex = 2;
            await Task.Delay(150);
            SaveImage(window, Path.Combine(outputDirectory, "settings.png"));
            ScrollViewer settingsPage = (ScrollViewer)((TabControl)window.FindName("Pages")).SelectedContent;
            settingsPage.ScrollToVerticalOffset(settingsPage.ScrollableHeight);
            await Task.Delay(150);
            SaveImage(window, Path.Combine(outputDirectory, "settings-bottom.png"));
            Preferences.Write(Preferences.Initial, Path.Combine(outputDirectory, "layout.json"));
            Require(Preferences.Parse(File.ReadAllText(Path.Combine(outputDirectory, "layout.json"))).Sections.Length == 4, "Layout round trip failed.");
            await CloseAsync(window); window = null;
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), $"PASS: frame calculations, stale data, swap-chain isolation, real sensors, real ETW capture ({captured.AppFps:0.0} FPS), capture restart with fresh frames and released old session, two real renderer processes, manual selection while dashboard is active, process exit and return to Automatic, {observedSamples}/40 nonempty FPS snapshots (occluded windows may stop rendering), {foregroundChecks} foreground routing checks (focus-dependent), ignore rules, custom labels, five presets with table/tile/hero styling round trips and non-overlap checks at 1920/3440 widths, average/1% lows, monitor-sized canvas, 100% zoom, cropped overlay, marquee/group movement, grid snapping and Ctrl bypass, saved overlay scaling, bounded popup placement, named preset save/replace/reload, separate metric add/remove, independent label sizes, schema migration, dynamic technology fields, minimum-size studio controls, UI renders, clean shutdown.\nPEAK/OptiScaler, Cyberpunk, AFMF, NVIDIA hardware, and exclusive fullscreen have not been validated.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "result.txt"), "FAIL\n" + error);
            if (window is not null) await CloseAsync(window);
            Environment.ExitCode = 1;
        }
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
            await CheckLiveEditingAsync(window, outputDirectory);
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
        string path = Path.Combine(directory, "saved-layouts.json");
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
    private static async Task CheckCustomPresetDropdownAsync(MainWindow window, string directory)
    {
        ComboBox selector = (ComboBox)window.FindName("CustomPresetSelector");
        ImmutableArray<NamedLayout> presets = [new("OG", Preferences.Initial)];
        SavedLayouts.Save("OG", presets[0].Layout, Path.Combine(directory, "saved-layouts-ui.json"));
        selector.ItemsSource = presets;
        selector.SelectedIndex = 0;
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
            ((TextBox)window.FindName("SectionName")).Text = "LIVE TEST LABEL";
            CheckBox showTitle = (CheckBox)window.FindName("ShowSectionName");
            showTitle.IsChecked = true; showTitle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Task.Delay(200);
            Require(VisualText(liveOverlay.Surface).Contains("LIVE TEST LABEL"), "Label edits did not reach the live overlay before saving.");
            ComboBox preset = (ComboBox)window.FindName("PresetSelector");
            preset.SelectedItem = "Classic RTSS";
            FindButton(window, "ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Slider size = (Slider)window.FindName("OverlaySize");
            double oldScale = size.Value;
            double oldHeight = liveOverlay.Surface.VisibleBounds().Height;
            size.Value = oldScale > 50 ? Math.Max(50, oldScale * 0.8) : 75;
            await Task.Delay(200);
            Require(Math.Abs(liveOverlay.Surface.VisibleBounds().Height / oldHeight - size.Value / oldScale) < 0.03 && size.Value != oldScale, "Overlay size slider did not resize the live overlay proportionally.");
            SaveImage(testScene, Path.Combine(outputDirectory, "test-scene.png"));
            testScene.Close();
            Require(!liveOverlay.IsVisible && !VisualText(liveOverlay.Surface).Contains("LIVE TEST LABEL"), "Closing the test did not restore the saved overlay.");
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




