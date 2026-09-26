using System.Collections.Immutable;

using System.ComponentModel;

using System.Diagnostics;

using System.IO;

using System.Net.Http;

using System.Text.Json;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Input;

using System.Windows.Interop;

using System.Windows.Media;

using Microsoft.Win32;



namespace Frameglass;



public partial class MainWindow : Window

{

    private readonly CancellationTokenSource shutdown = new();

    private readonly OverlayCanvas preview = new();

    private Preferences preferences = Preferences.Initial;

    private Preferences draft = Preferences.Initial;
    private Preferences layoutBaseline = Preferences.Initial;

    private HardwareMonitor? hardware;

    private FrameCapture? capture;

    private OverlayWindow? overlay;

    private HotkeyBinding? hotkey;

    private ImmutableArray<SensorReading> readings = [];

    private FrameSummary summary = FrameMetrics.Summarize([], Environment.TickCount64);

    private Task monitoring = Task.CompletedTask;

    private bool ready, loading, closing, closed, enabled;

    private int target, retries;

    private bool captureRestartRequested;



    private long nextCaptureDiagnostic;

    private string lastError = "";

    private string lastMode = "";

    private Task<ImmutableArray<SensorReading>>? sensorRead;

    private ImmutableArray<SensorReading> renderedReadings;





    public MainWindow()

    {

        InitializeComponent();
        Title = AppIdentity.ProductName;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MinHeight = Math.Min(MinHeight, Height);

        PreviewViewport.Content = preview; preview.EnableEditing();

        preview.SectionSelected += SelectEditorSection;

        preview.SectionsMoved += sections => { draft = draft with { Sections = sections }; RefreshTestOverlay(); RefreshLayoutNotice(); };

        Loaded += (_, _) =>

        {

            try

            {

                preferences = Preferences.Load(); draft = preferences; layoutBaseline = preferences; enabled = preferences.OverlayEnabled;
                AnimatedBackground.SetReducedMotion(preferences.ReduceMotion);
                if (preferences.StartMinimized) WindowState = WindowState.Minimized;

                overlay = new OverlayWindow(preferences);

                hotkey = new HotkeyBinding(new WindowInteropHelper(this).Handle);

                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowMessage);

                ready = true; InitializeStudio(); LoadControls();
                if (preferences.StartMinimized) WindowState = WindowState.Minimized;

                CaptureTargetSelector.ItemsSource = new[] { CaptureTarget.Automatic }; CaptureTargetSelector.SelectedIndex = 0;

                AdministratorButton.Visibility = HardwareMonitor.IsElevated() ? Visibility.Collapsed : Visibility.Visible;

                AppVersion.Text = "v" + typeof(App).Assembly.GetName().Version!.ToString(3);

                try { hotkey.Replace(preferences.Shortcut); }

                catch (Win32Exception error) { Report("Shortcut unavailable; choose another in Settings", error); }

                monitoring = MonitorAsync();
                if (preferences.CheckUpdatesOnStartup) _ = CheckForUpdatesAsync();

            }

            catch (Exception error) { Report("Startup failed", error); }

        };

        Closing += CloseAsync;
        StateChanged += (_, _) => { if (ready && WindowState != WindowState.Minimized) RenderData(); };

    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)

    {

        if (ready && message is 0x007E or 0x02E0) Dispatcher.BeginInvoke(RefreshDisplays);
        if (message == 0x0312 && hotkey?.Matches(wParam) == true) { ToggleOverlay(this, new RoutedEventArgs()); handled = true; }

        return 0;

    }

    private async Task MonitorAsync()

    {

        try { hardware = await Task.Run(() => new HardwareMonitor()); }

        catch (Exception error) { Report("Sensor initialization failed; FPS capture remains available", error); }

        long nextSensors = 0, nextCapture = 0;

        while (!shutdown.IsCancellationRequested)

        {

            long now = Environment.TickCount64;

            if (sensorRead is { IsCompleted: true })

            {

                try { readings = await sensorRead; HardwareStatus.Text = "Sensors connected"; }

                catch (Exception error) { HardwareStatus.Text = "Sensor read failed"; Report("Sensor read failed", error); nextSensors = now + 5000; }

                sensorRead = null;

            }

            if (hardware is not null && sensorRead is null && now >= nextSensors)

            {

                nextSensors = now + preferences.SensorRefreshMs; sensorRead = Task.Run(hardware.Read);

            }

            if (captureRestartRequested)
            {
                captureRestartRequested = false;
                try { if (capture is not null) await capture.DisposeAsync(); }
                catch (Exception error) { Report("Capture restart cleanup failed", error); }
                capture = null; target = 0; retries = 0; nextCapture = now;
                summary = FrameMetrics.Summarize([], now); overlay?.Hide();
                GameName.Text = "Waiting for your game";
                RetryButton.IsEnabled = true;
            }

            if (capture is null && retries < 3 && now >= nextCapture)

            {

                try { capture = new FrameCapture(); Status.Text = "Capture is running. Open your game; use " + Desktop.HotkeyLabel(preferences.Shortcut) + " to toggle the overlay."; }

                catch (Exception error) { retries++; nextCapture = now + 2000 * retries; Report("Capture could not start", error); }

            }

            try

            {

                if (capture is not null)

                {

                    capture.CheckHealth();

                    if (testWindow is not null)
                    {
                        target = Environment.ProcessId;
                        summary = capture.ReadSummary(target);
                        GameName.Text = "Frame Trace · Overlay test";
                        CaptureState.Text = "Live test · Frame Trace presentation FPS, not a game benchmark";
                        if (enabled && DisplaySelector.SelectedItem is DisplayInfo testDisplay) ShowLiveOverlay(testDisplay.Bounds);
                        else overlay?.Hide();
                    }
                    else
                    {
                    ForegroundWindow? foreground = Desktop.Foreground();

                    ImmutableArray<CaptureCandidate> candidates = capture.Candidates();

                    bool manual = selectedTarget.ProcessId != 0;
                    bool manualAlive = manualProcess is not null && !manualProcess.HasExited;
                    CaptureCandidate? candidate = manual
                        ? manualAlive ? candidates.FirstOrDefault(item => item.ProcessId == selectedTarget.ProcessId) : null
                        : FrameMetrics.SelectForeground(candidates, foreground?.ProcessId ?? 0, preferences.IgnoredApps, Environment.ProcessId);
                    if (manual) target = manualAlive ? selectedTarget.ProcessId : 0;

                    if (candidate is not null)

                    {

                        if (target != candidate.ProcessId) Diagnostics.Write("target", JsonSerializer.Serialize(candidate));

                        target = candidate.ProcessId;

                        if (foreground?.ProcessId == candidate.ProcessId) FollowGameDisplay(foreground.Monitor);

                        GameName.Text = manual ? selectedTarget.Title : string.IsNullOrWhiteSpace(foreground!.Title) ? candidate.Application : foreground.Title;

                    }

                    summary = capture.ReadSummary(target);

                    if (summary.PresentMode != lastMode)

                    {

                        lastMode = summary.PresentMode;

                        Diagnostics.Write("presentation-mode", JsonSerializer.Serialize(new { Target = target, Mode = lastMode, Overlay = enabled }));

                    }

                    CaptureState.Text = manual ? !manualAlive ? "Selected application closed · select its new instance, or choose Automatic."
                        : candidate is null ? "Selected application · no frame data arriving. Open the game, or try Restart capture."
                        : $"Capturing selected application · {candidate.Application} · {candidate.Runtime}"
                        : candidate is not null ? $"Capturing automatically · {candidate.Application} · {candidate.Runtime}"
                        : candidates.Length > 0 ? $"Waiting for a game in the foreground · receiving frames from {candidates.Length} processes."
                        : "No frame data arriving · open your game, or use Restart capture if it is already running.";
                    if (manual) GameName.Text = selectedTarget.Title;
                    if (!manual && candidate is null && !summary.AppFps.HasValue && !summary.DisplayFps.HasValue) GameName.Text = "Waiting for your game";
                    if (now >= nextCaptureDiagnostic)
                    {
                        nextCaptureDiagnostic = now + 5000;
                        Diagnostics.Write("capture-activity", JsonSerializer.Serialize(new { ForegroundPid = foreground?.ProcessId, Target = target, Selected = candidate?.ProcessId, Activity = capture.Activity, Candidates = candidates }));
                    }

                    if (enabled && candidate is not null && foreground?.ProcessId == candidate.ProcessId && (summary.AppFps.HasValue || summary.DisplayFps.HasValue))
                    {
                        Rect? selectedDisplay = (DisplaySelector.SelectedItem as DisplayInfo)?.Bounds;
                        ShowLiveOverlay(SelectOverlayMonitor(foreground!.Monitor, FollowDisplay.IsChecked == true, selectedDisplay));
                    }

                    else overlay?.Hide();
                    }

                }

            }

            catch (Exception error)

            {

                Report("Capture interrupted", error);

                if (capture is not null)

                {

                    try { await capture.DisposeAsync(); }

                    catch (Exception cleanup) { Report("Capture cleanup failed", cleanup); }

                }

                capture = null; retries++; nextCapture = now + 2000 * retries;

                summary = FrameMetrics.Summarize([], now); overlay?.Hide();

            }

            RetryButton.Content = capture is null && retries >= 3 ? "Retry capture" : "Restart capture";

            if (capture is null) CaptureState.Text = retries >= 3 ? "Capture stopped after three attempts. Export diagnostics or retry." : "Capture restarting…";

            RenderData();

            try { await Task.Delay(100, shutdown.Token); }

            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }

        }

    }

    private void ShowLiveOverlay(Rect bounds)
    {
        overlay?.UpdateData(OverlayData.Build(readings, summary));
        overlay?.Surface.UpdateGraph(summary.Points);
        overlay?.ShowOnMonitor(bounds);
    }

    private void RenderData()

    {

        if (!IsVisible || WindowState == WindowState.Minimized) return;
        OverlayButton.Content = enabled ? "Overlay on" : "Overlay off";
        if (Pages.SelectedIndex == 2) return;
        ImmutableArray<OverlaySectionData> data = OverlayData.Build(readings, summary);
        if (Pages.SelectedIndex == 1) { preview.UpdateData(data); preview.UpdateGraph(summary.Points); return; }

        AppFps.Text = summary.AppFps?.ToString("0") ?? "—"; DisplayFps.Text = summary.DisplayFps?.ToString("0") ?? "—";

        FrameTimeText.Text = Readings.Format(summary.FrameTime, "ms"); GenerationText.Text = "Frame generation · " + summary.Generation;

        PresentMode.Text = summary.PresentMode;

        AverageFps.Text = summary.AverageFps?.ToString("0") ?? "—"; LowFps.Text = summary.LowFps?.ToString("0") ?? "—";

        AverageFps.ToolTip = $"App/present samples · rolling up to 30 seconds · {summary.SampleCount} samples.";
        LowFps.ToolTip = $"1% low = 1000 / mean of slowest 1% frame times; at least 100 samples. Current sample count: {summary.SampleCount}.";

        if (renderedReadings != readings)

        {

        renderedReadings = readings;

        GpuName.Text = data[1].Name; CpuName.Text = data[2].Name;

        GpuStats.Text = string.Join(Environment.NewLine, data[1].Metrics.Select(m => m.Label + "   " + m.Text));

        CpuStats.Text = string.Join(Environment.NewLine, data[2].Metrics.Select(m => m.Label + "   " + m.Text));

        MemoryStats.Text = data[3].Metrics[0].Text; SensorAvailability.Text = HardwareMonitor.CpuSensorStatus;

        CpuCoreList.ItemsSource = readings.Where(s => s.HardwareType == "Cpu" && s.Kind == "Load" && s.Name != "CPU Total").Select(s => s.Name + "   " + Readings.Format(s.Value, "%")).ToArray();

        }

        Chart.SetSamples(summary.Points);

    }

    private void RecordHotkey(object sender, KeyEventArgs e)

    {

        e.Handled = true;

        try { draft = draft with { Shortcut = Desktop.FromKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers) }; HotkeyInput.Text = Desktop.HotkeyLabel(draft.Shortcut); }

        catch (ArgumentException error) { Status.Text = error.Message; }

    }

    private Preferences CollectSettings() => Preferences.Validate(draft with { OverlayEnabled = OverlayStartup.IsChecked == true, StartMinimized = StartMinimizedInput.IsChecked == true, RunAtLogin = RunAtLoginInput.IsChecked == true, ReduceMotion = ReduceMotionInput.IsChecked == true, CheckUpdatesOnStartup = CheckUpdatesOnStartupInput.IsChecked == true, SensorRefreshMs = (int)SensorRefreshSlider.Value, IgnoredApps = IgnoredApps.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray() });

    private void SensorRefreshChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        SensorRefreshLabel.Text = $"{e.NewValue:0} ms · {1000 / e.NewValue:0.##} reads/sec";

    private void SaveSettings(object sender, RoutedEventArgs e)

    {

        try

        {

            Preferences next = CollectSettings();
            bool startupUpdated = false, shortcutUpdated = false;
            try
            {
                if (next.RunAtLogin != preferences.RunAtLogin)
                {
                    StartupRegistration.SetEnabled(next.RunAtLogin);
                    startupUpdated = true;
                }
                if (hotkey is not null) { hotkey.Replace(next.Shortcut); shortcutUpdated = true; }
                Preferences.Save(next);
            }
            catch
            {
                if (shortcutUpdated) hotkey?.Replace(preferences.Shortcut);
                if (startupUpdated) StartupRegistration.SetEnabled(preferences.RunAtLogin);
                throw;
            }

            preferences = next; draft = next; overlay?.Apply(next); preview.Apply(next);
            AnimatedBackground.SetReducedMotion(next.ReduceMotion);
            SetLayoutBaseline();

            StudioStatus.Text = "";
            Status.Text = "Saved. Your overlay and shortcut are ready.";

        }

        catch (Exception error) { Report("Settings were not saved", error); }

    }

    private void ExportLayout(object sender, RoutedEventArgs e)

    {

        SaveFileDialog dialog = new() { Filter = "Overlay layout (*.json)|*.json", FileName = "My overlay.json" };

        if (dialog.ShowDialog(this) != true) return;

        try { Preferences.Write(Preferences.ForLayoutExport(CollectSettings()), dialog.FileName); StudioStatus.Text = "Layout exported."; }

        catch (Exception error) { Report("Layout export failed", error); }

    }

    private void ImportLayout(object sender, RoutedEventArgs e)

    {

        OpenFileDialog dialog = new() { Filter = "Overlay layout (*.json)|*.json" };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            Preferences imported = Preferences.Parse(File.ReadAllText(dialog.FileName));
            if (!ConfirmLayoutReplacement("import this layout")) return;
            draft = Preferences.ApplyImportedLayout(draft, imported); LoadControls(); SetLayoutBaseline();
            StudioStatus.Text = "Layout loaded in preview. Save & apply when ready.";
        }

        catch (Exception error) { Report("Layout import failed", error); }

    }

    private void ResetLayout(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLayoutReplacement("reset the layout")) return;
        draft = draft with { Sections = Preferences.Initial.Sections }; LoadControls();
    }

    private void PageChanged(object sender, SelectionChangedEventArgs e) { if (ready && e.Source == Pages) RenderData(); }

    private void ToggleOverlay(object sender, RoutedEventArgs e) { enabled = !enabled; if (!enabled) overlay?.Hide(); OverlayButton.Content = enabled ? "Overlay on" : "Overlay off"; }

    private void RetryCapture(object sender, RoutedEventArgs e) { captureRestartRequested = true; RetryButton.IsEnabled = false; }

    private void OpenSensorDriver(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });

    private void RestartElevated(object sender, RoutedEventArgs e)

    {

        try

        {

            hotkey?.Dispose();

            Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable.")) { UseShellExecute = true, Verb = "runas" }); Close();

        }

        catch (Win32Exception error) { hotkey = new HotkeyBinding(new WindowInteropHelper(this).Handle); hotkey.Replace(preferences.Shortcut); Report("Administrator restart did not complete", error); }

    }

    private void ExportDiagnostics(object sender, RoutedEventArgs e)

    {

        SaveFileDialog dialog = new() { Filter = "Diagnostics (*.json)|*.json", FileName = "FrameTrace-diagnostics.json" };

        if (dialog.ShowDialog(this) != true) return;

        try { WriteDiagnostics(dialog.FileName); Status.Text = "Diagnostics exported."; }

        catch (Exception error) { Report("Diagnostics export failed", error); }

    }

    public void WriteDiagnostics(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new { Version = typeof(App).Assembly.GetName().Version!.ToString(3), OverlayEnabled = enabled, Displays = Desktop.Displays().Select(d => new { d.Name, Width = d.Bounds.Width, Height = d.Bounds.Height, d.Scale }), Timestamp = Environment.TickCount64, ForegroundPid = Desktop.Foreground()?.ProcessId, TargetProcess = target, ManualTarget = selectedTarget, LastError = lastError, Sensors = readings, Frames = summary, Candidates = capture?.Candidates(), RecentFrames = capture?.RecentFrames(target), CaptureMessages = capture?.Messages, CaptureActivity = capture?.Activity, TraceSessions = CaptureSessions.Names(), CpuStatus = HardwareMonitor.CpuSensorStatus }, new JsonSerializerOptions { WriteIndented = true }));

    private void Report(string operation, Exception error)

    {

        lastError = operation + ": " + error.Message; Status.Text = lastError; Diagnostics.Write(operation, error.ToString());

    }

    private async void CloseAsync(object? sender, CancelEventArgs e)

    {

        if (closed) return;

        e.Cancel = true; if (closing) return; closing = true; shutdown.Cancel();

        try { await WaitForUpdateDownloadAsync(); await monitoring; if (sensorRead is not null) await sensorRead; if (capture is not null) await capture.DisposeAsync(); }

        catch (Exception error) { Report("Capture shutdown failed", error); }

        finally { manualProcess?.Dispose(); testWindow?.Close(); hardware?.Dispose(); hotkey?.Dispose(); overlay?.Close(); shutdown.Dispose(); closed = true; Close(); }

    }

}







