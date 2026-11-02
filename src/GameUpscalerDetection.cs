using System.Diagnostics;
using System.IO;

namespace FrameTrace;

internal static class GameUpscalerDetection
{
    public static UpscalerObservation InspectLoadedModules(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        return Identify(process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName));
    }

    public static UpscalerObservation Identify(IEnumerable<string> moduleNames)
    {
        string[] names = moduleNames.Select(name => Path.GetFileName(name) ?? throw new InvalidDataException("A game module has no filename.")).ToArray();
        bool amd = names.Any(IsUpscalerModule);
        bool intel = names.Any(name => name.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("libxess_dx11.dll", StringComparison.OrdinalIgnoreCase));
        bool nvidia = names.Any(name => name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("sl.dlss.dll", StringComparison.OrdinalIgnoreCase));
        List<string> runtimes = [];
        if (amd) runtimes.Add("AMD FSR");
        if (intel) runtimes.Add("Intel XeSS");
        if (nvidia) runtimes.Add("NVIDIA DLSS");
        return runtimes.Count == 0 ? UpscalerObservation.Unknown
            : new(string.Join(" and ", runtimes) + (runtimes.Count == 1 ? " runtime loaded" : " runtimes loaded") + " · active mode unknown", true);
    }

    private static bool IsUpscalerModule(string name) =>
        name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ffx_fsr3upscaler_x64.dll", StringComparison.OrdinalIgnoreCase);
}
