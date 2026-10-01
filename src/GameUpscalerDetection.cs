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
        return (amd, intel) switch
        {
            (true, true) => new("AMD FSR and Intel XeSS runtimes loaded · active mode unknown", true),
            (true, false) => new("AMD FSR runtime loaded · active mode unknown", true),
            (false, true) => new("Intel XeSS runtime loaded · active mode unknown", true),
            _ => UpscalerObservation.Unknown
        };
    }

    private static bool IsUpscalerModule(string name) =>
        name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ffx_fsr3upscaler_x64.dll", StringComparison.OrdinalIgnoreCase);
}
