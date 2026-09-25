using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Windows;
using System.Windows.Controls;

namespace Frameglass;

public sealed record CaptureTarget(int ProcessId, DateTime Started, string Application, string Title)
{
    public string Label => ProcessId == 0 ? "Automatic — follow the active game" : $"{Title} · {Application} ({ProcessId})";
    public static CaptureTarget Automatic => new(0, DateTime.MinValue, "", "");
}

internal static class RunningApplications
{
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

    private void OpenCaptureTargets(object sender, EventArgs e) => RefreshCaptureTargets();

    internal void RefreshCaptureTargets()
    {
        try
        {
            ImmutableArray<CaptureTarget> apps = RunningApplications.List();
            if (selectedTarget.ProcessId != 0 && !apps.Any(item => item.ProcessId == selectedTarget.ProcessId && item.Started == selectedTarget.Started))
                apps = apps.Add(selectedTarget);
            refreshingTargets = true;
            CaptureTargetSelector.ItemsSource = apps.Insert(0, CaptureTarget.Automatic);
            CaptureTargetSelector.SelectedItem = ((ImmutableArray<CaptureTarget>)CaptureTargetSelector.ItemsSource)
                .Single(item => item.ProcessId == selectedTarget.ProcessId && item.Started == selectedTarget.Started);
        }
        catch (Exception error) { Report("Could not refresh running applications", error); }
        finally { refreshingTargets = false; }
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
        }
        catch (Exception error)
        {
            Report("Cannot select this application", error);
            RefreshCaptureTargets();
        }
        finally { next?.Dispose(); }
    }
}
