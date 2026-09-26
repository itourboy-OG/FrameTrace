using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace Frameglass;

public partial class MainWindow
{
    private void OpenBugReport(object sender, RoutedEventArgs e) => OpenCommunityPage(new Uri("https://github.com/itourboy-OG/FrameTrace/issues/new?template=bug_report.yml"));
    private void OpenFeedback(object sender, RoutedEventArgs e) => OpenCommunityPage(new Uri("https://github.com/itourboy-OG/FrameTrace/issues/new?template=feedback.yml"));

    private void OpenCommunityPage(Uri page)
    {
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception error) { Report("Could not open GitHub; check your default browser", error); }
    }
}
