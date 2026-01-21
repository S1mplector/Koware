// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SystemConsole = System.Console;

namespace Koware.Cli.Commands;

/// <summary>
/// Implements the 'koware sync' command: sync data across devices using git.
/// The data directory is a git repository that can be pushed/pulled to a remote.
/// </summary>
public sealed class SyncCommand : ICliCommand
{
    public string Name => "sync";
    public IReadOnlyList<string> Aliases => ["backup", "restore"];
    public string Description => "Sync data across devices using git (history, lists, config)";
    public bool RequiresProvider => false;

    public async Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

        return subcommand switch
        {
            "init" => await InitAsync(args, context),
            "status" => await StatusAsync(context),
            "push" => await PushAsync(args, context),
            "pull" => await PullAsync(context),
            "log" => await LogAsync(context),
            "clone" => await CloneAsync(args, context),
            "help" or "--help" or "-h" => ShowHelp(),
            _ => ShowHelp()
        };
    }

    private static async Task<int> InitAsync(string[] args, CommandContext context)
    {
        var dataDir = GetDataDirectory();
        Directory.CreateDirectory(dataDir);
        
        var gitDir = Path.Combine(dataDir, ".git");
        if (Directory.Exists(gitDir))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("Git repository already initialized.");
            SystemConsole.ResetColor();
            
            // Check for remote
            var (_, remoteOutput, _) = await RunGitAsync(dataDir, "remote -v");
            if (!string.IsNullOrWhiteSpace(remoteOutput))
            {
                SystemConsole.WriteLine("Remote:");
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine($"  {remoteOutput.Split('\n')[0]}");
                SystemConsole.ResetColor();
            }
            return 0;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Initializing git sync repository...");
        SystemConsole.ResetColor();

        // Initialize git repo
        var (exitCode, output, error) = await RunGitAsync(dataDir, "init");
        if (exitCode != 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Failed to initialize git: {error}");
            SystemConsole.ResetColor();
            return 1;
        }

        // Create .gitignore
        var gitignorePath = Path.Combine(dataDir, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, """
            # Temporary files
            *.tmp
            *.bak
            *.backup-*
            
            # Logs
            *.log
            
            # SQLite journal files
            *.db-journal
            *.db-wal
            *.db-shm
            """, context.CancellationToken);

        // Initial commit
        await RunGitAsync(dataDir, "add -A");
        await RunGitAsync(dataDir, $"commit -m \"Initial koware sync\" --allow-empty");

        // Add remote if provided
        if (args.Length > 2)
        {
            var remote = args[2];
            await RunGitAsync(dataDir, $"remote add origin {remote}");
            WriteStatus("Remote", remote);
        }

        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.Green;
        SystemConsole.WriteLine($"[+] Git sync initialized at: {dataDir}");
        SystemConsole.ResetColor();

        if (args.Length <= 2)
        {
            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("To add a remote: koware sync init <git-url>");
            SystemConsole.WriteLine("Example: koware sync init git@github.com:user/koware-sync.git");
            SystemConsole.ResetColor();
        }

        return 0;
    }

    private static async Task<int> StatusAsync(CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var gitDir = Path.Combine(dataDir, ".git");
        var historyDb = Path.Combine(dataDir, "history.db");
        var userConfig = Path.Combine(dataDir, "appsettings.user.json");

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Koware Sync Status");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine(new string('─', 50));

        SystemConsole.WriteLine();
        WriteField("Data Directory", dataDir);

        // Git status
        if (Directory.Exists(gitDir))
        {
            WriteField("Git Sync", "enabled", ConsoleColor.Green);
            
            // Get current branch
            var (_, branch, _) = await RunGitAsync(dataDir, "branch --show-current");
            if (!string.IsNullOrWhiteSpace(branch))
                WriteField("  Branch", branch.Trim());

            // Get remote
            var (_, remote, _) = await RunGitAsync(dataDir, "remote get-url origin");
            if (!string.IsNullOrWhiteSpace(remote))
                WriteField("  Remote", remote.Trim());
            else
                WriteField("  Remote", "(not configured)", ConsoleColor.Yellow);

            // Check for uncommitted changes
            var (_, statusOutput, _) = await RunGitAsync(dataDir, "status --porcelain");
            if (!string.IsNullOrWhiteSpace(statusOutput))
            {
                var changeCount = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                WriteField("  Changes", $"{changeCount} uncommitted", ConsoleColor.Yellow);
            }
            else
            {
                WriteField("  Changes", "clean", ConsoleColor.Green);
            }
        }
        else
        {
            WriteField("Git Sync", "not initialized", ConsoleColor.Yellow);
            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("  Run 'koware sync init' to enable git sync");
            SystemConsole.ResetColor();
        }

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
        SystemConsole.WriteLine("  koware sync init [remote]   Initialize git sync");
        SystemConsole.WriteLine("  koware sync push            Commit and push changes");
        SystemConsole.WriteLine("  koware sync pull            Pull and apply remote changes");
        SystemConsole.WriteLine("  koware sync log             Show sync history");
        SystemConsole.WriteLine("  koware sync clone <url>     Clone from existing sync repo");
        SystemConsole.ResetColor();

        return 0;
    }

    private static async Task<int> PushAsync(string[] args, CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var gitDir = Path.Combine(dataDir, ".git");

        if (!Directory.Exists(gitDir))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Git sync not initialized. Run 'koware sync init' first.");
            SystemConsole.ResetColor();
            return 1;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Pushing koware data...");
        SystemConsole.ResetColor();

        // Stage all changes
        var (addCode, _, addError) = await RunGitAsync(dataDir, "add -A");
        if (addCode != 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Failed to stage changes: {addError}");
            SystemConsole.ResetColor();
            return 1;
        }

        // Check if there are changes to commit
        var (_, statusOutput, _) = await RunGitAsync(dataDir, "status --porcelain");
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("No changes to push.");
            SystemConsole.ResetColor();
            
            // Still try to push in case there are unpushed commits
        }
        else
        {
            // Commit with message
            var message = args.Length > 2 ? string.Join(" ", args.Skip(2)) : $"Sync from {Environment.MachineName} at {DateTime.Now:g}";
            var (commitCode, commitOutput, commitError) = await RunGitAsync(dataDir, $"commit -m \"{message}\"");
            
            if (commitCode != 0 && !commitError.Contains("nothing to commit"))
            {
                SystemConsole.ForegroundColor = ConsoleColor.Red;
                SystemConsole.WriteLine($"Failed to commit: {commitError}");
                SystemConsole.ResetColor();
                return 1;
            }
            
            WriteStatus("Committed", message);
        }

        // Check if remote exists
        var (_, remote, _) = await RunGitAsync(dataDir, "remote get-url origin");
        if (string.IsNullOrWhiteSpace(remote))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("No remote configured. Changes committed locally only.");
            SystemConsole.WriteLine("Add a remote with: koware sync init <git-url>");
            SystemConsole.ResetColor();
            return 0;
        }

        // Push to remote
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine($"Pushing to {remote.Trim()}...");
        SystemConsole.ResetColor();

        var (pushCode, pushOutput, pushError) = await RunGitAsync(dataDir, "push -u origin HEAD");
        if (pushCode != 0)
        {
            // Try setting upstream if first push
            if (pushError.Contains("no upstream branch"))
            {
                (pushCode, pushOutput, pushError) = await RunGitAsync(dataDir, "push --set-upstream origin main");
            }
            
            if (pushCode != 0)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Red;
                SystemConsole.WriteLine($"Failed to push: {pushError}");
                SystemConsole.ResetColor();
                return 1;
            }
        }

        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.Green;
        SystemConsole.WriteLine("[+] Pushed successfully");
        SystemConsole.ResetColor();

        return 0;
    }

    private static async Task<int> PullAsync(CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var gitDir = Path.Combine(dataDir, ".git");

        if (!Directory.Exists(gitDir))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Git sync not initialized. Run 'koware sync init' or 'koware sync clone' first.");
            SystemConsole.ResetColor();
            return 1;
        }

        // Check if remote exists
        var (_, remote, _) = await RunGitAsync(dataDir, "remote get-url origin");
        if (string.IsNullOrWhiteSpace(remote))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("No remote configured. Add a remote with: koware sync init <git-url>");
            SystemConsole.ResetColor();
            return 1;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Pulling koware data...");
        SystemConsole.ResetColor();

        // Stash any local changes
        var (_, statusOutput, _) = await RunGitAsync(dataDir, "status --porcelain");
        var hasLocalChanges = !string.IsNullOrWhiteSpace(statusOutput);
        
        if (hasLocalChanges)
        {
            WriteStatus("Local changes", "stashing...", ConsoleColor.Yellow);
            await RunGitAsync(dataDir, "stash");
        }

        // Pull from remote
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine($"Pulling from {remote.Trim()}...");
        SystemConsole.ResetColor();

        var (pullCode, pullOutput, pullError) = await RunGitAsync(dataDir, "pull --rebase origin HEAD");
        if (pullCode != 0)
        {
            // Try without rebase
            (pullCode, pullOutput, pullError) = await RunGitAsync(dataDir, "pull origin HEAD");
            
            if (pullCode != 0)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Red;
                SystemConsole.WriteLine($"Failed to pull: {pullError}");
                SystemConsole.ResetColor();
                
                // Restore stashed changes
                if (hasLocalChanges)
                {
                    await RunGitAsync(dataDir, "stash pop");
                }
                return 1;
            }
        }

        // Restore stashed changes
        if (hasLocalChanges)
        {
            WriteStatus("Local changes", "restoring...", ConsoleColor.Yellow);
            var (stashCode, _, stashError) = await RunGitAsync(dataDir, "stash pop");
            if (stashCode != 0 && !stashError.Contains("No stash entries"))
            {
                SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                SystemConsole.WriteLine($"Warning: Could not restore local changes: {stashError}");
                SystemConsole.WriteLine("Your changes are still in git stash.");
                SystemConsole.ResetColor();
            }
        }

        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.Green;
        SystemConsole.WriteLine("[+] Pulled successfully");
        SystemConsole.ResetColor();

        return 0;
    }

    private static async Task<int> LogAsync(CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var gitDir = Path.Combine(dataDir, ".git");

        if (!Directory.Exists(gitDir))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Git sync not initialized.");
            SystemConsole.ResetColor();
            return 1;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Sync History");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine(new string('─', 50));

        var (exitCode, output, error) = await RunGitAsync(dataDir, "log --oneline -20 --format=\"%C(yellow)%h%C(reset) %C(dim)%cr%C(reset) %s\"");
        if (exitCode != 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Failed to get log: {error}");
            SystemConsole.ResetColor();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("No sync history yet.");
            SystemConsole.ResetColor();
        }
        else
        {
            SystemConsole.WriteLine(output);
        }

        return 0;
    }

    private static async Task<int> CloneAsync(string[] args, CommandContext context)
    {
        if (args.Length < 3)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Error: Please specify a git URL to clone from.");
            SystemConsole.WriteLine("Usage: koware sync clone <git-url>");
            SystemConsole.WriteLine("Example: koware sync clone git@github.com:user/koware-sync.git");
            SystemConsole.ResetColor();
            return 1;
        }

        var remote = args[2];
        var dataDir = GetDataDirectory();
        var gitDir = Path.Combine(dataDir, ".git");

        if (Directory.Exists(gitDir))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Git repository already exists. Use 'koware sync pull' instead.");
            SystemConsole.ResetColor();
            return 1;
        }

        // Backup existing data
        var hasExistingData = Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any();
        string? backupDir = null;
        
        if (hasExistingData)
        {
            backupDir = dataDir + $".backup-{DateTime.Now:yyyyMMddHHmmss}";
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine($"Backing up existing data to: {Path.GetFileName(backupDir)}");
            SystemConsole.ResetColor();
            Directory.Move(dataDir, backupDir);
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine($"Cloning from {remote}...");
        SystemConsole.ResetColor();

        // Clone the repository
        var parentDir = Path.GetDirectoryName(dataDir)!;
        Directory.CreateDirectory(parentDir);
        
        var (exitCode, output, error) = await RunGitAsync(parentDir, $"clone {remote} koware");
        if (exitCode != 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Failed to clone: {error}");
            SystemConsole.ResetColor();
            
            // Restore backup if clone failed
            if (backupDir != null && Directory.Exists(backupDir))
            {
                Directory.Move(backupDir, dataDir);
                SystemConsole.WriteLine("Restored backup data.");
            }
            return 1;
        }

        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.Green;
        SystemConsole.WriteLine("[+] Cloned successfully");
        SystemConsole.ResetColor();

        if (backupDir != null)
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine($"Previous data backed up at: {backupDir}");
            SystemConsole.ResetColor();
        }

        return 0;
    }

    private static int ShowHelp()
    {
        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("koware sync - Sync data across devices using git");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Usage: koware sync <command> [options]");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Commands:");
        SystemConsole.WriteLine("  status              Show sync status");
        SystemConsole.WriteLine("  init [url]          Initialize git sync (optionally with remote)");
        SystemConsole.WriteLine("  push [message]      Commit and push changes to remote");
        SystemConsole.WriteLine("  pull                Pull changes from remote");
        SystemConsole.WriteLine("  log                 Show sync history");
        SystemConsole.WriteLine("  clone <url>         Clone from existing sync repository");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Setup (new sync):");
        SystemConsole.WriteLine("  1. Create a private git repo (GitHub, GitLab, etc.)");
        SystemConsole.WriteLine("  2. koware sync init git@github.com:user/koware-sync.git");
        SystemConsole.WriteLine("  3. koware sync push");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Setup (existing sync):");
        SystemConsole.WriteLine("  koware sync clone git@github.com:user/koware-sync.git");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Daily use:");
        SystemConsole.WriteLine("  koware sync push    # After watching/reading");
        SystemConsole.WriteLine("  koware sync pull    # On another device");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Synced Data:");
        SystemConsole.WriteLine("  - Watch/read history (history.db)");
        SystemConsole.WriteLine("  - Anime/manga lists");
        SystemConsole.WriteLine("  - User configuration (appsettings.user.json)");

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

    private static async Task<(int exitCode, string output, string error)> RunGitAsync(string workingDir, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return (process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
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
}
