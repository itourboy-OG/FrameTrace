using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace FrameTrace;

public partial class MainWindow
{
    private bool changelogRequested, changelogLoading;

    private async void RefreshChangelog(object sender, RoutedEventArgs e) => await LoadChangelogAsync();

    private async Task LoadChangelogAsync()
    {
        if (changelogLoading) return;
        changelogRequested = changelogLoading = true;
        RefreshChangelogButton.IsEnabled = false;
        ChangelogItems.ItemsSource = null;
        ChangelogStatus.Text = "Loading recent releases from GitHub…";
        try
        {
            ImmutableArray<ChangelogRelease> releases = await UpdateChecker.ReadChangelogAsync(shutdown.Token);
            ChangelogItems.ItemsSource = releases;
            ChangelogStatus.Text = releases.Length == 0 ? "No stable releases have been published yet."
                : $"Showing {releases.Length} recent stable releases.";
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            ChangelogStatus.Text = "Could not load changelog: " + error.Message + " Choose Refresh to try again.";
            Diagnostics.Write("changelog-load-failed", JsonSerializer.Serialize(new { Error = error.ToString() }));
        }
        finally { changelogLoading = false; RefreshChangelogButton.IsEnabled = true; }
    }

    private void OpenChangelogRelease(object sender, RoutedEventArgs e) =>
        OpenCommunityPage((sender as Button)?.Tag as Uri ?? throw new InvalidOperationException("Select a release before opening its page."));

    private void OpenReleaseHistory(object sender, RoutedEventArgs e) =>
        OpenCommunityPage(new Uri("https://github.com/itourboy-OG/FrameTrace/releases"));
}
