using System.IO;
using System.Runtime.InteropServices;

namespace FrameTrace;

internal sealed record NvidiaOverrides(string? SuperResolutionPreset, string? FrameGenerationPreset, string? RayReconstructionPreset)
{
    public bool HasPreset => SuperResolutionPreset is not null || FrameGenerationPreset is not null || RayReconstructionPreset is not null;
}

internal static class NvidiaOverrideDetection
{
    private const ulong Enabled = 1UL << 1;
    private const ulong Preset = 1UL << 5;
    private const ulong Evaluating = 1UL << 10;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiCall();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetOverrideState(ref OverrideState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct OverrideState
    {
        public uint Version, ProcessIdentifier;
        public ulong FeedbackMaskSR, FeedbackMaskRR, FeedbackMaskFG;
        public float ScalingRatio;
        public uint PerformanceMode, RenderPreset, FrameGenerationCount, FrameGenerationPreset, FrameGenerationMode, Reserved0, Reserved1;
    }

    public static NvidiaOverrides? Inspect(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"), out IntPtr library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out IntPtr queryPointer))
                throw new InvalidOperationException("The NVIDIA driver does not export nvapi_QueryInterface.");
            QueryInterface query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(queryPointer);
            IntPtr initializePointer = query(0x0150e828);
            IntPtr unloadPointer = query(0xd22bdd7e);
            IntPtr statePointer = query(0x3fd96fba);
            if (statePointer == IntPtr.Zero) return null;
            if (initializePointer == IntPtr.Zero || unloadPointer == IntPtr.Zero)
                throw new InvalidOperationException("The NVIDIA driver does not expose NVAPI initialization or cleanup.");
            int initialization = Marshal.GetDelegateForFunctionPointer<NvApiCall>(initializePointer)();
            if (initialization != 0) throw new InvalidOperationException($"NVAPI initialization failed with status {initialization}.");
            OverrideState state = new() { Version = (uint)(Marshal.SizeOf<OverrideState>() | (1 << 16)), ProcessIdentifier = (uint)processId };
            int status = Marshal.GetDelegateForFunctionPointer<GetOverrideState>(statePointer)(ref state);
            int cleanup = Marshal.GetDelegateForFunctionPointer<NvApiCall>(unloadPointer)();
            if (cleanup != 0) throw new InvalidOperationException($"NVAPI cleanup failed after reading process {processId} with status {cleanup}.");
            if (status == -121) return null; // NVAPI_DATA_NOT_FOUND: this process has no override data.
            if (status != 0) throw new InvalidOperationException($"NVIDIA DLSS override read failed for process {processId} with NVAPI status {status}.");
            return ReadPresets(state.FeedbackMaskSR, state.FeedbackMaskRR, state.FeedbackMaskFG,
                state.RenderPreset, state.FrameGenerationPreset);
        }
        finally { NativeLibrary.Free(library); }
    }

    internal static NvidiaOverrides ReadPresets(ulong srMask, ulong rrMask, ulong fgMask, uint renderPreset, uint fgPreset)
    {
        bool srActive = (srMask & (Enabled | Evaluating)) == (Enabled | Evaluating);
        bool rrActive = (rrMask & (Enabled | Evaluating)) == (Enabled | Evaluating);
        if (srActive && rrActive)
            throw new InvalidDataException("NVIDIA reported both Super Resolution and Ray Reconstruction as evaluating, but provides only one shared render preset.");
        bool fgOverride = (fgMask & (Enabled | Preset)) == (Enabled | Preset);
        return new NvidiaOverrides(srActive ? PresetName(renderPreset) : null,
            fgOverride ? PresetName(fgPreset) : null,
            rrActive ? PresetName(renderPreset) : null);
    }

    private static string PresetName(uint value) => value switch
    {
        0 => "Driver default",
        0x00ffffff => "Latest",
        >= 1 and <= 26 => "Preset " + (char)('A' + value - 1),
        _ => $"Preset {value}"
    };
}
