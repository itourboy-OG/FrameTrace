using Microsoft.Win32;

namespace Frameglass;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The Frame Trace executable path is unavailable.");
            using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                ?? throw new InvalidOperationException("Windows could not open the current user's startup settings.");
            runKey.SetValue(AppIdentity.ProductName, $"\"{executable}\"", RegistryValueKind.String);
            return;
        }

        using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        existingKey?.DeleteValue(AppIdentity.ProductName, false);
    }
}
