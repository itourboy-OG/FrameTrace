using System.Windows;

namespace Frameglass;

public partial class MainWindow
{
    private OverlayTestWindow? testWindow;
    private bool enabledBeforeTest;

    private void ToggleOverlayTest(object sender, RoutedEventArgs e)
    {
        if (!ready || overlay is null) return;
        if (testWindow is not null) { testWindow.Close(); return; }
        enabledBeforeTest = enabled; enabled = true;
        testWindow = new OverlayTestWindow();
        testWindow.Closed += (_, _) =>
        {
            testWindow = null; enabled = enabledBeforeTest;
            overlay.Hide(); overlay.Apply(preferences);
            GameName.Text = "Waiting for your game";
            target = 0; summary = FrameMetrics.Summarize([], Environment.TickCount64);
            TestOverlayButton.Content = "Test overlay";
            StudioStatus.Text = "Test closed. Unsaved edits stay in the studio; Save & apply keeps them for games.";
        };
        testWindow.Show();
        if (DisplaySelector.SelectedItem is DisplayInfo display)
            Desktop.PlaceTestWindow(testWindow, new Rect(display.Bounds.X + 40, display.Bounds.Y + 80, 900 * display.Scale, 520 * display.Scale));
        TestOverlayButton.Content = "Stop test";
        RefreshTestOverlay(); Activate();
        StudioStatus.Text = "Live test running. Edit any layout control or drag sections; Save & apply keeps your changes.";
    }

    private void RefreshTestOverlay()
    {
        if (testWindow is null || overlay is null) return;
        overlay.Apply(draft);
        overlay.UpdateData(OverlayData.Build(readings, summary));
        overlay.Surface.UpdateGraph(summary.Points);
        if (enabled && DisplaySelector.SelectedItem is DisplayInfo display) overlay.ShowOnMonitor(display.Bounds);
    }
}
