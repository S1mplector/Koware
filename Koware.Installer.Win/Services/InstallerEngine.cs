// Author: Ilgaz Mehmetoğlu 
// Core installation logic to publish/copy Koware CLI and player and update PATH.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Koware.Installer.Win.Models;

namespace Koware.Installer.Win.Services;

public sealed class InstallerEngine
{
    private readonly string _repoRoot;

    public InstallerEngine(string? repoRoot = null)
    {
        _repoRoot = repoRoot ?? DetectRepoRoot();
    }

    public async Task InstallAsync(InstallOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.InstallDir))
        {
            throw new ArgumentException("Install directory is required.", nameof(options));
        }

        var installDir = Path.GetFullPath(options.InstallDir);
        var cliProject = Path.Combine(_repoRoot, "Koware.Cli");
        var playerProject = Path.Combine(_repoRoot, "Koware.Player");

        EnsureDirectory(installDir, options.CleanTarget, progress);

        var embeddedUsed = TryExtractEmbedded("Payload.KowareCli", installDir, progress);
        var embeddedPlayerUsed = false;
        var embeddedReaderUsed = false;
        var embeddedBrowserUsed = false;

        if (options.IncludePlayer)
        {
            embeddedPlayerUsed = TryExtractEmbedded("Payload.KowarePlayer", installDir, progress);
            embeddedReaderUsed = TryExtractEmbedded("Payload.KowareReader", installDir, progress);
            embeddedBrowserUsed = TryExtractEmbedded("Payload.KowareBrowser", installDir, progress);
        }

        if (!embeddedUsed && options.Publish)
        {
            await PublishAsync("Koware CLI", cliProject, installDir, progress, cancellationToken);
            if (options.IncludePlayer && Directory.Exists(playerProject))
            {
                await PublishAsync("Koware Player", playerProject, installDir, progress, cancellationToken);
            }
        }
        else if (!embeddedUsed)
        {
            CopyLatestBuild(cliProject, installDir, progress);
            if (options.IncludePlayer && Directory.Exists(playerProject))
            {
                CopyLatestBuild(playerProject, installDir, progress);
            }
        }

        CreateShims(installDir, progress);

        if (options.AddToPath)
        {
            AddToPath(installDir, progress);
        }

        WriteVersionFile(installDir, progress);
    }

    public bool IsInstalled(string? installDir = null)
    {
        var dir = installDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "koware");
        }

        dir = Path.GetFullPath(dir);

        if (!Directory.Exists(dir))
        {
            return false;
        }

        var cliExe = Path.Combine(dir, "Koware.Cli.exe");
        return File.Exists(cliExe);
    }

    public string? GetInstalledVersion(string? installDir = null)
    {
        var dir = installDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "koware");
        }

        dir = Path.GetFullPath(dir);

        var versionFile = Path.Combine(dir, "version.txt");
        if (!File.Exists(versionFile))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(versionFile).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public Task UninstallAsync(string? installDir = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var dir = installDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "koware");
        }

        dir = Path.GetFullPath(dir);

        progress?.Report($"Uninstalling from {dir}...");

        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                progress?.Report($"Removed install directory {dir}.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to delete install directory {dir}: {ex.Message}", ex);
            }
        }
        else
        {
            progress?.Report($"Install directory not found at {dir}. Nothing to remove.");
        }

        RemoveFromPath(dir, progress);

        return Task.CompletedTask;
    }

    private static string DetectRepoRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return Directory.Exists(Path.Combine(candidate, "Koware.Cli"))
            ? candidate
            : baseDir;
    }

    private static void EnsureDirectory(string path, bool clean, IProgress<string>? progress)
    {
        if (clean && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            progress?.Report($"Cleaned {path}");
        }
        Directory.CreateDirectory(path);
    }

    private static void CopyLatestBuild(string projectDir, string installDir, IProgress<string>? progress)
    {
        var binDir = Path.Combine(projectDir, "bin");
        if (!Directory.Exists(binDir))
        {
            throw new DirectoryNotFoundException($"No build artifacts found at {binDir}. Run dotnet publish or build first.");
        }

        var latest = Directory.EnumerateDirectories(binDir, "*", SearchOption.AllDirectories)
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null)
        {
            throw new DirectoryNotFoundException($"No build artifacts under {binDir}.");
        }

        CopyDirectory(latest.FullName, installDir);
        progress?.Report($"Copied build from {latest.FullName}");
    }

    private static async Task PublishAsync(string name, string projectDir, string installDir, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"Publishing {name}...");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectDir}\" -c Release -o \"{installDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed for {name}:{Environment.NewLine}{stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            progress?.Report(stdout.Trim());
        }
    }

    private static void AddToPath(string installDir, IProgress<string>? progress)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (parts.Any(p => string.Equals(p, installDir, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report("Install path already on user PATH.");
            return;
        }

        parts.Add(installDir);
        var updated = string.Join(Path.PathSeparator, parts);
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        progress?.Report($"Added {installDir} to user PATH.");
    }

    private static void RemoveFromPath(string installDir, IProgress<string>? progress)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var removed = parts.RemoveAll(p => string.Equals(p, installDir, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            progress?.Report("Install path was not present on user PATH.");
            return;
        }

        var updated = string.Join(Path.PathSeparator, parts);
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        progress?.Report($"Removed {installDir} from user PATH.");
    }

    private static void WriteVersionFile(string installDir, IProgress<string>? progress)
    {
        var cliExe = Path.Combine(installDir, "Koware.Cli.exe");
        if (!File.Exists(cliExe))
        {
            return;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(cliExe);
            var version = !string.IsNullOrWhiteSpace(info.ProductVersion)
                ? info.ProductVersion
                : info.FileVersion;

            if (string.IsNullOrWhiteSpace(version))
            {
                return;
            }

            var path = Path.Combine(installDir, "version.txt");
            File.WriteAllText(path, version);
            progress?.Report($"Recorded installed version {version}.");
        }
        catch (Exception ex)
        {
            progress?.Report($"Warning: failed to record installed version: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = dir.Replace(sourceDir, destinationDir);
            Directory.CreateDirectory(target);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(sourceDir, destinationDir);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CreateShims(string installDir, IProgress<string>? progress)
    {
        var cliExe = Path.Combine(installDir, "Koware.Cli.exe");
        if (!File.Exists(cliExe))
        {
            progress?.Report("Warning: Koware.Cli.exe not found in install directory; command shims not created.");
            return;
        }

        var cmdShimPath = Path.Combine(installDir, "koware.cmd");
        var psShimPath = Path.Combine(installDir, "koware.ps1");

        var cmdContent = "@echo off\r\n\"%~dp0Koware.Cli.exe\" %*\r\n";
        File.WriteAllText(cmdShimPath, cmdContent);

        var psContent = "$here = Split-Path -Parent $MyInvocation.MyCommand.Path\r\n& \"$here\\Koware.Cli.exe\" @args\r\n";
        File.WriteAllText(psShimPath, psContent);

        progress?.Report("Created command shims (koware.cmd, koware.ps1).");
    }

    private bool TryExtractEmbedded(string resourceName, string destinationDir, IProgress<string>? progress)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n =>
                n.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase));

        if (fullName is null)
        {
            return false;
        }

        using var stream = assembly.GetManifestResourceStream(fullName);
        if (stream is null)
        {
            return false;
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        archive.ExtractToDirectory(destinationDir, overwriteFiles: true);
        progress?.Report($"Extracted embedded payload: {resourceName}");
        return true;
    }
}
