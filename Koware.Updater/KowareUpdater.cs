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
/// Progress information for download operations.
/// </summary>
/// <param name="BytesDownloaded">Bytes downloaded so far.</param>
/// <param name="TotalBytes">Total bytes to download (null if unknown).</param>
/// <param name="Status">Current status message.</param>
/// <param name="Phase">Current phase of the update process.</param>
public sealed record UpdateProgress(
    long BytesDownloaded,
    long? TotalBytes,
    string Status,
    UpdatePhase Phase);

/// <summary>
/// Phases of the update process.
/// </summary>
public enum UpdatePhase
{
    /// <summary>Checking GitHub API for latest release.</summary>
    CheckingVersion,
    /// <summary>Downloading the release asset.</summary>
    Downloading,
    /// <summary>Extracting zip archive.</summary>
    Extracting,
    /// <summary>Launching installer.</summary>
    Launching,
    /// <summary>Update complete.</summary>
    Complete
}

/// <summary>
/// Static helper to check for updates and download/run the latest Koware installer from GitHub Releases.
/// </summary>
public static class KowareUpdater
{
    private const string Owner = "S1mplector";
    private const string Repo = "Koware";
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;

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
    public static Task<KowareUpdateResult> DownloadAndRunLatestInstallerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Wrap string progress to UpdateProgress for backward compatibility
        IProgress<UpdateProgress>? updateProgress = progress != null
            ? new Progress<UpdateProgress>(p => progress.Report(p.Status))
            : null;
        return DownloadAndRunLatestInstallerAsync(updateProgress, cancellationToken);
    }

    /// <summary>
    /// Download the latest release from GitHub and extract/run the installer with detailed progress.
    /// </summary>
    /// <param name="progress">Optional progress reporter for detailed update progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success/failure with metadata.</returns>
    /// <remarks>
    /// Windows only. Downloads to the user's Downloads folder, extracts if needed,
    /// and attempts to find and launch an installer. If no installer is found,
    /// the extracted folder is opened for manual installation.
    /// Includes retry logic for transient network failures.
    /// </remarks>
    public static async Task<KowareUpdateResult> DownloadAndRunLatestInstallerAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return new KowareUpdateResult(
                Success: false,
                Error: "Updater is not supported on this platform.",
                InstallerPath: null,
                ExtractPath: null,
                ReleaseTag: null,
                ReleaseName: null,
                AssetName: null,
                InstallerLaunched: false);
        }

        using var httpClient = CreateGitHubClient();
        httpClient.Timeout = TimeSpan.FromMinutes(30); // Long timeout for large downloads

        try
        {
            progress?.Report(new UpdateProgress(0, null, "Contacting GitHub releases API...", UpdatePhase.CheckingVersion));

            var latest = await GetLatestInstallerAssetWithRetryAsync(httpClient, GetPlatformIdentifier(), cancellationToken).ConfigureAwait(false);
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

            progress?.Report(new UpdateProgress(0, latest.AssetSize, $"Downloading {latest.AssetName}...", UpdatePhase.Downloading));
            var containerPath = await DownloadWithRetryAsync(httpClient, latest.AssetUrl, downloadPath, latest.AssetSize, progress, cancellationToken).ConfigureAwait(false);

            var extension = Path.GetExtension(containerPath);
            string? installerPath = null;
            string? extractPath = null;
            bool installerLaunched = false;

            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Direct executable download (Windows)
                installerPath = containerPath;
                extractPath = downloadsFolder;
                
                progress?.Report(new UpdateProgress(latest.AssetSize ?? 0, latest.AssetSize, $"Launching {Path.GetFileName(installerPath)}...", UpdatePhase.Launching));
                LaunchExecutable(installerPath);
                installerLaunched = true;
            }
            else if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) && containerPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                // Linux/macOS tarball
                var extractFolderName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(latest.AssetName));
                extractPath = Path.Combine(downloadsFolder, extractFolderName);
                
                if (Directory.Exists(extractPath))
                {
                    try { Directory.Delete(extractPath, true); }
                    catch { extractPath = Path.Combine(downloadsFolder, $"{extractFolderName}-{DateTime.Now:yyyyMMdd-HHmmss}"); }
                }

                progress?.Report(new UpdateProgress(0, null, $"Extracting to: {extractPath}", UpdatePhase.Extracting));
                ExtractTarGz(containerPath, extractPath);

                // Find the koware executable in extracted files
                installerPath = FindLinuxExecutable(extractPath);

                if (installerPath != null)
                {
                    // For Linux, we provide instructions rather than auto-launching
                    progress?.Report(new UpdateProgress(0, null, $"Update extracted to: {extractPath}", UpdatePhase.Complete));
                    
                    // Open the folder for manual installation
                    OpenFolder(extractPath);
                }
                else
                {
                    progress?.Report(new UpdateProgress(0, null, "Extraction complete. Opening folder...", UpdatePhase.Complete));
                    OpenFolder(extractPath);
                }

                try { File.Delete(containerPath); } catch { }
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

                progress?.Report(new UpdateProgress(0, null, $"Extracting to: {extractPath}", UpdatePhase.Extracting));
                ZipFile.ExtractToDirectory(containerPath, extractPath, overwriteFiles: true);

                // Try to find an installer executable
                installerPath = FindInstallerExecutable(extractPath);

                if (installerPath != null)
                {
                    progress?.Report(new UpdateProgress(0, null, $"Found installer: {Path.GetFileName(installerPath)}", UpdatePhase.Launching));
                    LaunchExecutable(installerPath);
                    installerLaunched = true;
                }
                else
                {
                    // No installer found - open the folder for manual installation
                    progress?.Report(new UpdateProgress(0, null, "No installer executable found in archive. Opening folder...", UpdatePhase.Complete));
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

    /// <summary>Download a file from a URL to a local path with retry logic and detailed progress.</summary>
    private static async Task<string> DownloadWithRetryAsync(
        HttpClient client,
        Uri url,
        string destinationPath,
        long? expectedSize,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await DownloadCoreAsync(client, url, destinationPath, expectedSize, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                progress?.Report(new UpdateProgress(0, expectedSize, $"Download failed, retrying ({attempt}/{MaxRetries})...", UpdatePhase.Downloading));
                
                // Clean up partial download
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                
                await Task.Delay(RetryDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                progress?.Report(new UpdateProgress(0, expectedSize, $"I/O error, retrying ({attempt}/{MaxRetries})...", UpdatePhase.Downloading));
                
                // Clean up partial download
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                
                await Task.Delay(RetryDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        
        throw lastException ?? new HttpRequestException("Download failed after retries.");
    }

    /// <summary>Core download implementation with byte-level progress reporting.</summary>
    private static async Task<string> DownloadCoreAsync(
        HttpClient client,
        Uri url,
        string destinationPath,
        long? expectedSize,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        // Ensure directory exists
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        var lastReportTime = DateTime.UtcNow;

        while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;

            // Report progress at most every 100ms to avoid console flicker
            var now = DateTime.UtcNow;
            if ((now - lastReportTime).TotalMilliseconds >= 100)
            {
                progress?.Report(new UpdateProgress(readTotal, totalBytes, "Downloading...", UpdatePhase.Downloading));
                lastReportTime = now;
            }
        }

        // Final progress report
        progress?.Report(new UpdateProgress(readTotal, totalBytes, "Download complete", UpdatePhase.Downloading));
        return destinationPath;
    }

    /// <summary>Download a file from a URL to a local path with progress reporting (legacy).</summary>
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

    /// <summary>Open a folder in the system file manager.</summary>
    private static void OpenFolder(string folderPath)
    {
        ProcessStartInfo startInfo;
        
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            };
        }
        else // Linux
        {
            // Try common Linux file managers
            startInfo = new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            };
        }

        try
        {
            Process.Start(startInfo);
        }
        catch
        {
            // Silently fail if no file manager is available
        }
    }

    /// <summary>
    /// Query GitHub API for the latest release and find the best installer asset with retry logic.
    /// </summary>
    private static async Task<(string? Tag, string? Name, string? AssetName, Uri? AssetUrl, long? AssetSize)> GetLatestInstallerAssetWithRetryAsync(
        HttpClient client,
        string platformId,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await GetLatestInstallerAssetAsync(client, platformId, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastException = ex;
                await Task.Delay(RetryDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        
        throw lastException ?? new HttpRequestException("Failed to fetch release information after retries.");
    }

    /// <summary>
    /// Query GitHub API for the latest release and find the best installer asset.
    /// Uses /releases endpoint to include prereleases (e.g., beta versions).
    /// </summary>
    private static async Task<(string? Tag, string? Name, string? AssetName, Uri? AssetUrl, long? AssetSize)> GetLatestInstallerAssetAsync(
        HttpClient client,
        string platformId,
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
            return (null, null, null, null, null);
        }

        var root = document.RootElement[0];

        string? tag = null;
        string? name = null;
        string? assetName = null;
        Uri? assetUrl = null;
        long? assetSize = null;

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

                var score = ScoreAssetName(candidateName, platformId);
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
                
                // Get asset size if available
                if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size))
                {
                    assetSize = size;
                }
            }
        }

        return (tag, name, assetName, assetUrl, assetSize);
    }

    /// <summary>
    /// Score an asset name to pick the best installer for the current platform.
    /// </summary>
    private static int ScoreAssetName(string name, string platformId)
    {
        var score = 0;
        var lowerName = name.ToLowerInvariant();

        // Platform-specific scoring
        if (OperatingSystem.IsWindows())
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                score += 80;
            if (lowerName.Contains("win") || lowerName.Contains("windows"))
                score += 50;
            if (lowerName.Contains("installer"))
                score += 40;
            // Penalize Linux/macOS assets on Windows
            if (lowerName.Contains("linux") || lowerName.Contains("osx") || lowerName.Contains("macos"))
                score -= 200;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (lowerName.Contains("linux"))
                score += 80;
            if (lowerName.Contains(platformId))
                score += 60;
            // Penalize Windows/macOS assets on Linux
            if (lowerName.Contains("win") || lowerName.Contains(".exe") || lowerName.Contains("osx") || lowerName.Contains("macos") || lowerName.Contains(".dmg"))
                score -= 200;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                score += 80;
            if (lowerName.Contains("osx") || lowerName.Contains("macos") || lowerName.Contains("darwin"))
                score += 60;
            if (lowerName.Contains(platformId))
                score += 40;
            // Penalize Windows/Linux assets on macOS
            if (lowerName.Contains("win") || lowerName.Contains(".exe") || lowerName.Contains("linux"))
                score -= 200;
        }

        // Common scoring
        if (lowerName.Contains("koware"))
            score += 10;

        return score;
    }

    /// <summary>
    /// Get the platform identifier for asset selection.
    /// </summary>
    private static string GetPlatformIdentifier()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows())
            return $"win-{arch}";
        if (OperatingSystem.IsLinux())
            return $"linux-{arch}";
        if (OperatingSystem.IsMacOS())
            return $"osx-{arch}";
        
        return arch;
    }

    /// <summary>
    /// Extract a .tar.gz archive to a directory.
    /// </summary>
    private static void ExtractTarGz(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        
        // Use system tar command for simplicity and reliability
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xzf \"{archivePath}\" -C \"{destinationDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start tar process");
        }

        process.WaitForExit();
        
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"tar extraction failed: {error}");
        }
    }

    /// <summary>
    /// Find the koware executable in an extracted Linux directory.
    /// </summary>
    private static string? FindLinuxExecutable(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        // Look for koware executable
        var patterns = new[] { "koware", "Koware", "Koware.Cli" };
        
        foreach (var pattern in patterns)
        {
            var files = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".dll") && !f.EndsWith(".pdb") && !f.EndsWith(".json"))
                .ToList();
            
            if (files.Count > 0)
                return files[0];
        }

        return null;
    }

    /// <summary>Remove invalid filename characters from a string.</summary>
    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(buffer);
    }
}
