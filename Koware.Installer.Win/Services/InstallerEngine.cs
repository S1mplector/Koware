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
        
        // Create Start Menu shortcut and register for uninstall
        CreateStartMenuShortcut(installDir, progress);
        RegisterUninstall(installDir, progress);
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
        RemoveStartMenuShortcut(progress);
        UnregisterUninstall(progress);

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

    private static void CreateStartMenuShortcut(string installDir, IProgress<string>? progress)
    {
        try
        {
            var startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "Koware");
            
            Directory.CreateDirectory(startMenuPath);
            
            // Look for Browser first, then fall back to CLI
            var browserExe = Path.Combine(installDir, "Koware.Browser.exe");
            var cliExe = Path.Combine(installDir, "Koware.Cli.exe");
            var targetExe = File.Exists(browserExe) ? browserExe : cliExe;
            var shortcutName = File.Exists(browserExe) ? "Koware.lnk" : "Koware CLI.lnk";
            
            if (!File.Exists(targetExe))
            {
                progress?.Report("Warning: No executable found for Start Menu shortcut.");
                return;
            }
            
            var shortcutPath = Path.Combine(startMenuPath, shortcutName);
            
            // Use PowerShell to create shortcut (no COM dependency)
            var ps = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{shortcutPath}'); $s.TargetPath = '{targetExe}'; $s.WorkingDirectory = '{installDir}'; $s.Description = 'Koware - Anime & Manga Browser'; $s.Save()\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(ps);
            process?.WaitForExit(5000);
            
            progress?.Report($"Created Start Menu shortcut at {startMenuPath}");
        }
        catch (Exception ex)
        {
            progress?.Report($"Warning: Failed to create Start Menu shortcut: {ex.Message}");
        }
    }
    
    private static void RemoveStartMenuShortcut(IProgress<string>? progress)
    {
        try
        {
            var startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "Koware");
            
            if (Directory.Exists(startMenuPath))
            {
                Directory.Delete(startMenuPath, recursive: true);
                progress?.Report("Removed Start Menu shortcut.");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Warning: Failed to remove Start Menu shortcut: {ex.Message}");
        }
    }
    
    private static void RegisterUninstall(string installDir, IProgress<string>? progress)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        try
        {
            var version = "0.8.0";
            var versionFile = Path.Combine(installDir, "version.txt");
            if (File.Exists(versionFile))
            {
                version = File.ReadAllText(versionFile).Trim();
            }
            
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Koware");
            
            if (key != null)
            {
                var browserExe = Path.Combine(installDir, "Koware.Browser.exe");
                var iconPath = File.Exists(browserExe) ? browserExe : Path.Combine(installDir, "Koware.Cli.exe");
                
                key.SetValue("DisplayName", "Koware");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "Ilgaz Mehmetoglu");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", iconPath);
                key.SetValue("UninstallString", $"\"{Path.Combine(installDir, "Koware.Installer.Win.exe")}\" --uninstall");
                key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                
                progress?.Report("Registered Koware in Programs and Features.");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Warning: Failed to register uninstall: {ex.Message}");
        }
    }
    
    private static void UnregisterUninstall(IProgress<string>? progress)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Koware", 
                throwOnMissingSubKey: false);
            progress?.Report("Removed Koware from Programs and Features.");
        }
        catch (Exception ex)
        {
            progress?.Report($"Warning: Failed to unregister: {ex.Message}");
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
