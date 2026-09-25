using System.IO;

namespace Frameglass;

internal static class AppIdentity
{
    public static string ProductName
    {
        get
        {
#if PREVIEW_BUILD
            return "Frame Trace Preview";
#else
            return "Frame Trace";
#endif
        }
    }

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if PREVIEW_BUILD
        "FrameTracePreview"
#else
        "Frameglass"
#endif
    );
}
