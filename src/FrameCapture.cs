using System.Collections.Immutable;

using System.ComponentModel;

using System.Diagnostics;

using System.Globalization;

using System.IO;

using System.Text.Json;

using Microsoft.VisualBasic.FileIO;



namespace Frameglass;



public sealed record CaptureActivity(string Session, int HelperPid, long TotalRows, long? LastFrameAgeMs);

public sealed record CaptureCandidate(int ProcessId, string Application, int FrameCount, string Runtime, long LastFrame);



public static class Diagnostics

{

    private static readonly object Gate = new();

    public static string PathName => Path.Combine(AppIdentity.DataDirectory, "diagnostics.jsonl");

    public static void Write(string operation, string detail)

    {

        lock (Gate)

        {

        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);

        if (File.Exists(PathName) && new FileInfo(PathName).Length > 2_000_000) File.Move(PathName, PathName + ".previous", true);

        File.AppendAllText(PathName, JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, Operation = operation, Detail = detail }) + Environment.NewLine);

        }

    }

}



/// <summary>Pure PresentMon parsing and calculations. Presentation FPS is never treated as proof of native rendered FPS.</summary>

public static class FrameMetrics

{

    public static string[] SplitCsv(string line)

    {

        if (!line.Contains('"')) return line.Split(',');

        using TextFieldParser parser = new(new StringReader(line)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };

        parser.SetDelimiters(",");

        return parser.ReadFields() ?? throw new InvalidDataException("PresentMon emitted an empty CSV record.");

    }



    public static void ValidateHeader(string[] header)

    {

        foreach (string column in new[] { "Application", "ProcessID", "PresentRuntime", "SwapChainAddress", "FrameType", "PresentMode", "FrameTime", "DisplayedTime", "CPUStartQPCTime" })

            if (header.Count(name => name == column) != 1) throw new InvalidDataException($"PresentMon CSV must contain exactly one '{column}' column. Actual: {string.Join(',', header)}");

    }



    public static FrameReading Parse(string[] header, string line, long now)

    {

        string[] fields = SplitCsv(line);

        if (fields.Length != header.Length) throw new InvalidDataException($"PresentMon CSV has {fields.Length} fields; expected {header.Length}. Record: {line}");

        string Field(string name) => fields[Array.IndexOf(header, name)];

        if (!int.TryParse(Field("ProcessID"), CultureInfo.InvariantCulture, out int pid) || pid <= 0) throw new InvalidDataException($"Invalid PresentMon ProcessID '{Field("ProcessID")}'.");

        if (!double.TryParse(Field("CPUStartQPCTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out double started) || !double.IsFinite(started) || started < 0) throw new InvalidDataException("Invalid CPUStartQPCTime.");

        return new FrameReading(now, pid, Field("Application"), Field("PresentRuntime"), Field("SwapChainAddress"), Field("FrameType"), Field("PresentMode"), Duration(Field("FrameTime")), Duration(Field("DisplayedTime")), started);

    }



    private static double? Duration(string text)

    {

        if (text is "NA" or "N/A" or "") return null;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0)

            throw new InvalidDataException($"Invalid PresentMon duration '{text}'; expected nonnegative milliseconds or NA.");

        return value > 0 ? value : null;

    }



    public static FrameSummary Summarize(ImmutableArray<FrameReading> frames, long now)

    {

        FrameReading[] recent = frames.Where(frame => now - frame.ReceivedAt < 3000).ToArray();

        // ETW delivery can arrive in batches over 1.5 seconds apart. Expire chains at the same three-second boundary as the sample window.

        FrameReading[] active = recent.GroupBy(frame => frame.SwapChain)
            .MaxBy(group => (group.Count(frame => now - frame.ReceivedAt < 1500), group.Max(frame => frame.ReceivedAt)))?.ToArray() ?? [];

        double[] times = active.Where(frame => frame.FrameType == "Application" && frame.FrameTime.HasValue).Select(frame => frame.FrameTime!.Value).ToArray();

        double[] displayed = active.Where(frame => frame.DisplayedTime.HasValue).Select(frame => frame.DisplayedTime!.Value).ToArray();

        string[] generated = active.Select(frame => frame.FrameType).Where(type => type is not ("Application" or "NotSet" or "Unknown" or "Repeated")).Distinct().ToArray();

        string? chain = active.LastOrDefault()?.SwapChain;

        FrameReading[] history = frames.Where(frame => frame.SwapChain == chain && frame.FrameType == "Application" && frame.FrameTime.HasValue && now - frame.ReceivedAt < 30000).ToArray();

        double[] historyTimes = history.Select(frame => frame.FrameTime!.Value).ToArray();
        double? frameTime = times.Length > 1 ? times.Average() : null;

        return new FrameSummary(frameTime.HasValue ? 1000 / frameTime.Value : null, displayed.Length > 1 ? 1000 / displayed.Average() : null,

            frameTime, generated.Length > 0 ? string.Join(" + ", generated.Select(TechnologyLabel)) : "Unavailable",

            active.LastOrDefault()?.PresentMode ?? "No fresh frames", times.TakeLast(180).ToImmutableArray())

        {

            AverageFps = times.Length > 1 && historyTimes.Length > 1 ? 1000 / historyTimes.Average() : null,

            LowFps = times.Length > 1 && historyTimes.Length >= 100 ? 1000 / historyTimes.OrderDescending().Take((int)Math.Ceiling(historyTimes.Length * 0.01)).Average() : null,

            SampleCount = historyTimes.Length,

            Points = history.Where(frame => now - frame.ReceivedAt < 12000).Select(frame => new FramePoint(frame.StartedAt, frame.FrameTime!.Value)).ToImmutableArray()

        };

    }

    private static string TechnologyLabel(string frameType) => frameType == "AMD AFMF" ? "AFMF 2.1" : frameType + " reported";



    public static CaptureCandidate? SelectForeground(ImmutableArray<CaptureCandidate> candidates, int foregroundPid, ImmutableArray<string> ignored, int ownPid)

    {

        return candidates.FirstOrDefault(candidate => candidate.ProcessId == foregroundPid && candidate.ProcessId != ownPid && candidate.FrameCount >= 2

            && !ignored.Contains(candidate.Application, StringComparer.OrdinalIgnoreCase));

    }

}



/// <summary>One continuous ETW stream for all processes. Changing game or display mode does not restart measurement.</summary>

public sealed class FrameCapture : IAsyncDisposable

{

    private readonly Process process;
    private readonly CaptureJob lifetime;

    private readonly Task reader;

    private readonly Task errorReader;

    private readonly string session;

    private readonly string executable;

    private readonly object gate = new();

    private readonly Queue<FrameReading> frames = new();

    private readonly Queue<string> warnings = new();

    private string lastRow = "";

    private bool disposed;

    private long totalRows, lastReceived;

    public CaptureActivity Activity { get { lock (gate) return new(session, process.Id, totalRows, totalRows == 0 ? null : Environment.TickCount64 - lastReceived); } }



    public FrameCapture()

    {

        executable = Path.Combine(AppContext.BaseDirectory, "vendor", "PresentMon.exe");

        if (!File.Exists(executable)) throw new FileNotFoundException("Reinstall the app to restore vendor/PresentMon.exe.", executable);

        CaptureSessions.RemoveOrphans();

        session = $"FrameTrace-{Environment.ProcessId}-{Guid.NewGuid():N}";

        ProcessStartInfo start = StartInfo(executable);

        foreach (string argument in new[] { "--output_stdout", "--no_console_stats", "--track_frame_type", "--v2_metrics", "--qpc_time_ms", "--session_name", session }) start.ArgumentList.Add(argument);

        lifetime = new CaptureJob();
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("Windows could not start PresentMon.");
            try { lifetime.Attach(process); }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        }
        catch { lifetime.Dispose(); throw; }

        errorReader = Task.Run(ReadWarningsAsync);

        reader = Task.Run(ReadAsync);

        Diagnostics.Write("capture-start", $"PID={process.Id}; session={session}; target=all processes");

    }



    private static ProcessStartInfo StartInfo(string executable) => new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };



    private async Task ReadWarningsAsync()

    {

        while (await process.StandardError.ReadLineAsync() is string line)

        {

            lock (gate) { if (warnings.Count >= 30) warnings.Dequeue(); warnings.Enqueue(line); }

            Diagnostics.Write("presentmon-message", line);

        }

    }



    private async Task ReadAsync()

    {

        string[]? header = null;

        while (await process.StandardOutput.ReadLineAsync() is string line)

        {

            if (line.Length == 0) continue;

            lastRow = line;

            if (header is null) { header = FrameMetrics.SplitCsv(line); FrameMetrics.ValidateHeader(header); continue; }

            long now = Environment.TickCount64;

            FrameReading frame = FrameMetrics.Parse(header, line, now);

            lock (gate)

            {

                while (frames.TryPeek(out FrameReading? first) && (now - first.ReceivedAt >= 30000 || frames.Count >= 65536)) frames.Dequeue();

                frames.Enqueue(frame);

                totalRows++; lastReceived = now;

            }

        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0) throw new InvalidOperationException($"PresentMon exited with code {process.ExitCode}. {Messages}");

    }



    public string Messages { get { lock (gate) return string.Join(Environment.NewLine, warnings); } }



    public void CheckHealth()

    {

        if (reader.IsFaulted) throw new InvalidOperationException($"PresentMon parsing stopped. Last record: {lastRow}. {Messages}", reader.Exception!.GetBaseException());

        if (errorReader.IsFaulted) throw new IOException("Capture diagnostics could not be recorded.", errorReader.Exception!.GetBaseException());

        if (process.HasExited) throw new InvalidOperationException($"PresentMon stopped (exit {process.ExitCode}). {Messages}");

    }



    public ImmutableArray<CaptureCandidate> Candidates()

    {

        CheckHealth();

        lock (gate) return frames.Where(frame => Environment.TickCount64 - frame.ReceivedAt < 3000).GroupBy(frame => frame.ProcessId)

            .Select(group => new CaptureCandidate(group.Key, group.Last().Application, group.Count(), group.Last().Runtime, group.Max(frame => frame.ReceivedAt))).ToImmutableArray();

    }



    public FrameSummary ReadSummary(int processId)

    {

        CheckHealth();

        ImmutableArray<FrameReading> selected;

        lock (gate) selected = frames.Where(frame => frame.ProcessId == processId).ToImmutableArray();

        return FrameMetrics.Summarize(selected, Environment.TickCount64);

    }



    public ImmutableArray<FrameReading> RecentFrames(int processId)

    {

        lock (gate) return frames.Where(frame => frame.ProcessId == processId).TakeLast(240).ToImmutableArray();

    }



    public async ValueTask DisposeAsync()

    {

        if (disposed) return;

        disposed = true;

        try

        {

            if (!process.HasExited)

            {

                ProcessStartInfo stopInfo = StartInfo(executable);

                foreach (string argument in new[] { "--session_name", session, "--terminate_existing_session" }) stopInfo.ArgumentList.Add(argument);

                using Process stop = Process.Start(stopInfo) ?? throw new Win32Exception("Could not stop the capture session.");

                Task<string> error = stop.StandardError.ReadToEndAsync();

                Task<string> output = stop.StandardOutput.ReadToEndAsync();

                await stop.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));

                await output;

                if (stop.ExitCode != 0) throw new InvalidOperationException($"Cannot stop session '{session}': {await error}");

                // A faulted parser no longer drains stdout; kill only our own child after terminating its ETW session.

                if (reader.IsFaulted && !process.HasExited) process.Kill();

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));

            }

            if (reader.IsFaulted) Diagnostics.Write("capture-reader-failed", reader.Exception!.GetBaseException().ToString());

            else await reader;

            await errorReader;

        }

        finally { lifetime.Dispose(); process.Dispose(); }

    }

}










