using System.ServiceProcess;

namespace FrameTrace;

public sealed record AmdServiceHealth(bool Installed, bool ExternalEventsRunning, bool CrashDefenderRunning)
{
    public bool Running => ExternalEventsRunning && CrashDefenderRunning;
}

public static class CaptureHealth
{
    public static AmdServiceHealth ReadAmdServices()
    {
        ServiceController[] services = ServiceController.GetServices();
        try
        {
            ServiceController? events = services.FirstOrDefault(service => service.ServiceName == "AMD External Events Utility");
            ServiceController? defender = services.FirstOrDefault(service => service.ServiceName == "AMD Crash Defender Service");
            return new(events is not null || defender is not null,
                events?.Status == ServiceControllerStatus.Running,
                defender?.Status == ServiceControllerStatus.Running);
        }
        finally
        {
            foreach (ServiceController service in services) service.Dispose();
        }
    }

    public static string Frames(bool captureRunning, int target, long lastFrameAt, long now)
    {
        if (!captureRunning) return "Capture health · Capture stopped. Use Restart capture.";
        if (target == 0) return "Capture health · Waiting for a game.";
        if (lastFrameAt == 0) return "Capture health · Selected game has not sent frames yet.";
        long age = Math.Max(0, now - lastFrameAt);
        return age < 3000 ? $"Capture health · Frames arriving · last frame {age / 1000d:0.0} s ago"
            : $"Capture health · No recent game frames · last frame {age / 1000d:0} s ago";
    }

    public static string Amd(AmdServiceHealth health, bool recentFrames, bool afmfReported)
    {
        if (!health.Running) return "AMD services stopped · AFMF may stay inactive. Start them as administrator.";
        if (!recentFrames) return "AMD services running · Open a game to check AFMF frame data.";
        return afmfReported ? "AMD services running · AFMF tagged in recent frames."
            : "AMD services running · No AFMF tag in recent frames; this does not prove AFMF is off.";
    }
}
