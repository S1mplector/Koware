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
        var playerProject = Path.Combine(_repoRoot, "Koware.Player.Win");
        var cmdShim = Path.Combine(_repoRoot, "koware.cmd");

        EnsureDirectory(installDir, options.CleanTarget, progress);

        var embeddedUsed = TryExtractEmbedded("Payload.KowareCli", installDir, progress);
        var embeddedPlayerUsed = false;

        if (options.IncludePlayer)
        {
            embeddedPlayerUsed = TryExtractEmbedded("Payload.KowarePlayer", installDir, progress);
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

        if (File.Exists(cmdShim))
        {
            File.Copy(cmdShim, Path.Combine(installDir, "koware.cmd"), overwrite: true);
            progress?.Report($"Copied shim to {installDir}");
        }

        if (options.AddToPath)
        {
            AddToPath(installDir, progress);
        }
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
