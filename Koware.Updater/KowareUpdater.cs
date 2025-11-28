using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Koware.Updater;

public sealed record KowareUpdateResult(
    bool Success,
    string? Error,
    string? InstallerPath,
    string? ReleaseTag,
    string? ReleaseName,
    string? AssetName);

public sealed record KowareLatestVersion(
    string? Tag,
    string? Name);

public static class KowareUpdater
{
    private const string Owner = "S1mplector";
    private const string Repo = "Koware";

    public static async Task<KowareLatestVersion> GetLatestVersionAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateGitHubClient();
        var latest = await GetLatestInstallerAssetAsync(httpClient, cancellationToken).ConfigureAwait(false);
        return new KowareLatestVersion(latest.Tag, latest.Name);
    }

    public static async Task<KowareUpdateResult> DownloadAndRunLatestInstallerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new KowareUpdateResult(false, "Updater currently supports Windows installers only.", null, null, null, null);
        }

        using var httpClient = CreateGitHubClient();

        try
        {
            progress?.Report("Contacting GitHub releases API...");

            var latest = await GetLatestInstallerAssetAsync(httpClient, cancellationToken).ConfigureAwait(false);
            if (latest.AssetUrl is null || string.IsNullOrWhiteSpace(latest.AssetName))
            {
                return new KowareUpdateResult(false, "No suitable installer asset found in the latest GitHub release.", null, latest.Tag, latest.Name, null);
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "koware-updater");
            Directory.CreateDirectory(tempRoot);

            var safeTag = string.IsNullOrWhiteSpace(latest.Tag) ? "latest" : SanitizeForPath(latest.Tag);
            var downloadPath = Path.Combine(tempRoot, safeTag + "-" + latest.AssetName);

            progress?.Report($"Downloading {latest.AssetName}...");
            var containerPath = await DownloadAsync(httpClient, latest.AssetUrl, downloadPath, progress, cancellationToken).ConfigureAwait(false);

            var extension = Path.GetExtension(containerPath);
            string installerPath;

            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                installerPath = containerPath;
            }
            else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDir = Path.Combine(tempRoot, safeTag + "-installer");
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, true);
                }

                progress?.Report("Extracting installer...");
                ZipFile.ExtractToDirectory(containerPath, extractDir, overwriteFiles: true);

                installerPath = Directory
                    .EnumerateFiles(extractDir, "Koware.Installer.Win.exe", SearchOption.AllDirectories)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("Installer executable 'Koware.Installer.Win.exe' not found in the downloaded archive.");
            }
            else
            {
                return new KowareUpdateResult(false, $"Unsupported installer asset type: {extension}", containerPath, latest.Tag, latest.Name, latest.AssetName);
            }

            progress?.Report("Launching installer...");
            LaunchInstaller(installerPath);

            return new KowareUpdateResult(true, null, installerPath, latest.Tag, latest.Name, latest.AssetName);
        }
        catch (Exception ex)
        {
            return new KowareUpdateResult(false, ex.Message, null, null, null, null);
        }
    }

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Koware.Updater/1.0 (+https://github.com/" + Owner + "/" + Repo + ")");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task<string> DownloadAsync(
        HttpClient client,
        Uri url,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;

        while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                var percent = (int)(readTotal * 100 / totalBytes.Value);
                progress?.Report($"Downloaded {percent}%...");
            }
        }

        progress?.Report("Download complete.");
        return destinationPath;
    }

    private static void LaunchInstaller(string installerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start installer process.");
        }
    }

    private static async Task<(string? Tag, string? Name, string? AssetName, Uri? AssetUrl)> GetLatestInstallerAssetAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;

        string? tag = null;
        string? name = null;
        string? assetName = null;
        Uri? assetUrl = null;

        if (root.TryGetProperty("tag_name", out var tagElement) && tagElement.ValueKind == JsonValueKind.String)
        {
            tag = tagElement.GetString();
        }

        if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            name = nameElement.GetString();
        }

        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            var bestScore = 0;

            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var assetNameElement) || assetNameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidateName = assetNameElement.GetString();
                if (string.IsNullOrWhiteSpace(candidateName))
                {
                    continue;
                }

                var score = ScoreAssetName(candidateName);
                if (score <= 0 || score < bestScore)
                {
                    continue;
                }

                if (!asset.TryGetProperty("browser_download_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var urlText = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(urlText) || !Uri.TryCreate(urlText, UriKind.Absolute, out var parsed))
                {
                    continue;
                }

                bestScore = score;
                assetName = candidateName;
                assetUrl = parsed;
            }
        }

        return (tag, name, assetName, assetUrl);
    }

    private static int ScoreAssetName(string name)
    {
        var score = 0;

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        if (name.Contains("installer", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (name.Contains("koware", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(buffer);
    }
}
