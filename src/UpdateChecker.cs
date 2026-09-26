using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Frameglass;

internal enum UpdateAvailability { UpToDate, Available, NoRelease }

internal sealed record UpdateInstaller(string Name, Uri DownloadUri, long Size, string Sha256);
internal sealed record UpdateCheckResult(UpdateAvailability Availability, Version? LatestVersion, Uri? ReleaseUri, UpdateInstaller? Installer);

internal static class UpdateChecker
{
    private const string Repository = "itourboy-OG/FrameTrace";
    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    internal static async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameTrace", currentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        for (int attempt = 1; ; attempt++)
        {
            try { return await ReadReleaseAsync(client, currentVersion, cancellationToken); }
            catch (HttpRequestException error) when (attempt < 3 && IsTransientHttpError(error))
            {
                Diagnostics.Write("update-check-retry", error.ToString());
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private static async Task<UpdateCheckResult> ReadReleaseAsync(HttpClient client, Version currentVersion, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(LatestRelease, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateAvailability.NoRelease, null, null, null);
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

        if (release.Draft || release.Prerelease) throw new InvalidDataException("GitHub returned a draft or prerelease instead of a stable update.");
        if (latestVersion <= currentVersion) return new(UpdateAvailability.UpToDate, latestVersion, releaseUri, null);
        if (release.Assets is null || release.Assets.Any(asset => asset is null)) throw new InvalidDataException("GitHub returned an invalid release asset list.");
        string name = $"FrameTrace-{latestVersion.ToString(3)}-Setup.exe";
        GitHubAsset[] matches = release.Assets.Where(asset => asset.Name == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Release {release.TagName} must contain exactly one {name} installer.");
        GitHubAsset installer = matches[0];
        string download = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(release.TagName)}/{name}";
        if (installer.DownloadUrl != download || installer.Size <= 0 || installer.State != "uploaded")
            throw new InvalidDataException($"Release {release.TagName} has an invalid installer address, size, or upload status.");
        if (installer.Digest is null || !installer.Digest.StartsWith("sha256:", StringComparison.Ordinal) || installer.Digest.Length != 71 || installer.Digest[7..].Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Release {release.TagName} has no valid SHA-256 checksum. The installer cannot be verified.");
        return new(UpdateAvailability.Available, latestVersion, releaseUri, new(name, new Uri(download), installer.Size, installer.Digest[7..]));
    }

    internal static async Task<string> DownloadAsync(UpdateInstaller installer, string directory, IProgress<int> progress, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameTrace", typeof(App).Assembly.GetName().Version!.ToString(3)));
        for (int attempt = 1; ; attempt++)
        {
            try { return await DownloadInstallerAsync(client, installer, directory, progress, cancellationToken); }
            catch (HttpRequestException error) when (attempt < 3 && IsTransientHttpError(error))
            {
                Diagnostics.Write("update-download-retry", error.ToString());
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransientHttpError(HttpRequestException error) =>
        error.StatusCode is null or HttpStatusCode.TooManyRequests || (int)error.StatusCode >= 500;

    private static async Task<string> DownloadInstallerAsync(HttpClient client, UpdateInstaller installer, string directory, IProgress<int> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, installer.Name);
        string partial = destination + ".part";
        bool createdPartial = false;
        try
        {
            progress.Report(0);
            using HttpResponseMessage response = await client.GetAsync(installer.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != installer.Size)
                throw new InvalidDataException("The installer download size does not match GitHub's release metadata. Nothing was installed.");
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream output = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                createdPartial = true;
                byte[] buffer = new byte[81920];
                long received = 0;
                int previous = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += count;
                    if (received > installer.Size) throw new InvalidDataException("The installer exceeded its expected size. Nothing was installed.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    int percent = (int)Math.Min(99, received * 100 / installer.Size);
                    if (percent != previous) { progress.Report(percent); previous = percent; }
                }
            }
            await VerifyInstallerAsync(partial, installer, cancellationToken);
            File.Move(partial, destination);
            progress.Report(100);
            return destination;
        }
        finally { if (createdPartial && File.Exists(partial)) File.Delete(partial); }
    }

    internal static async Task VerifyInstallerAsync(string path, UpdateInstaller installer, CancellationToken cancellationToken)
    {
        await using FileStream file = File.OpenRead(path);
        if (file.Length != installer.Size) throw new InvalidDataException("The installer download is incomplete. Nothing was installed; try updating again.");
        string checksum = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!checksum.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer checksum does not match GitHub's release. Nothing was installed; check your connection and try again.");
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name"), JsonRequired] string TagName,
        [property: JsonPropertyName("html_url"), JsonRequired] string HtmlUrl,
        [property: JsonPropertyName("draft"), JsonRequired] bool Draft,
        [property: JsonPropertyName("prerelease"), JsonRequired] bool Prerelease,
        [property: JsonPropertyName("assets"), JsonRequired] GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name"), JsonRequired] string Name,
        [property: JsonPropertyName("browser_download_url"), JsonRequired] string DownloadUrl,
        [property: JsonPropertyName("size"), JsonRequired] long Size,
        [property: JsonPropertyName("state"), JsonRequired] string State,
        [property: JsonPropertyName("digest"), JsonRequired] string? Digest);
}
