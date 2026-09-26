using System.IO;

namespace Frameglass;

internal static class DataMigration
{
    internal static void MoveLegacyDirectory(string legacyDirectory, string currentDirectory)
    {
        if (!Directory.Exists(legacyDirectory)) return;
        if (Directory.Exists(currentDirectory) || File.Exists(currentDirectory))
            throw new IOException($"Cannot migrate local data because both ‘{legacyDirectory}’ and ‘{currentDirectory}’ exist. Close Frame Trace and review these folders before retrying. Neither folder has been changed.");
        Directory.Move(legacyDirectory, currentDirectory);
    }
}
