// Author: Ilgaz Mehmetoğlu
using Koware.Updater;

namespace Koware.Cli.Commands;

/// <summary>
/// Implements the 'koware update' command: checks for updates and downloads the latest version.
/// </summary>
public sealed class UpdateCommand : ICliCommand
{
    public string Name => "update";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Description => "Check for updates and download the latest version";

    public async Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        var checkOnly = args.Contains("--check", StringComparer.OrdinalIgnoreCase) 
                     || args.Contains("-c", StringComparer.OrdinalIgnoreCase);
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase)
                 || args.Contains("-f", StringComparer.OrdinalIgnoreCase);
        var showHelp = args.Contains("--help", StringComparer.OrdinalIgnoreCase)
                    || args.Contains("-h", StringComparer.OrdinalIgnoreCase);

        if (showHelp)
        {
            PrintHelp();
            return 0;
        }

        var currentVersion = VersionCommand.GetVersionLabel();
        System.Console.WriteLine($"Current version: {currentVersion}");
        System.Console.WriteLine();

        try
        {
            System.Console.WriteLine("Checking for updates...");
            var latest = await KowareUpdater.GetLatestVersionAsync(context.CancellationToken);

            if (string.IsNullOrWhiteSpace(latest.Tag))
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("Could not determine latest version from GitHub.");
                System.Console.ResetColor();
                return 1;
            }

            System.Console.WriteLine($"Latest version:  {latest.Tag}");
            if (!string.IsNullOrWhiteSpace(latest.Name) && latest.Name != latest.Tag)
            {
                System.Console.WriteLine($"Release name:    {latest.Name}");
            }
            System.Console.WriteLine();

            // Compare versions
            var current = VersionCommand.TryParseVersionCore(currentVersion);
            var latestParsed = VersionCommand.TryParseVersionCore(latest.Tag);

            bool isUpToDate = false;
            if (current != null && latestParsed != null)
            {
                isUpToDate = current >= latestParsed;
            }

            if (isUpToDate && !force)
            {
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine("You are already on the latest version!");
                System.Console.ResetColor();
                return 0;
            }

            if (!isUpToDate)
            {
                System.Console.ForegroundColor = ConsoleColor.Cyan;
                System.Console.WriteLine("A new version is available!");
                System.Console.ResetColor();
            }
            else if (force)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("Forcing update (already on latest version)...");
                System.Console.ResetColor();
            }

            if (checkOnly)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("Run 'koware update' without --check to download and install.");
                return isUpToDate ? 0 : 2; // Exit code 2 = update available
            }

            System.Console.WriteLine();

            // Check platform support
            if (!OperatingSystem.IsWindows())
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("Auto-update is currently only supported on Windows.");
                System.Console.WriteLine("Please download the latest release manually from:");
                System.Console.WriteLine("  https://github.com/S1mplector/Koware/releases");
                System.Console.ResetColor();
                return 1;
            }

            // Download and run installer
            var progress = new Progress<string>(message =>
            {
                System.Console.WriteLine($"  {message}");
            });

            var result = await KowareUpdater.DownloadAndRunLatestInstallerAsync(progress, context.CancellationToken);

            if (!result.Success)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"Update failed: {result.Error}");
                System.Console.ResetColor();
                return 1;
            }

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;

            if (result.InstallerLaunched)
            {
                System.Console.WriteLine("Installer launched successfully!");
                System.Console.WriteLine("Please follow the installer prompts to complete the update.");
            }
            else if (!string.IsNullOrWhiteSpace(result.ExtractPath))
            {
                System.Console.WriteLine($"Update downloaded to: {result.ExtractPath}");
                System.Console.WriteLine("Please complete the installation manually.");
            }

            System.Console.ResetColor();
            return 0;
        }
        catch (OperationCanceledException)
        {
            System.Console.WriteLine("Update cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error checking for updates: {ex.Message}");
            System.Console.ResetColor();
            return 1;
        }
    }

    private static void PrintHelp()
    {
        System.Console.WriteLine("Usage: koware update [options]");
        System.Console.WriteLine();
        System.Console.WriteLine("Check for updates and download the latest version of Koware.");
        System.Console.WriteLine();
        System.Console.WriteLine("Options:");
        System.Console.WriteLine("  -c, --check    Check for updates without downloading");
        System.Console.WriteLine("  -f, --force    Download even if already on the latest version");
        System.Console.WriteLine("  -h, --help     Show this help message");
        System.Console.WriteLine();
        System.Console.WriteLine("Examples:");
        System.Console.WriteLine("  koware update           Download and install the latest version");
        System.Console.WriteLine("  koware update --check   Check if an update is available");
        System.Console.WriteLine("  koware update --force   Force re-download of the latest version");
    }
}
