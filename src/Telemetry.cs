using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using Microsoft.VisualBasic.FileIO;

namespace Frameglass;

public sealed record SensorReading(string Device, string HardwareType, string Name, string Kind, double? Value, string Unit);
public sealed record FrameReading(long ReceivedAt, int ProcessId, string Application, string Runtime, string SwapChain, string FrameType, string PresentMode, double? FrameTime, double? DisplayedTime, double StartedAt);
public sealed record FrameSummary(double? AppFps, double? DisplayFps, double? FrameTime, string Generation, string PresentMode, ImmutableArray<double> Times)
{
    public double? AverageFps { get; init; }
    public double? LowFps { get; init; }
    public int SampleCount { get; init; }
    public ImmutableArray<FramePoint> Points { get; init; } = [];
}
public sealed record FramePoint(double At, double Milliseconds);

/// <summary>Reads vendor-provided sensors without changing hardware settings. Missing values remain unavailable.</summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };

    public HardwareMonitor() => computer.Open();

    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string CpuSensorStatus => !PawnIo.IsInstalled
        ? "CPU readings need the PawnIO helper. Use sensor setup, then restart this app as administrator."
        : !IsElevated() ? "Temperature and power need permission to read the CPU. Use Restart as administrator."
        : "CPU sensor helper detected. Readings depend on hardware and driver support.";

    public ImmutableArray<SensorReading> Read()
    {
        return computer.Hardware.SelectMany(ReadHardware).ToImmutableArray();
    }

    private static IEnumerable<SensorReading> ReadHardware(IHardware hardware)
    {
        hardware.Update();
        bool cpuRegistersAccessible = hardware.HardwareType != HardwareType.Cpu || (PawnIo.IsInstalled && IsElevated());
        SensorReading[] readings = hardware.Sensors.Select(sensor => new SensorReading(
            hardware.Name, hardware.HardwareType.ToString(), sensor.Name, sensor.SensorType.ToString(),
            (!cpuRegistersAccessible && sensor.SensorType != SensorType.Load) ? null : sensor.Value is float value && float.IsFinite(value) ? value : null,
            sensor.SensorType switch
            {
                SensorType.Load => "%", SensorType.Temperature => "°C", SensorType.Power => "W",
                SensorType.Clock => "MHz", SensorType.Data => "GB", SensorType.SmallData => "MB",
                SensorType.Fan => "RPM", SensorType.Voltage => "V", SensorType.Throughput => "B/s",
                _ => sensor.SensorType.ToString()
            })).ToArray();
        return readings.Concat(hardware.SubHardware.SelectMany(ReadHardware));
    }

    public void Dispose() => computer.Close();
}


