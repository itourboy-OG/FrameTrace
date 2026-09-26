using System.IO;
using System.Windows;

namespace Frameglass;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if !PREVIEW_BUILD
        try { DataMigration.MoveLegacyDirectory(AppIdentity.LegacyDataDirectory, AppIdentity.StableDataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Frame Trace could not migrate its local data.\n\n" + error.Message, "Local data migration failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
#endif
        if (e.Args.Length == 2 && e.Args[0] == "--capture-lifetime-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await using (FrameCapture capture = new())
            {
                File.WriteAllText(e.Args[1], "Ready");
                await Task.Delay(30000);
            }
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--capture-probe")
        {
            await SmokeTest.RunProbeAsync(e.Args[1]);
            Shutdown(Environment.ExitCode);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--studio-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await SmokeTest.RunStudioAsync(Path.GetFullPath(e.Args[1]));
            Shutdown(Environment.ExitCode);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            await SmokeTest.RunAsync(Path.GetFullPath(e.Args[1]));
            Shutdown(Environment.ExitCode);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}




