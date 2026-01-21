// Author: Ilgaz Mehmetoğlu
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SystemConsole = System.Console;

namespace Koware.Cli.Commands;

/// <summary>
/// Implements the 'koware sync' command: sync data across devices.
/// Supports export/import of history, lists, and configuration.
/// </summary>
public sealed class SyncCommand : ICliCommand
{
    public string Name => "sync";
    public IReadOnlyList<string> Aliases => ["backup", "restore"];
    public string Description => "Sync/backup data across devices (history, lists, config)";
    public bool RequiresProvider => false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

        return subcommand switch
        {
            "export" => await ExportAsync(args, context),
            "import" => await ImportAsync(args, context),
            "status" => await StatusAsync(context),
            "push" => await PushAsync(args, context),
            "pull" => await PullAsync(args, context),
            "help" or "--help" or "-h" => ShowHelp(),
            _ => ShowHelp()
        };
    }

    private static async Task<int> StatusAsync(CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var historyDb = Path.Combine(dataDir, "history.db");
        var userConfig = Path.Combine(dataDir, "appsettings.user.json");

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Koware Sync Status");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine(new string('─', 50));

        SystemConsole.WriteLine();
        WriteField("Data Directory", dataDir);
        SystemConsole.WriteLine();

        // History database
        if (File.Exists(historyDb))
        {
            var info = new FileInfo(historyDb);
            WriteField("History DB", $"{info.Length / 1024.0:F1} KB");
            WriteField("  Modified", info.LastWriteTime.ToString("g"));
        }
        else
        {
            WriteField("History DB", "(not found)", ConsoleColor.Yellow);
        }

        // User config
        if (File.Exists(userConfig))
        {
            var info = new FileInfo(userConfig);
            WriteField("User Config", $"{info.Length} bytes");
            WriteField("  Modified", info.LastWriteTime.ToString("g"));
        }
        else
        {
            WriteField("User Config", "(using defaults)", ConsoleColor.DarkGray);
        }

        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine("Commands:");
        SystemConsole.WriteLine("  koware sync export <file>   Export data to sync file");
        SystemConsole.WriteLine("  koware sync import <file>   Import data from sync file");
        SystemConsole.WriteLine("  koware sync push <path>     Push to sync directory/remote");
        SystemConsole.WriteLine("  koware sync pull <path>     Pull from sync directory/remote");
        SystemConsole.ResetColor();

        return 0;
    }

    private static async Task<int> ExportAsync(string[] args, CommandContext context)
    {
        var outputPath = args.Length > 2 ? args[2] : $"koware-sync-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        
        // Expand ~ to home directory
        if (outputPath.StartsWith("~/"))
        {
            outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), outputPath[2..]);
        }

        var dataDir = GetDataDirectory();
        var historyDb = Path.Combine(dataDir, "history.db");
        var userConfig = Path.Combine(dataDir, "appsettings.user.json");

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Exporting Koware data...");
        SystemConsole.ResetColor();

        try
        {
            using var zipStream = new FileStream(outputPath, FileMode.Create);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            // Export history database
            if (File.Exists(historyDb))
            {
                var entry = archive.CreateEntry("history.db", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(historyDb);
                await fileStream.CopyToAsync(entryStream, context.CancellationToken);
                WriteStatus("History DB", "exported");
            }
            else
            {
                WriteStatus("History DB", "skipped (not found)", ConsoleColor.Yellow);
            }

            // Export user config
            if (File.Exists(userConfig))
            {
                var entry = archive.CreateEntry("appsettings.user.json", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(userConfig);
                await fileStream.CopyToAsync(entryStream, context.CancellationToken);
                WriteStatus("User Config", "exported");
            }
            else
            {
                WriteStatus("User Config", "skipped (using defaults)", ConsoleColor.DarkGray);
            }

            // Add metadata
            var metadata = new SyncMetadata
            {
                ExportedAt = DateTimeOffset.UtcNow,
                MachineName = Environment.MachineName,
                Username = Environment.UserName,
                KowareVersion = "1.0.0",
                Platform = Environment.OSVersion.Platform.ToString()
            };
            var metaEntry = archive.CreateEntry("sync-metadata.json", CompressionLevel.Optimal);
            using (var metaStream = metaEntry.Open())
            {
                await JsonSerializer.SerializeAsync(metaStream, metadata, JsonOptions, context.CancellationToken);
            }

            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine($"✓ Exported to: {Path.GetFullPath(outputPath)}");
            SystemConsole.ResetColor();

            var fileInfo = new FileInfo(outputPath);
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine($"  Size: {fileInfo.Length / 1024.0:F1} KB");
            SystemConsole.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Export failed");
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Export failed: {ex.Message}");
            SystemConsole.ResetColor();
            return 1;
        }
    }

    private static async Task<int> ImportAsync(string[] args, CommandContext context)
    {
        if (args.Length < 3)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Error: Please specify a sync file to import.");
            SystemConsole.WriteLine("Usage: koware sync import <file.zip>");
            SystemConsole.ResetColor();
            return 1;
        }

        var inputPath = args[2];
        
        // Expand ~ to home directory
        if (inputPath.StartsWith("~/"))
        {
            inputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), inputPath[2..]);
        }

        if (!File.Exists(inputPath))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Error: File not found: {inputPath}");
            SystemConsole.ResetColor();
            return 1;
        }

        var dataDir = GetDataDirectory();
        Directory.CreateDirectory(dataDir);

        var merge = args.Any(a => a.Equals("--merge", StringComparison.OrdinalIgnoreCase));
        var force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine($"Importing Koware data from: {inputPath}");
        SystemConsole.ResetColor();

        try
        {
            using var zipStream = File.OpenRead(inputPath);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            // Read metadata
            var metaEntry = archive.GetEntry("sync-metadata.json");
            if (metaEntry != null)
            {
                using var metaStream = metaEntry.Open();
                var metadata = await JsonSerializer.DeserializeAsync<SyncMetadata>(metaStream, JsonOptions, context.CancellationToken);
                if (metadata != null)
                {
                    SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                    SystemConsole.WriteLine($"  From: {metadata.MachineName} ({metadata.Platform})");
                    SystemConsole.WriteLine($"  Date: {metadata.ExportedAt.LocalDateTime:g}");
                    SystemConsole.ResetColor();
                    SystemConsole.WriteLine();
                }
            }

            // Import history database
            var historyEntry = archive.GetEntry("history.db");
            if (historyEntry != null)
            {
                var historyPath = Path.Combine(dataDir, "history.db");
                var exists = File.Exists(historyPath);

                if (exists && !force && !merge)
                {
                    SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                    SystemConsole.WriteLine("History DB already exists. Use --force to overwrite or --merge to merge.");
                    SystemConsole.ResetColor();
                }
                else if (merge && exists)
                {
                    // For merge, we'd need to implement SQLite merge logic
                    // For now, just backup and replace
                    var backupPath = historyPath + $".backup-{DateTime.Now:yyyyMMddHHmmss}";
                    File.Copy(historyPath, backupPath);
                    WriteStatus("History DB", $"backed up to {Path.GetFileName(backupPath)}");
                    
                    historyEntry.ExtractToFile(historyPath, true);
                    WriteStatus("History DB", "imported (merge not yet implemented, replaced)");
                }
                else
                {
                    if (exists)
                    {
                        var backupPath = historyPath + $".backup-{DateTime.Now:yyyyMMddHHmmss}";
                        File.Copy(historyPath, backupPath);
                        WriteStatus("History DB", $"backed up existing");
                    }
                    historyEntry.ExtractToFile(historyPath, true);
                    WriteStatus("History DB", "imported");
                }
            }

            // Import user config
            var configEntry = archive.GetEntry("appsettings.user.json");
            if (configEntry != null)
            {
                var configPath = Path.Combine(dataDir, "appsettings.user.json");
                var exists = File.Exists(configPath);

                if (exists && !force)
                {
                    var backupPath = configPath + $".backup-{DateTime.Now:yyyyMMddHHmmss}";
                    File.Copy(configPath, backupPath);
                    WriteStatus("User Config", "backed up existing");
                }
                configEntry.ExtractToFile(configPath, true);
                WriteStatus("User Config", "imported");
            }

            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine("✓ Import complete");
            SystemConsole.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Import failed");
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Import failed: {ex.Message}");
            SystemConsole.ResetColor();
            return 1;
        }
    }

    private static async Task<int> PushAsync(string[] args, CommandContext context)
    {
        if (args.Length < 3)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Error: Please specify a sync directory.");
            SystemConsole.WriteLine("Usage: koware sync push <directory>");
            SystemConsole.WriteLine("Example: koware sync push ~/Dropbox/KowareSync");
            SystemConsole.ResetColor();
            return 1;
        }

        var syncDir = args[2];
        
        // Expand ~ to home directory
        if (syncDir.StartsWith("~/"))
        {
            syncDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), syncDir[2..]);
        }

        Directory.CreateDirectory(syncDir);

        var exportFile = Path.Combine(syncDir, $"koware-{Environment.MachineName}.zip");
        
        // Reuse export logic
        var exportArgs = new[] { "sync", "export", exportFile };
        return await ExportAsync(exportArgs, context);
    }

    private static async Task<int> PullAsync(string[] args, CommandContext context)
    {
        if (args.Length < 3)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Error: Please specify a sync directory or file.");
            SystemConsole.WriteLine("Usage: koware sync pull <directory|file>");
            SystemConsole.ResetColor();
            return 1;
        }

        var syncPath = args[2];
        
        // Expand ~ to home directory
        if (syncPath.StartsWith("~/"))
        {
            syncPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), syncPath[2..]);
        }

        string importFile;

        if (Directory.Exists(syncPath))
        {
            // Find the most recent sync file (not from this machine)
            var syncFiles = Directory.GetFiles(syncPath, "koware-*.zip")
                .Where(f => !Path.GetFileName(f).Contains(Environment.MachineName))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToList();

            if (syncFiles.Count == 0)
            {
                // Try any sync file
                syncFiles = Directory.GetFiles(syncPath, "koware-*.zip")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .ToList();
            }

            if (syncFiles.Count == 0)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                SystemConsole.WriteLine("No sync files found in directory.");
                SystemConsole.ResetColor();
                return 1;
            }

            importFile = syncFiles.First();
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine($"Using: {Path.GetFileName(importFile)}");
            SystemConsole.ResetColor();
        }
        else if (File.Exists(syncPath))
        {
            importFile = syncPath;
        }
        else
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Error: Path not found: {syncPath}");
            SystemConsole.ResetColor();
            return 1;
        }

        // Reuse import logic with --force
        var importArgs = new[] { "sync", "import", importFile, "--force" };
        return await ImportAsync(importArgs, context);
    }

    private static int ShowHelp()
    {
        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("koware sync - Sync data across devices");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Usage: koware sync <command> [options]");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Commands:");
        SystemConsole.WriteLine("  status              Show sync status and data locations");
        SystemConsole.WriteLine("  export <file>       Export data to a sync file (.zip)");
        SystemConsole.WriteLine("  import <file>       Import data from a sync file");
        SystemConsole.WriteLine("  push <directory>    Push sync file to a shared directory");
        SystemConsole.WriteLine("  pull <directory>    Pull latest sync file from shared directory");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Options:");
        SystemConsole.WriteLine("  --force             Overwrite existing data without prompting");
        SystemConsole.WriteLine("  --merge             Attempt to merge data (not fully implemented)");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Examples:");
        SystemConsole.WriteLine("  koware sync export ~/koware-backup.zip");
        SystemConsole.WriteLine("  koware sync import ~/koware-backup.zip --force");
        SystemConsole.WriteLine("  koware sync push ~/Dropbox/KowareSync");
        SystemConsole.WriteLine("  koware sync pull ~/Dropbox/KowareSync");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Synced Data:");
        SystemConsole.WriteLine("  - Watch/read history");
        SystemConsole.WriteLine("  - Anime/manga lists");
        SystemConsole.WriteLine("  - Download records");
        SystemConsole.WriteLine("  - User configuration");

        return 0;
    }

    private static string GetDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "koware");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "koware");
    }

    private static void WriteField(string label, string value, ConsoleColor color = ConsoleColor.White)
    {
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.Write($"  {label,-15} ");
        SystemConsole.ForegroundColor = color;
        SystemConsole.WriteLine(value);
        SystemConsole.ResetColor();
    }

    private static void WriteStatus(string item, string status, ConsoleColor color = ConsoleColor.Green)
    {
        SystemConsole.ForegroundColor = ConsoleColor.White;
        SystemConsole.Write($"  {item,-15} ");
        SystemConsole.ForegroundColor = color;
        SystemConsole.WriteLine(status);
        SystemConsole.ResetColor();
    }

    private sealed class SyncMetadata
    {
        public DateTimeOffset ExportedAt { get; set; }
        public string MachineName { get; set; } = "";
        public string Username { get; set; } = "";
        public string KowareVersion { get; set; } = "";
        public string Platform { get; set; } = "";
    }
}
