using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FrameTrace;

public sealed record CaptureTarget(int ProcessId, DateTime Started, string Application, string Title)
{
    public bool IsPresenting { get; init; }
    public string Label => ProcessId == 0 ? "Automatic — follow the active game" : $"{(IsPresenting ? "● LIVE FPS · " : "")}{Application} · {Title} ({ProcessId})";
    public static CaptureTarget Automatic => new(0, DateTime.MinValue, "", "");
}

internal static class CaptureRules
{
    public static ImmutableArray<string> Allow(ImmutableArray<string> ignored, string application) =>
        ignored.Where(name => !name.Equals(application, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();

    public static ImmutableArray<string> Ignore(ImmutableArray<string> ignored, string application) =>
        ignored.Contains(application, StringComparer.OrdinalIgnoreCase) ? ignored : ignored.Add(application);
}

internal static class RunningApplications
{
    public static CaptureTarget? FromFrames(CaptureCandidate candidate)
    {
        using Process process = Process.GetProcessById(candidate.ProcessId);
        string path = ExecutablePath(candidate.ProcessId);
        string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || !candidate.Application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? null : new CaptureTarget(candidate.ProcessId, process.StartTime.ToUniversalTime(), candidate.Application, "Rendering process") { IsPresenting = true };
    }

    public static BitmapSource Icon(int pid)
    {
        string path = ExecutablePath(pid);
        uint count = ExtractIconEx(path, 0, out nint large, out nint small, 1);
        try
        {
            if (count == 0 || small == 0) throw new InvalidDataException($"No application icon was found in {path}.");
            BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(small, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
            icon.Freeze();
            return icon;
        }
        finally
        {
            if (small != 0) DestroyIcon(small);
            if (large != 0) DestroyIcon(large);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint icons);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    private static string ExecutablePath(int pid)
    {
        using SafeProcessHandle handle = OpenProcess(0x1000, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the application path.");
        StringBuilder path = new(32768);
        int size = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the application path.");
        return path.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    public static ImmutableArray<CaptureTarget> List()
    {
        string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        ImmutableArray<CaptureTarget>.Builder targets = ImmutableArray.CreateBuilder<CaptureTarget>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    string path = ExecutablePath(process.Id);
                    if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(new CaptureTarget(process.Id, process.StartTime.ToUniversalTime(), Path.GetFileName(path), process.MainWindowTitle));
                }
                catch (Win32Exception error) { Diagnostics.Write("process-list-unavailable", System.Text.Json.JsonSerializer.Serialize(new { Pid = process.Id, Error = error.NativeErrorCode })); }
                catch (InvalidOperationException error) { Diagnostics.Write("process-list-changed", System.Text.Json.JsonSerializer.Serialize(new { Pid = process.Id, Error = error.Message })); }
            }
        }
        return targets.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ProcessId).ToImmutableArray();
    }
}

public partial class MainWindow
{
    private Process? manualProcess;
    private CaptureTarget selectedTarget = CaptureTarget.Automatic;
    private bool refreshingTargets;

    private void OpenCaptureTargetsBeforeMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !CaptureTargetSelector.IsDropDownOpen) RefreshCaptureTargets();
    }

    private void OpenCaptureTargetsBeforeKey(object sender, KeyEventArgs e)
    {
        if (!CaptureTargetSelector.IsDropDownOpen && (e.Key == Key.F4 || e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)) RefreshCaptureTargets();
    }

    internal void RefreshCaptureTargets()
    {
        try
        {
            ImmutableArray<CaptureCandidate> rendering = [];
            try { rendering = capture?.Candidates() ?? []; }
            catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
            {
                Diagnostics.Write("capture-target-frames-unavailable", System.Text.Json.JsonSerializer.Serialize(new { Error = error.Message }));
            }
            ImmutableArray<CaptureTarget> apps = RunningApplications.List().Select(item =>
                rendering.FirstOrDefault(candidate => candidate.ProcessId == item.ProcessId) is { } candidate
                    ? item with { Application = candidate.Application, IsPresenting = true } : item).ToImmutableArray();
            foreach (CaptureCandidate candidate in rendering.Where(item => item.ProcessId != Environment.ProcessId && !apps.Any(app => app.ProcessId == item.ProcessId)))
            {
                try
                {
                    CaptureTarget? extra = RunningApplications.FromFrames(candidate);
                    if (extra is not null) apps = apps.Add(extra);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    Diagnostics.Write("capture-target-changed", System.Text.Json.JsonSerializer.Serialize(new { candidate.ProcessId, Error = error.Message }));
                }
            }
            if (selectedTarget.ProcessId != 0 && !apps.Any(item => item.ProcessId == selectedTarget.ProcessId && item.Started == selectedTarget.Started))
                apps = apps.Add(selectedTarget);
            refreshingTargets = true;
            CaptureTargetSelector.ItemsSource = apps.OrderByDescending(item => item.IsPresenting).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToImmutableArray().Insert(0, CaptureTarget.Automatic);
            CaptureTargetSelector.SelectedItem = ((ImmutableArray<CaptureTarget>)CaptureTargetSelector.ItemsSource)
                .Single(item => item.ProcessId == selectedTarget.ProcessId && item.Started == selectedTarget.Started);
        }
        catch (Exception error) { Report("Could not refresh running applications", error); }
        finally { refreshingTargets = false; }
        RefreshCaptureRuleButtons();
    }

    private void ChangeCaptureTarget(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingTargets || CaptureTargetSelector.SelectedItem is not CaptureTarget choice) return;
        Process? next = null;
        try
        {
            if (choice.ProcessId != 0)
            {
                next = Process.GetProcessById(choice.ProcessId);
                if (next.HasExited || next.StartTime.ToUniversalTime() != choice.Started)
                    throw new InvalidOperationException("The selected application has closed. Refresh the list and select its new instance.");
            }
            manualProcess?.Dispose(); manualProcess = next; next = null; selectedTarget = choice;
            target = 0; summary = FrameMetrics.Summarize([], Environment.TickCount64); overlay?.Hide();
            Diagnostics.Write("capture-selection", System.Text.Json.JsonSerializer.Serialize(choice));
            RefreshCaptureRuleButtons();
        }
        catch (Exception error)
        {
            Report("Cannot select this application", error);
            RefreshCaptureTargets();
        }
        finally { next?.Dispose(); }
    }

    private void RefreshCaptureRuleButtons()
    {
        CaptureTarget? choice = CaptureTargetSelector.SelectedItem as CaptureTarget;
        bool selected = choice is { ProcessId: not 0 };
        bool ignored = selected && preferences.IgnoredApps.Contains(choice!.Application, StringComparer.OrdinalIgnoreCase);
        AllowCaptureButton.IsEnabled = ignored;
        IgnoreCaptureButton.IsEnabled = selected && !ignored;
        CaptureRuleStatus.Text = !selected ? "Select a running app to allow or ignore it in automatic capture."
            : ignored ? $"{choice!.Application} is excluded as a capture target."
            : $"{choice!.Application} can be captured.";
    }

    private void AllowCaptureTarget(object sender, RoutedEventArgs e)
    {
        if (CaptureTargetSelector.SelectedItem is not CaptureTarget { ProcessId: not 0 } choice) return;
        try
        {
            SaveCaptureRules(CaptureRules.Allow(ReadIgnoredApps(), choice.Application));
            CaptureRuleStatus.Text = $"Saved · {choice.Application} can be captured again.";
        }
        catch (Exception error) { Report("Could not allow automatic capture", error); }
    }

    private void IgnoreCaptureTarget(object sender, RoutedEventArgs e)
    {
        if (CaptureTargetSelector.SelectedItem is not CaptureTarget { ProcessId: not 0 } choice) return;
        try
        {
            SaveCaptureRules(CaptureRules.Ignore(ReadIgnoredApps(), choice.Application));
            CaptureRuleStatus.Text = $"Saved · {choice.Application} is excluded as a capture target.";
        }
        catch (Exception error) { Report("Could not exclude this application", error); }
    }

    private ImmutableArray<string> ReadIgnoredApps() => IgnoredApps.Text
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    private void SaveCaptureRules(ImmutableArray<string> ignored)
    {
        Preferences updated = Preferences.Validate(preferences with { IgnoredApps = ignored });
        Preferences.Save(updated);
        preferences = updated;
        draft = draft with { IgnoredApps = ignored };
        string settingsState = SettingsSaveState.Text;
        IgnoredApps.Text = string.Join(Environment.NewLine, ignored);
        SettingsSaveState.Text = settingsState;
        CaptureTargetSelector.SelectedIndex = 0;
        RefreshCaptureRuleButtons();
    }
}
