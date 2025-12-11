using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Koware.Updater;

/// <summary>
/// Result of an update operation, indicating success/failure and metadata.
/// </summary>
/// <param name="Success">True if the update was downloaded successfully.</param>
/// <param name="Error">Error message if failed; null on success.</param>
/// <param name="InstallerPath">Path to the installer executable if found and launched.</param>
/// <param name="ExtractPath">Path where the update was extracted.</param>
/// <param name="ReleaseTag">GitHub release tag (e.g., "v1.0.0").</param>
/// <param name="ReleaseName">GitHub release name.</param>
/// <param name="AssetName">Downloaded asset filename.</param>
/// <param name="InstallerLaunched">True if an installer was found and launched.</param>
public sealed record KowareUpdateResult(
    bool Success,
    string? Error,
    string? InstallerPath,
    string? ExtractPath,
    string? ReleaseTag,
    string? ReleaseName,
    string? AssetName,
    bool InstallerLaunched);

/// <summary>
/// Represents the latest release version information from GitHub.
/// </summary>
/// <param name="Tag">Release tag (e.g., "v1.0.0").</param>
/// <param name="Name">Release name/title.</param>
public sealed record KowareLatestVersion(
    string? Tag,
    string? Name);

/// <summary>
/// Static helper to check for updates and download/run the latest Koware installer from GitHub Releases.
/// </summary>
public static class KowareUpdater
{
    private const string Owner = "S1mplector";
    private const string Repo = "Koware";

    /// <summary>
    /// Query GitHub for the latest release version without downloading.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest version info (tag and name).</returns>
    public static async Task<KowareLatestVersion> GetLatestVersionAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateGitHubClient();
        var latest = await GetLatestInstallerAssetAsync(httpClient, cancellationToken).ConfigureAwait(false);
        return new KowareLatestVersion(latest.Tag, latest.Name);
    }

    /// <summary>
    /// Download the latest release from GitHub and extract/run the installer.
    /// </summary>
    /// <param name="progress">Optional progress reporter for status messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success/failure with metadata.</returns>
    /// <remarks>
    /// Windows only. Downloads to the user's Downloads folder, extracts if needed,
    /// and attempts to find and launch an installer. If no installer is found,
    /// the extracted folder is opened for manual installation.
    /// </remarks>
    public static async Task<KowareUpdateResult> DownloadAndRunLatestInstallerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new KowareUpdateResult(
                Success: false,
                Error: "Updater currently supports Windows only.",
                InstallerPath: null,
                ExtractPath: null,
                ReleaseTag: null,
                ReleaseName: null,
                AssetName: null,
                InstallerLaunched: false);
        }

        using var httpClient = CreateGitHubClient();

        try
        {
            progress?.Report("Contacting GitHub releases API...");

            var latest = await GetLatestInstallerAssetAsync(httpClient, cancellationToken).ConfigureAwait(false);
            if (latest.AssetUrl is null || string.IsNullOrWhiteSpace(latest.AssetName))
            {
                return new KowareUpdateResult(
                    Success: false,
                    Error: "No suitable release asset found in the latest GitHub release.",
                    InstallerPath: null,
                    ExtractPath: null,
                    ReleaseTag: latest.Tag,
                    ReleaseName: latest.Name,
                    AssetName: null,
                    InstallerLaunched: false);
            }

            // Use Downloads folder for better user experience
            var downloadsFolder = GetDownloadsFolder();
            var safeTag = string.IsNullOrWhiteSpace(latest.Tag) ? "latest" : SanitizeForPath(latest.Tag);
            var downloadPath = Path.Combine(downloadsFolder, latest.AssetName);

            progress?.Report($"Downloading {latest.AssetName} to Downloads folder...");
            var containerPath = await DownloadAsync(httpClient, latest.AssetUrl, downloadPath, progress, cancellationToken).ConfigureAwait(false);

            var extension = Path.GetExtension(containerPath);
            string? installerPath = null;
            string? extractPath = null;
            bool installerLaunched = false;

            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Direct executable download
                installerPath = containerPath;
                extractPath = downloadsFolder;
                
                progress?.Report($"Launching {Path.GetFileName(installerPath)}...");
                LaunchExecutable(installerPath);
                installerLaunched = true;
            }
            else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Extract zip to a dedicated folder in Downloads
                var extractFolderName = Path.GetFileNameWithoutExtension(latest.AssetName);
                extractPath = Path.Combine(downloadsFolder, extractFolderName);
                
                // Clean up existing extraction if present
                if (Directory.Exists(extractPath))
                {
                    try
                    {
                        Directory.Delete(extractPath, true);
                    }
                    catch
                    {
                        // If we can't delete, append timestamp
                        extractPath = Path.Combine(downloadsFolder, $"{extractFolderName}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    }
                }

                progress?.Report($"Extracting to: {extractPath}");
                ZipFile.ExtractToDirectory(containerPath, extractPath, overwriteFiles: true);

                // Try to find an installer executable
                installerPath = FindInstallerExecutable(extractPath);

                if (installerPath != null)
                {
                    progress?.Report($"Found installer: {Path.GetFileName(installerPath)}");
                    progress?.Report("Launching installer...");
                    LaunchExecutable(installerPath);
                    installerLaunched = true;
                }
                else
                {
                    // No installer found - open the folder for manual installation
                    progress?.Report("No installer executable found in archive.");
                    progress?.Report($"Opening folder: {extractPath}");
                    OpenFolder(extractPath);
                }

                // Optionally clean up the zip file after successful extraction
                try
                {
                    File.Delete(containerPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
            else
            {
                return new KowareUpdateResult(
                    Success: false,
                    Error: $"Unsupported file type: {extension}. Expected .exe or .zip.",
                    InstallerPath: null,
                    ExtractPath: null,
                    ReleaseTag: latest.Tag,
                    ReleaseName: latest.Name,
                    AssetName: latest.AssetName,
                    InstallerLaunched: false);
            }

            return new KowareUpdateResult(
                Success: true,
                Error: null,
                InstallerPath: installerPath,
                ExtractPath: extractPath,
                ReleaseTag: latest.Tag,
                ReleaseName: latest.Name,
                AssetName: latest.AssetName,
                InstallerLaunched: installerLaunched);
        }
        catch (HttpRequestException ex)
        {
            return new KowareUpdateResult(
                Success: false,
                Error: $"Network error: {ex.Message}",
                InstallerPath: null,
                ExtractPath: null,
                ReleaseTag: null,
                ReleaseName: null,
                AssetName: null,
                InstallerLaunched: false);
        }
        catch (Exception ex)
        {
            return new KowareUpdateResult(
                Success: false,
                Error: ex.Message,
                InstallerPath: null,
                ExtractPath: null,
                ReleaseTag: null,
                ReleaseName: null,
                AssetName: null,
                InstallerLaunched: false);
        }
    }

    /// <summary>Get the user's Downloads folder path.</summary>
    private static string GetDownloadsFolder()
    {
        // Try the known folder API first (Windows)
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloadsPath = Path.Combine(downloads, "Downloads");
        
        if (Directory.Exists(downloadsPath))
        {
            return downloadsPath;
        }

        // Fallback to temp if Downloads doesn't exist
        var fallback = Path.Combine(Path.GetTempPath(), "koware-updates");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// Find the best installer executable in an extracted directory.
    /// Searches for common installer patterns with priority ordering.
    /// </summary>
    private static string? FindInstallerExecutable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var allExeFiles = Directory
            .EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
            .ToList();

        if (allExeFiles.Count == 0)
        {
            return null;
        }

        // Priority patterns for installer detection (ordered by preference)
        var installerPatterns = new[]
        {
            "Koware.Installer.Win.exe",     // Exact match for Koware installer
            "*Installer*.exe",               // Any installer
            "*Setup*.exe",                   // Setup executables
            "install*.exe",                  // Install prefixed
            "setup*.exe",                    // Setup prefixed
            "Koware.exe",                    // Main application (fallback)
            "koware.exe",                    // Main application lowercase
        };

        foreach (var pattern in installerPatterns)
        {
            var matches = Directory
                .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .ToList();

            if (matches.Count > 0)
            {
                // Return the first match for this pattern
                return matches[0];
            }
        }

        // No pattern matched - return null (will open folder instead)
        return null;
    }

    /// <summary>Create an HttpClient configured for GitHub API requests.</summary>
    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Koware.Updater/1.0 (+https://github.com/" + Owner + "/" + Repo + ")");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Download a file from a URL to a local path with progress reporting.</summary>
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

    /// <summary>Launch an executable using shell execute.</summary>
    private static void LaunchExecutable(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start process: {Path.GetFileName(executablePath)}");
        }
    }

    /// <summary>Open a folder in the system file explorer.</summary>
    private static void OpenFolder(string folderPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath}\"",
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    /// <summary>
    /// Query GitHub API for the latest release and find the best installer asset.
    /// Uses /releases endpoint to include prereleases (e.g., beta versions).
    /// </summary>
    private static async Task<(string? Tag, string? Name, string? AssetName, Uri? AssetUrl)> GetLatestInstallerAssetAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        // Use /releases (not /releases/latest) to include prereleases like beta versions
        var uri = new Uri($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=1");

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        // /releases returns an array, so get the first element
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return (null, null, null, null);
        }

        var root = document.RootElement[0];

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

    /// <summary>
    /// Score an asset name to pick the best installer (prefers .exe, installer, win-x64).
    /// </summary>
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

    /// <summary>Remove invalid filename characters from a string.</summary>
    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(buffer);
    }
}
