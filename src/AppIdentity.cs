using System.IO;

namespace FrameTrace;

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
        "FrameTrace"
#endif
    );

    public static string StableDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrace");
    public static string LegacyDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frameglass");
    public static string SupportReportsDirectory
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) throw new DirectoryNotFoundException("Windows Documents folder is unavailable. Choose a Documents location in Windows before saving a support log.");
            return Path.Combine(documents, "Frame Trace Logs");
        }
    }
}
