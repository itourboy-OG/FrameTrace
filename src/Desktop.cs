using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Frameglass;

public sealed record ForegroundWindow(nint Handle, int ProcessId, string Title, Rect Monitor);
public sealed record DisplayInfo(string Name, Rect Bounds, double Scale, bool Primary)
{
    public Size CanvasSize => new(Bounds.Width / Scale, Bounds.Height / Scale);
    public string Label => $"{Name} · {Bounds.Width:0} × {Bounds.Height:0}";
}

/// <summary>Small native Windows adapter for foreground targeting, monitor coordinates, and the system color picker.</summary>
public static class Desktop
{
    public static ImmutableArray<DisplayInfo> Displays()
    {
        ImmutableArray<DisplayInfo>.Builder displays = ImmutableArray.CreateBuilder<DisplayInfo>();
        MonitorCallback callback = (nint monitor, nint context, nint rectangle, nint data) =>
        {
            MonitorInfoEx info = new() { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
            if (!GetMonitorInfoEx(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the display.");
            int result = GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            displays.Add(new DisplayInfo(info.Device, new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), dpiX / 96d, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate displays.");
        return displays.ToImmutable();
    }
    public static ForegroundWindow? Foreground()
    {
        nint handle = GetForegroundWindow();
        if (handle == 0) return null;
        GetWindowThreadProcessId(handle, out uint pid);
        StringBuilder title = new(512);
        GetWindowText(handle, title, title.Capacity);
        nint monitor = MonitorFromWindow(handle, 2);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the game's monitor bounds.");
        return new ForegroundWindow(handle, checked((int)pid), title.ToString(), new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top));
    }

    public static string HotkeyLabel(Hotkey key)
    {
        string prefix = ((key.Modifiers & 2) != 0 ? "Ctrl + " : "") + ((key.Modifiers & 1) != 0 ? "Alt + " : "") + ((key.Modifiers & 4) != 0 ? "Shift + " : "");
        return prefix + KeyInterop.KeyFromVirtualKey(key.Key);
    }

    public static Hotkey FromKey(Key key, ModifierKeys modifiers)
    {
        uint flags = ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) | ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0);
        if (flags == 0 || (modifiers & ModifierKeys.Windows) != 0 || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException("Hold Ctrl, Alt, or Shift, then press another key. The Windows key is reserved.");
        return new Hotkey(flags, KeyInterop.VirtualKeyFromKey(key));
    }

    public static string? PickColor(Window owner, string initial)
    {
        Preferences.ValidateColor(initial);
        uint rgb = Convert.ToUInt32(initial[1..], 16);
        nint colors = Marshal.AllocHGlobal(16 * sizeof(int));
        try
        {
            Marshal.Copy(new int[16], 0, colors, 16);
            ChooseColor data = new() { Size = Marshal.SizeOf<ChooseColor>(), Owner = new WindowInteropHelper(owner).Handle, CustomColors = colors, Flags = 0x1 | 0x2,
                RgbResult = ((rgb >> 16) & 255) | (rgb & 0xFF00) | ((rgb & 255) << 16) };
            if (!ChooseColorDialog(ref data))
            {
                uint error = CommDlgExtendedError();
                if (error != 0) throw new Win32Exception((int)error, "The Windows color picker could not open.");
                return null;
            }
            return $"#{data.RgbResult & 255:X2}{(data.RgbResult >> 8) & 255:X2}{(data.RgbResult >> 16) & 255:X2}";
        }
        finally { Marshal.FreeHGlobal(colors); }
    }

    public static void MakeClickThrough(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        nint style = GetWindowLongPtr(handle, -20);
        Marshal.SetLastPInvokeError(0);
        nint result = SetWindowLongPtr(handle, -20, style | 0x20 | 0x08000000 | 0x80);
        if (result == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot enable click-through overlay.");
    }

    public static void PlaceOverlay(Window window, Rect monitor)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, new nint(-1), (int)monitor.Left, (int)monitor.Top, (int)monitor.Width, (int)monitor.Height, 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot place the overlay on the game's monitor.");
    }

    public static void PlaceTestWindow(Window window, Rect bounds)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, 0, (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height, 0x0014))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot position the overlay test window.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoEx { public int Size; public NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(nint monitor, nint context, nint rectangle, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint context, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfoEx(nint monitor, ref MonitorInfoEx info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)] private struct ChooseColor { public int Size; public nint Owner, Instance; public uint RgbResult; public nint CustomColors; public uint Flags; public nint CustomData, Hook, Template; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW", SetLastError = true)] private static extern bool ChooseColorDialog(ref ChooseColor color);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Registers a replacement first so a shortcut conflict leaves the existing shortcut intact.</summary>
public sealed class HotkeyBinding : IDisposable
{
    private readonly nint handle;
    private int id;
    private Hotkey? current;
    public HotkeyBinding(nint handle) => this.handle = handle;
    public bool Matches(nint messageId) => id != 0 && messageId == id;
    public void Replace(Hotkey next)
    {
        if (current == next) return;
        int nextId = id == 1 ? 2 : 1;
        if (!RegisterHotKey(handle, nextId, next.Modifiers | 0x4000, (uint)next.Key)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"{Desktop.HotkeyLabel(next)} is unavailable. Choose a different shortcut.");
        if (id != 0) UnregisterHotKey(handle, id);
        id = nextId; current = next;
    }
    public void Dispose() { if (id != 0) UnregisterHotKey(handle, id); id = 0; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
