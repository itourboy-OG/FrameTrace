using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace Frameglass;

public partial class MainWindow
{
    private bool updateCheckRunning, updateDownloading;
    private UpdateCheckResult? availableUpdate;
    private Task<string>? updateDownload;

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (updateCheckRunning || updateDownloading) return;
        updateCheckRunning = true;
        CheckForUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        UpdateCheckStatus.Text = "Checking GitHub for the latest stable release…";
        try
        {
            Version currentVersion = typeof(App).Assembly.GetName().Version
                ?? throw new InvalidDataException("The installed Frame Trace version is missing.");
            UpdateCheckResult result = await UpdateChecker.CheckAsync(currentVersion, shutdown.Token);
            if (result.Availability == UpdateAvailability.Available) ShowAvailableUpdate(result);
            else
            {
                availableUpdate = null;
                UpdateCheckStatus.Text = result.Availability == UpdateAvailability.UpToDate
                    ? $"You're up to date · v{currentVersion.ToString(3)}."
                    : "No public release is available yet.";
                UpdateBanner.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            availableUpdate = null;
            UpdateCheckStatus.Text = "Update check failed: " + error.Message + " Check your connection and try again.";
            UpdateBanner.Visibility = Visibility.Collapsed;
            Diagnostics.Write("update-check-failed", error.ToString());
        }
        finally { updateCheckRunning = false; CheckForUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = true; }
    }

    internal void ShowAvailableUpdate(UpdateCheckResult result)
    {
        if (result.LatestVersion is null || result.Installer is null) throw new InvalidDataException("The update version or verified installer metadata is missing.");
        availableUpdate = result;
        UpdateCheckStatus.Text = $"Version {result.LatestVersion.ToString(3)} is available.";
        UpdateBannerText.Text = $"Frame Trace {result.LatestVersion.ToString(3)} is available.";
        UpdateDownloadProgress.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void DismissUpdateBanner(object sender, RoutedEventArgs e)
    {
        availableUpdate = null;
        UpdateBanner.Visibility = Visibility.Collapsed;
        UpdateCheckStatus.Text = "Update dismissed. Check again whenever you're ready.";
    }

    internal async Task<string> DownloadUpdateAsync(string directory, CancellationToken cancellationToken)
    {
        UpdateCheckResult update = availableUpdate ?? throw new InvalidOperationException("Check for updates before downloading.");
        UpdateInstaller installer = update.Installer ?? throw new InvalidDataException("No verified installer is available for this release.");
        if (updateDownloading) throw new InvalidOperationException("An update is already downloading.");
        updateDownloading = true;
        Pages.IsEnabled = false;
        CheckForUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = DismissUpdateButton.IsEnabled = false;
        UpdateDownloadProgress.Value = 0;
        UpdateDownloadProgress.Visibility = Visibility.Visible;
        UpdateBannerText.Text = $"Downloading {update.LatestVersion!.ToString(3)} · 0%";
        Progress<int> progress = new(percent =>
        {
            UpdateBannerText.Text = percent == 100 ? "Download verified · ready to install" : $"Downloading {update.LatestVersion!.ToString(3)} · {percent}%";
            UpdateCheckStatus.Text = UpdateBannerText.Text;
            UpdateDownloadProgress.Value = percent;
        });
        try { return await UpdateChecker.DownloadAsync(installer, directory, progress, cancellationToken); }
        finally
        {
            updateDownloading = false;
            Pages.IsEnabled = true;
            CheckForUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = DismissUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLayoutReplacement("update Frame Trace")) return;
        string directory = Path.Combine(AppIdentity.DataDirectory, "Updates", Guid.NewGuid().ToString("N"));
        try
        {
            updateDownload = DownloadUpdateAsync(directory, shutdown.Token);
            string installer = await updateDownload;
            shutdown.Token.ThrowIfCancellationRequested();
            using Process setup = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true })
                ?? throw new Win32Exception("Windows did not start the Frame Trace installer. Retry the update.");
            Close();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            UpdateBannerText.Text = "Update failed · nothing was installed";
            UpdateCheckStatus.Text = error.Message;
            UpdateDownloadProgress.Visibility = Visibility.Collapsed;
            Report("Update could not be installed; try again", error);
        }
    }

    private async Task WaitForUpdateDownloadAsync()
    {
        if (updateDownload is null) return;
        try { await updateDownload; }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        { Diagnostics.Write("update-download-stopped", error.ToString()); }
    }
}
