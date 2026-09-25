using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Frameglass;

/// <summary>Reclaims only Frame Trace trace sessions whose owning dashboard process has exited.</summary>
internal static class CaptureSessions
{
    public static ImmutableArray<string> Names()
    {
        int headerSize = Marshal.SizeOf<TraceProperties>();
        int bufferSize = headerSize + 4096;
        for (int capacity = 64; ; capacity *= 2)
        {
            nint[] buffers = new nint[capacity];
            try
            {
                for (int i = 0; i < capacity; i++)
                {
                    buffers[i] = Marshal.AllocHGlobal(bufferSize);
                    TraceProperties properties = new()
                    {
                        Wnode = new WnodeHeader { BufferSize = (uint)bufferSize },
                        LoggerNameOffset = (uint)headerSize,
                        LogFileNameOffset = (uint)(headerSize + 2048)
                    };
                    Marshal.StructureToPtr(properties, buffers[i], false);
                }
                uint error = QueryAllTraces(buffers, (uint)capacity, out uint count);
                if (error == 234) continue; // Windows reports a larger session list; query it again with room.
                if (error != 0) throw new Win32Exception((int)error, "Cannot inspect Windows capture sessions.");
                return buffers.Take((int)count).Select(buffer =>
                {
                    TraceProperties properties = Marshal.PtrToStructure<TraceProperties>(buffer);
                    return Marshal.PtrToStringUni(buffer + (int)properties.LoggerNameOffset)
                        ?? throw new InvalidOperationException("Windows returned a trace session without a name.");
                }).ToImmutableArray();
            }
            finally { foreach (nint buffer in buffers) if (buffer != 0) Marshal.FreeHGlobal(buffer); }
        }
    }

    public static void RemoveOrphans()
    {
        foreach (string name in Names())
        {
            string[] parts = name.Split('-');
            if (parts.Length != 3 || parts[0] is not ("Frameglass" or "FrameTrace")
                || !int.TryParse(parts[1], out int owner) || owner <= 0 || !Guid.TryParseExact(parts[2], "N", out _)) continue;
            if (OwnerExists(owner)) continue;
            int headerSize = Marshal.SizeOf<TraceProperties>();
            nint buffer = Marshal.AllocHGlobal(headerSize + 4096);
            uint error;
            try
            {
                TraceProperties properties = new()
                {
                    Wnode = new WnodeHeader { BufferSize = (uint)(headerSize + 4096) },
                    LoggerNameOffset = (uint)headerSize,
                    LogFileNameOffset = (uint)(headerSize + 2048)
                };
                Marshal.StructureToPtr(properties, buffer, false);
                error = ControlTrace(0, name, buffer, 1);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (error == 4201) continue; // Another instance already stopped this session.
            if (error != 0) throw new Win32Exception((int)error, $"Cannot release abandoned capture session '{name}'. Close Frame Trace and reopen it as administrator.");
            Diagnostics.Write("capture-orphan-released", name);
        }
    }

    private static bool OwnerExists(int pid)
    {
        try { using Process owner = Process.GetProcessById(pid); return !owner.HasExited; }
        catch (ArgumentException) { return false; } // The PID no longer exists; a reused live PID is conservatively preserved.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long Timestamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [DllImport("advapi32.dll", EntryPoint = "QueryAllTracesW", CharSet = CharSet.Unicode)]
    private static extern uint QueryAllTraces([In] nint[] properties, uint count, out uint actualCount);
    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong handle, string name, nint properties, uint control);
}
