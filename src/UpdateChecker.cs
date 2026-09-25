using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frameglass;

internal enum UpdateAvailability { UpToDate, Available, NoRelease }

internal sealed record UpdateCheckResult(UpdateAvailability Availability, Version? LatestVersion, Uri? ReleaseUri);

internal static class UpdateChecker
{
    private const string Repository = "itourboy-OG/FrameTrace";
    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    internal static async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameTrace", currentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await client.GetAsync(LatestRelease, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateAvailability.NoRelease, null, null);
        response.EnsureSuccessStatusCode();

        GitHubRelease release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: cancellationToken)
            ?? throw new JsonException("GitHub returned an empty release response.");
        if (string.IsNullOrWhiteSpace(release.TagName) || string.IsNullOrWhiteSpace(release.HtmlUrl))
            throw new JsonException("GitHub's release response omitted its version tag or page address.");
        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out Version? latestVersion))
            throw new InvalidDataException($"GitHub returned an invalid release tag: {release.TagName}.");
        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? releaseUri)
            || releaseUri.Scheme != Uri.UriSchemeHttps
            || !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !releaseUri.AbsolutePath.StartsWith($"/{Repository}/releases/tag/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub returned an invalid release page address.");

        return latestVersion > currentVersion
            ? new(UpdateAvailability.Available, latestVersion, releaseUri)
            : new(UpdateAvailability.UpToDate, latestVersion, releaseUri);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl);
}
