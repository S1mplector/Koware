// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using System.Text;
using Koware.Cli.Console;
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
        var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : null;

        // If no subcommand, show interactive menu
        if (subcommand == null)
        {
            return await ShowInteractiveMenuAsync(args, context);
        }

        return subcommand switch
        {
            "init" => await InitAsync(args, context),
            "status" => await StatusAsync(context),
            "push" => await PushAsync(args, context),
            "pull" => await PullAsync(context),
            "log" => await LogAsync(context),
            "clone" => await CloneAsync(args, context),
            "auto" => await AutoSyncAsync(args, context),
            "now" or "quick" => await QuickSyncAsync(context),
            "diff" => await DiffAsync(context),
            "resolve" => await ResolveConflictsAsync(context),
            "abort" => await AbortMergeAsync(context),
            "help" or "--help" or "-h" => ShowHelp(),
            _ => await ShowInteractiveMenuAsync(args, context)
        };
    }

    private static async Task<int> ShowInteractiveMenuAsync(string[] args, CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var gitDir = Path.Combine(dataDir, ".git");
        var isInitialized = Directory.Exists(gitDir);

        // Build menu items based on current state
        var menuItems = new List<SyncMenuItem>();

        if (!isInitialized)
        {
            // Not initialized - show setup options
            menuItems.Add(new SyncMenuItem("init", $"{Icons.Add} Initialize", "Set up git sync (auto-creates GitHub repo if gh CLI available)"));
            menuItems.Add(new SyncMenuItem("clone", $"{Icons.Download} Clone existing", "Clone from an existing sync repository URL"));
        }
        else
        {
            // Check for pending changes and remote status
            var (_, statusOutput, _) = await RunGitAsync(dataDir, "status --porcelain");
            var hasChanges = !string.IsNullOrWhiteSpace(statusOutput);
            var changeCount = hasChanges ? statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;

            var (_, remote, _) = await RunGitAsync(dataDir, "remote get-url origin");
            var hasRemote = !string.IsNullOrWhiteSpace(remote);

            // Check for merge conflicts
            var mergeEngine = new SyncMergeEngine(dataDir);
            var hasConflicts = await mergeEngine.HasUnmergedFilesAsync();

            if (hasConflicts)
            {
                menuItems.Add(new SyncMenuItem("resolve", $"{Icons.Warning} Resolve conflicts", "Auto-resolve merge conflicts from previous sync"));
                menuItems.Add(new SyncMenuItem("abort", $"{Icons.Error} Abort merge", "Cancel merge and restore previous state"));
            }

            // Primary actions
            menuItems.Add(new SyncMenuItem("quick", $"{Icons.Play} Quick sync", hasChanges 
                ? $"Pull → Commit {changeCount} change(s) → Push" 
                : "Pull → Push (no local changes)"));

            if (hasChanges)
            {
                menuItems.Add(new SyncMenuItem("diff", $"{Icons.Search} View changes", $"Preview {changeCount} pending change(s)"));
                menuItems.Add(new SyncMenuItem("push", $"{Icons.Add} Push", $"Commit and push {changeCount} change(s)"));
            }

            menuItems.Add(new SyncMenuItem("pull", $"{Icons.Download} Pull", "Pull changes from remote (auto-merges conflicts)"));
            menuItems.Add(new SyncMenuItem("status", $"{Icons.Info} Status", "Show detailed sync status"));
            menuItems.Add(new SyncMenuItem("log", $"{Icons.History} History", "View recent sync history"));

            // Settings
            var configPath = Path.Combine(dataDir, "sync.config");
            var autoEnabled = File.Exists(configPath) && File.ReadAllText(configPath).Trim() == "enabled";
            menuItems.Add(new SyncMenuItem("auto", $"{Icons.Provider} Auto-sync", autoEnabled ? "Currently: ON" : "Currently: OFF"));
        }

        menuItems.Add(new SyncMenuItem("exit", $"{Icons.Back} Exit", "Return to shell"));

        // Show the menu
        while (true)
        {
            var selector = new InteractiveSelector<SyncMenuItem>(
                menuItems,
                m => m.Label,
                new SelectorOptions<SyncMenuItem>
                {
                    Prompt = $"{Icons.Provider} Sync",
                    PreviewFunc = m => m.Description,
                    ShowSearch = false,
                    ShowPreview = true,
                    MaxVisibleItems = 10,
                    EmptyMessage = "No options available"
                });

            var result = selector.Run();
            if (result.Cancelled || result.Selected?.Id == "exit")
            {
                return 0;
            }

            var choice = result.Selected!.Id;
            int exitCode;

            switch (choice)
            {
                case "init":
                    exitCode = await InitAsync(args, context);
                    break;
                case "clone":
                    SystemConsole.Write("Enter repository URL: ");
                    var url = SystemConsole.ReadLine()?.Trim();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                        SystemConsole.WriteLine("No URL provided.");
                        SystemConsole.ResetColor();
                        continue;
                    }
                    exitCode = await CloneAsync(new[] { "sync", "clone", url }, context);
                    break;
                case "quick":
                    exitCode = await QuickSyncAsync(context);
                    break;
                case "push":
                    exitCode = await PushAsync(args, context);
                    break;
                case "pull":
                    exitCode = await PullAsync(context);
                    break;
                case "status":
                    exitCode = await StatusAsync(context);
                    break;
                case "diff":
                    exitCode = await DiffAsync(context);
                    break;
                case "log":
                    exitCode = await LogAsync(context);
                    break;
                case "auto":
                    exitCode = await ShowAutoSyncMenuAsync(context);
                    break;
                case "resolve":
                    exitCode = await ResolveConflictsAsync(context);
                    break;
                case "abort":
                    exitCode = await AbortMergeAsync(context);
                    break;
                default:
                    continue;
            }

            // After action, wait for user before returning to menu
            if (choice != "auto")
            {
                WaitForContinue();
            }

            // Refresh menu items after action
            return await ShowInteractiveMenuAsync(args, context);
        }
    }

    private static async Task<int> ShowAutoSyncMenuAsync(CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var configPath = Path.Combine(dataDir, "sync.config");
        var autoEnabled = File.Exists(configPath) && File.ReadAllText(configPath).Trim() == "enabled";

        var menuItems = new[]
        {
            new SyncMenuItem("on", $"{Icons.Success} Enable", "Sync automatically when Koware runs"),
            new SyncMenuItem("off", $"{Icons.Error} Disable", "Only sync manually with koware sync"),
            new SyncMenuItem("back", $"{Icons.Back} Back", "Return to sync menu")
        };

        var selector = new InteractiveSelector<SyncMenuItem>(
            menuItems,
            m => m.Label,
            new SelectorOptions<SyncMenuItem>
            {
                Prompt = $"Auto-sync ({(autoEnabled ? "ON" : "OFF")})",
                PreviewFunc = m => m.Description,
                ShowSearch = false,
                ShowPreview = true,
                MaxVisibleItems = 5
            });

        var result = selector.Run();
        if (result.Cancelled || result.Selected?.Id == "back")
        {
            return 0;
        }

        var choice = result.Selected!.Id;
        if (choice == "on")
        {
            await File.WriteAllTextAsync(configPath, "enabled");
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine("[+] Auto-sync enabled");
            SystemConsole.ResetColor();
        }
        else if (choice == "off")
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("[!] Auto-sync disabled");
            SystemConsole.ResetColor();
        }

        return 0;
    }

    private static void WaitForContinue()
    {
        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine("Press any key to continue...");
        SystemConsole.ResetColor();
        SystemConsole.ReadKey(true);
    }

    private sealed record SyncMenuItem(string Id, string Label, string Description);

    private static async Task<int> InitAsync(string[] args, CommandContext context)
    {
        var dataDir = GetDataDirectory();
        Directory.CreateDirectory(dataDir);
        
        var gitDir = Path.Combine(dataDir, ".git");
        var repoAlreadyExists = Directory.Exists(gitDir);
        
        // Check for existing remote
        string? existingRemote = null;
        if (repoAlreadyExists)
        {
            var (_, remoteOutput, _) = await RunGitAsync(dataDir, "remote get-url origin");
            existingRemote = string.IsNullOrWhiteSpace(remoteOutput) ? null : remoteOutput.Trim();
        }

        // Determine remote URL
        string? remoteUrl = args.Length > 2 ? args[2] : null;
        
        // If no remote provided and no existing remote, try to auto-create with gh CLI
        if (remoteUrl == null && existingRemote == null)
        {
            var ghAvailable = await IsGhCliAvailableAsync();
            if (ghAvailable)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Cyan;
                SystemConsole.WriteLine("GitHub CLI detected. Creating private repository...");
                SystemConsole.ResetColor();
                
                remoteUrl = await CreateGitHubRepoAsync();
                if (remoteUrl != null)
                {
                    WriteStatus("Created", remoteUrl, ConsoleColor.Green);
                }
            }
        }

        if (repoAlreadyExists)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("Git repository already initialized.");
            SystemConsole.ResetColor();
            
            // Add remote if we have one and don't already have one
            if (remoteUrl != null && existingRemote == null)
            {
                await RunGitAsync(dataDir, $"remote add origin {remoteUrl}");
                WriteStatus("Remote added", remoteUrl);
            }
            else if (existingRemote != null)
            {
                WriteStatus("Remote", existingRemote);
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

        // Add remote if we have one
        if (remoteUrl != null)
        {
            await RunGitAsync(dataDir, $"remote add origin {remoteUrl}");
            WriteStatus("Remote", remoteUrl);
        }

        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.Green;
        SystemConsole.WriteLine($"[+] Git sync initialized at: {dataDir}");
        SystemConsole.ResetColor();

        if (remoteUrl == null)
        {
            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("No remote configured. Options:");
            SystemConsole.WriteLine("  - Install GitHub CLI (gh) for automatic repo creation");
            SystemConsole.WriteLine("  - Or manually: koware sync init <git-url>");
            SystemConsole.ResetColor();
        }

        return 0;
    }

    private static async Task<bool> IsGhCliAvailableAsync()
    {
        try
        {
            var (code, output, _) = await RunCommandAsync("gh", "auth status");
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> CreateGitHubRepoAsync()
    {
        try
        {
            var repoName = "koware-sync";
            
            // Check if repo already exists
            var (checkCode, _, _) = await RunCommandAsync("gh", $"repo view {repoName}");
            if (checkCode == 0)
            {
                // Repo exists, get its URL
                var (_, urlOutput, _) = await RunCommandAsync("gh", $"repo view {repoName} --json sshUrl -q .sshUrl");
                if (!string.IsNullOrWhiteSpace(urlOutput))
                {
                    return urlOutput.Trim();
                }
            }
            
            // Create new private repo
            var (createCode, createOutput, createError) = await RunCommandAsync(
                "gh", 
                $"repo create {repoName} --private --description \"Koware sync data\" --clone=false"
            );
            
            if (createCode != 0)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                SystemConsole.WriteLine($"Could not create GitHub repo: {createError}");
                SystemConsole.ResetColor();
                return null;
            }
            
            // Get the SSH URL of the created repo
            var (_, sshUrl, _) = await RunCommandAsync("gh", $"repo view {repoName} --json sshUrl -q .sshUrl");
            return string.IsNullOrWhiteSpace(sshUrl) ? null : sshUrl.Trim();
        }
        catch (Exception ex)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine($"GitHub repo creation failed: {ex.Message}");
            SystemConsole.ResetColor();
            return null;
        }
    }

    private static async Task<(int exitCode, string output, string error)> RunCommandAsync(string command, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        
        try
        {
            process.Start();
            
            // Read stdout and stderr concurrently to avoid deadlocks
            // Using ReadToEndAsync is safer than event-based reading which can cause AccessViolationException
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();
            
            return (process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return (-1, "", ex.Message);
        }
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

        // Get current branch name
        var (_, currentBranch, _) = await RunGitAsync(dataDir, "branch --show-current");
        currentBranch = currentBranch.Trim();
        if (string.IsNullOrEmpty(currentBranch))
        {
            currentBranch = "main"; // fallback
        }

        // Push to remote
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine($"Pushing to {remote.Trim()}...");
        SystemConsole.ResetColor();

        var (pushCode, pushOutput, pushError) = await RunGitAsync(dataDir, $"push -u origin {currentBranch}");
        if (pushCode != 0)
        {
            // Try setting upstream if first push
            if (pushError.Contains("no upstream") || pushError.Contains("does not match any") || 
                pushError.Contains("failed to push"))
            {
                (pushCode, pushOutput, pushError) = await RunGitAsync(dataDir, $"push --set-upstream origin {currentBranch}");
            }
            
            if (pushCode != 0)
            {
                // Check if actually up-to-date (message can be in stdout)
                if (pushOutput.Contains("Everything up-to-date"))
                {
                    SystemConsole.WriteLine();
                    SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                    SystemConsole.WriteLine("Already up to date with remote.");
                    SystemConsole.ResetColor();
                    return 0;
                }
                
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

        var mergeEngine = new SyncMergeEngine(dataDir);

        // Check for existing unmerged files from a previous failed merge
        if (await mergeEngine.HasUnmergedFilesAsync())
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("Found unmerged files from previous sync. Attempting auto-resolve...");
            SystemConsole.ResetColor();

            var resolveResult = await mergeEngine.AutoResolveConflictsAsync(verbose: true);
            if (!resolveResult.Success)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Red;
                SystemConsole.WriteLine($"Could not auto-resolve: {resolveResult.Message}");
                SystemConsole.WriteLine("You can run 'koware sync resolve' to try again or 'koware sync abort' to cancel.");
                SystemConsole.ResetColor();
                return 1;
            }

            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine($"[+] Auto-resolved {resolveResult.FilesResolved} conflict(s)");
            SystemConsole.ResetColor();
            return 0;
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
                // Check if this is a merge conflict
                if (pullError.Contains("unmerged") || pullError.Contains("CONFLICT") || 
                    await mergeEngine.HasUnmergedFilesAsync())
                {
                    SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                    SystemConsole.WriteLine("Merge conflict detected. Attempting intelligent auto-merge...");
                    SystemConsole.ResetColor();

                    var resolveResult = await mergeEngine.AutoResolveConflictsAsync(verbose: true);
                    if (resolveResult.Success)
                    {
                        // Restore stashed changes after successful merge
                        if (hasLocalChanges)
                        {
                            WriteStatus("Local changes", "restoring...", ConsoleColor.Yellow);
                            await RunGitAsync(dataDir, "stash pop");
                        }

                        SystemConsole.WriteLine();
                        SystemConsole.ForegroundColor = ConsoleColor.Green;
                        SystemConsole.WriteLine($"[+] Pulled and auto-merged {resolveResult.FilesResolved} conflict(s)");
                        SystemConsole.ResetColor();
                        return 0;
                    }
                    else
                    {
                        SystemConsole.ForegroundColor = ConsoleColor.Red;
                        SystemConsole.WriteLine($"Auto-merge incomplete: {resolveResult.Message}");
                        SystemConsole.WriteLine("Run 'koware sync resolve' to retry or 'koware sync abort' to cancel.");
                        SystemConsole.ResetColor();
                        
                        if (hasLocalChanges)
                        {
                            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                            SystemConsole.WriteLine("Your local changes are still stashed.");
                            SystemConsole.ResetColor();
                        }
                        return 1;
                    }
                }

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

    private static async Task<int> QuickSyncAsync(CommandContext context)
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

        var mergeEngine = new SyncMergeEngine(dataDir);

        // Check for existing unmerged files first
        if (await mergeEngine.HasUnmergedFilesAsync())
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("Found unmerged files. Attempting auto-resolve first...");
            SystemConsole.ResetColor();

            var resolveResult = await mergeEngine.AutoResolveConflictsAsync(verbose: true);
            if (!resolveResult.Success)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Red;
                SystemConsole.WriteLine($"Could not auto-resolve: {resolveResult.Message}");
                SystemConsole.WriteLine("Run 'koware sync resolve' to retry or 'koware sync abort' to cancel.");
                SystemConsole.ResetColor();
                return 1;
            }
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine($"[+] Resolved {resolveResult.FilesResolved} conflict(s)");
            SystemConsole.ResetColor();
            SystemConsole.WriteLine();
        }

        // Check if remote exists
        var (_, remote, _) = await RunGitAsync(dataDir, "remote get-url origin");
        if (string.IsNullOrWhiteSpace(remote))
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("No remote configured. Run 'koware sync init <url>' first.");
            SystemConsole.ResetColor();
            return 1;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("╭──────────────────────────────────────╮");
        SystemConsole.WriteLine("│           Quick Sync                 │");
        SystemConsole.WriteLine("╰──────────────────────────────────────╯");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine();

        // Step 1: Pull first to get remote changes
        WriteStep(1, 3, "Pulling remote changes...");
        var (_, statusBefore, _) = await RunGitAsync(dataDir, "status --porcelain");
        var hasLocalChanges = !string.IsNullOrWhiteSpace(statusBefore);
        
        if (hasLocalChanges)
        {
            await RunGitAsync(dataDir, "stash");
        }

        var (pullCode, pullOutput, pullError) = await RunGitAsync(dataDir, "pull --rebase origin HEAD");
        if (pullCode != 0 && !pullError.Contains("Couldn't find remote ref"))
        {
            // Try without rebase
            (pullCode, pullOutput, pullError) = await RunGitAsync(dataDir, "pull origin HEAD");
        }
        
        // Check for merge conflicts after pull
        if (pullCode != 0 && (pullError.Contains("CONFLICT") || pullError.Contains("unmerged") || 
            await mergeEngine.HasUnmergedFilesAsync()))
        {
            WriteStepResult("!", "Merge conflict detected, auto-resolving...", ConsoleColor.Yellow);
            var resolveResult = await mergeEngine.AutoResolveConflictsAsync(verbose: false);
            if (resolveResult.Success)
            {
                WriteStepResult("✓", $"Auto-merged {resolveResult.FilesResolved} conflict(s)", ConsoleColor.Green);
                pullCode = 0; // Mark as successful
            }
            else
            {
                WriteStepResult("✗", "Could not auto-merge", ConsoleColor.Red);
                if (hasLocalChanges)
                {
                    await RunGitAsync(dataDir, "stash pop");
                }
                return 1;
            }
        }
        else if (pullCode == 0 || pullError.Contains("Couldn't find remote ref"))
        {
            WriteStepResult("✓", "Up to date with remote", ConsoleColor.Green);
        }
        else
        {
            WriteStepResult("!", "Pull had issues (continuing anyway)", ConsoleColor.Yellow);
        }

        if (hasLocalChanges)
        {
            await RunGitAsync(dataDir, "stash pop");
        }

        // Step 2: Stage and commit local changes
        WriteStep(2, 3, "Committing local changes...");
        await RunGitAsync(dataDir, "add -A");
        var (_, statusAfter, _) = await RunGitAsync(dataDir, "status --porcelain");
        
        if (!string.IsNullOrWhiteSpace(statusAfter))
        {
            var changeCount = statusAfter.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var message = $"Sync from {Environment.MachineName} at {DateTime.Now:g}";
            await RunGitAsync(dataDir, $"commit -m \"{message}\"");
            WriteStepResult("✓", $"Committed {changeCount} change(s)", ConsoleColor.Green);
        }
        else
        {
            WriteStepResult("○", "No local changes", ConsoleColor.DarkGray);
        }

        // Step 3: Push to remote
        WriteStep(3, 3, "Pushing to remote...");
        
        // Get current branch name
        var (_, currentBranch, _) = await RunGitAsync(dataDir, "branch --show-current");
        currentBranch = currentBranch.Trim();
        if (string.IsNullOrEmpty(currentBranch))
        {
            currentBranch = "main"; // fallback
        }
        
        // Try push with current branch
        var (pushCode, pushOutput, pushError) = await RunGitAsync(dataDir, $"push -u origin {currentBranch}");
        
        // Handle various push failure scenarios
        if (pushCode != 0)
        {
            // Try setting upstream if needed
            if (pushError.Contains("no upstream") || pushError.Contains("does not match any") || 
                pushError.Contains("failed to push"))
            {
                // Try with --set-upstream
                (pushCode, pushOutput, pushError) = await RunGitAsync(dataDir, $"push --set-upstream origin {currentBranch}");
            }
            
            // If still failing, try force pushing (for empty remotes or diverged branches)
            if (pushCode != 0 && (pushError.Contains("rejected") || pushError.Contains("non-fast-forward")))
            {
                // Ask if we should force push? For now, just report error
            }
        }

        var pushSucceeded = false;
        if (pushCode == 0)
        {
            WriteStepResult("✓", "Pushed successfully", ConsoleColor.Green);
            pushSucceeded = true;
        }
        else if (pushOutput.Contains("Everything up-to-date") || pushError.Contains("Everything up-to-date"))
        {
            WriteStepResult("○", "Already up to date", ConsoleColor.DarkGray);
            pushSucceeded = true;
        }
        else
        {
            WriteStepResult("✗", "Push failed", ConsoleColor.Red);
            var errorLine = !string.IsNullOrWhiteSpace(pushError) 
                ? pushError.Split('\n')[0] 
                : pushOutput.Split('\n').FirstOrDefault(l => l.Contains("error") || l.Contains("fatal")) ?? "Unknown error";
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine($"     {errorLine}");
            SystemConsole.ResetColor();
        }

        // Summary
        SystemConsole.WriteLine();
        if (pushSucceeded)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine("[+] Quick sync complete");
        }
        else
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.WriteLine("[!] Quick sync completed with push errors");
            SystemConsole.WriteLine("    Try 'koware sync push' for detailed error info");
        }
        SystemConsole.ResetColor();

        return pushSucceeded ? 0 : 1;
    }

    private static async Task<int> DiffAsync(CommandContext context)
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
        SystemConsole.WriteLine("Pending Changes");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine(new string('─', 50));
        SystemConsole.WriteLine();

        // Get status
        var (_, statusOutput, _) = await RunGitAsync(dataDir, "status --porcelain");
        
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("  No pending changes.");
            SystemConsole.ResetColor();
            return 0;
        }

        var lines = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var added = 0;
        var modified = 0;
        var deleted = 0;

        foreach (var line in lines)
        {
            if (line.Length < 3) continue;
            
            var status = line[0..2].Trim();
            var file = line[3..].Trim();
            
            var (icon, color, label) = status switch
            {
                "A" or "?" => ("+", ConsoleColor.Green, "added"),
                "M" => ("~", ConsoleColor.Yellow, "modified"),
                "D" => ("-", ConsoleColor.Red, "deleted"),
                "R" => ("→", ConsoleColor.Cyan, "renamed"),
                _ => ("?", ConsoleColor.Gray, "changed")
            };

            if (status is "A" or "?") added++;
            else if (status == "M") modified++;
            else if (status == "D") deleted++;

            SystemConsole.Write("  ");
            SystemConsole.ForegroundColor = color;
            SystemConsole.Write($"[{icon}] ");
            SystemConsole.ResetColor();
            SystemConsole.ForegroundColor = ConsoleColor.White;
            SystemConsole.Write(file);
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine($"  ({label})");
            SystemConsole.ResetColor();
        }

        // Summary
        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine(new string('─', 50));
        SystemConsole.ResetColor();
        
        SystemConsole.Write("  Summary: ");
        if (added > 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.Write($"+{added} ");
        }
        if (modified > 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Yellow;
            SystemConsole.Write($"~{modified} ");
        }
        if (deleted > 0)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.Write($"-{deleted} ");
        }
        SystemConsole.ResetColor();
        SystemConsole.WriteLine();
        
        SystemConsole.WriteLine();
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.WriteLine("  Run 'koware sync push' to commit and push these changes.");
        SystemConsole.WriteLine("  Run 'koware sync now' for quick sync (pull + commit + push).");
        SystemConsole.ResetColor();

        return 0;
    }

    private static void WriteStep(int step, int total, string message)
    {
        SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
        SystemConsole.Write($"  [{step}/{total}] ");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine(message);
    }

    private static void WriteStepResult(string icon, string message, ConsoleColor color)
    {
        SystemConsole.Write("       ");
        SystemConsole.ForegroundColor = color;
        SystemConsole.Write($"{icon} ");
        SystemConsole.ResetColor();
        SystemConsole.WriteLine(message);
    }

    private static async Task<int> ResolveConflictsAsync(CommandContext context)
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

        var mergeEngine = new SyncMergeEngine(dataDir);

        if (!await mergeEngine.HasUnmergedFilesAsync())
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("No merge conflicts to resolve.");
            SystemConsole.ResetColor();
            return 0;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Cyan;
        SystemConsole.WriteLine("Resolving merge conflicts...");
        SystemConsole.ResetColor();

        var result = await mergeEngine.AutoResolveConflictsAsync(verbose: true);

        if (result.Success)
        {
            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine($"[+] Successfully resolved {result.FilesResolved} conflict(s)");
            SystemConsole.ResetColor();
            return 0;
        }
        else
        {
            SystemConsole.WriteLine();
            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine($"Could not resolve all conflicts: {result.Message}");
            SystemConsole.WriteLine("You may need to manually resolve remaining conflicts.");
            SystemConsole.WriteLine("Run 'koware sync abort' to cancel the merge.");
            SystemConsole.ResetColor();
            return 1;
        }
    }

    private static async Task<int> AbortMergeAsync(CommandContext context)
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

        var mergeEngine = new SyncMergeEngine(dataDir);

        if (!mergeEngine.IsInMergeState() && !await mergeEngine.HasUnmergedFilesAsync())
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("No merge in progress to abort.");
            SystemConsole.ResetColor();
            return 0;
        }

        SystemConsole.ForegroundColor = ConsoleColor.Yellow;
        SystemConsole.WriteLine("Aborting merge...");
        SystemConsole.ResetColor();

        var success = await mergeEngine.AbortMergeAsync();

        if (success)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Green;
            SystemConsole.WriteLine("[+] Merge aborted. Repository restored to previous state.");
            SystemConsole.ResetColor();
            return 0;
        }
        else
        {
            // Try git reset as fallback
            var (resetCode, _, _) = await RunGitAsync(dataDir, "reset --hard HEAD");
            if (resetCode == 0)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Green;
                SystemConsole.WriteLine("[+] Reset to HEAD. Any uncommitted changes were discarded.");
                SystemConsole.ResetColor();
                return 0;
            }

            SystemConsole.ForegroundColor = ConsoleColor.Red;
            SystemConsole.WriteLine("Failed to abort merge. Try manually running: git merge --abort");
            SystemConsole.ResetColor();
            return 1;
        }
    }

    private static async Task<int> AutoSyncAsync(string[] args, CommandContext context)
    {
        var dataDir = GetDataDirectory();
        var configPath = Path.Combine(dataDir, "sync.config");
        
        // Parse action: on, off, status
        var action = args.Length > 2 ? args[2].ToLowerInvariant() : "status";
        
        switch (action)
        {
            case "on" or "enable":
            {
                // Check if git is configured with remote
                var engine = new SyncEngine();
                if (!engine.IsGitConfigured())
                {
                    SystemConsole.ForegroundColor = ConsoleColor.Red;
                    SystemConsole.WriteLine("Auto-sync requires git with a remote configured.");
                    SystemConsole.WriteLine("Run 'koware sync init' first.");
                    SystemConsole.ResetColor();
                    engine.Dispose();
                    return 1;
                }
                engine.Dispose();
                
                await File.WriteAllTextAsync(configPath, "enabled", context.CancellationToken);
                SystemConsole.ForegroundColor = ConsoleColor.Green;
                SystemConsole.WriteLine("[+] Auto-sync enabled");
                SystemConsole.ResetColor();
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine("Changes will be automatically synced when you watch/read content.");
                SystemConsole.ResetColor();
                return 0;
            }
            
            case "off" or "disable":
            {
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
                SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                SystemConsole.WriteLine("[ ] Auto-sync disabled");
                SystemConsole.ResetColor();
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine("Use 'koware sync push' to manually sync changes.");
                SystemConsole.ResetColor();
                return 0;
            }
            
            case "status":
            default:
            {
                var enabled = File.Exists(configPath) && 
                              (await File.ReadAllTextAsync(configPath, context.CancellationToken)).Trim() == "enabled";
                
                var engine = new SyncEngine();
                var configured = engine.IsGitConfigured();
                engine.Dispose();
                
                SystemConsole.ForegroundColor = ConsoleColor.Cyan;
                SystemConsole.WriteLine("Auto-Sync Status");
                SystemConsole.ResetColor();
                SystemConsole.WriteLine(new string('─', 30));
                
                WriteField("Enabled", enabled ? "yes" : "no", enabled ? ConsoleColor.Green : ConsoleColor.Yellow);
                WriteField("Git configured", configured ? "yes" : "no", configured ? ConsoleColor.Green : ConsoleColor.Red);
                
                if (enabled && !configured)
                {
                    SystemConsole.WriteLine();
                    SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                    SystemConsole.WriteLine("Warning: Auto-sync is enabled but git remote is not configured.");
                    SystemConsole.WriteLine("Run 'koware sync init' to configure.");
                    SystemConsole.ResetColor();
                }
                
                SystemConsole.WriteLine();
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine("Commands:");
                SystemConsole.WriteLine("  koware sync auto on     Enable auto-sync");
                SystemConsole.WriteLine("  koware sync auto off    Disable auto-sync");
                SystemConsole.ResetColor();
                
                return 0;
            }
        }
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
        SystemConsole.WriteLine("  now / quick         Quick sync: pull → commit → push (recommended)");
        SystemConsole.WriteLine("  diff                Show pending changes before syncing");
        SystemConsole.WriteLine("  push [message]      Commit and push changes to remote");
        SystemConsole.WriteLine("  pull                Pull changes from remote (auto-merges conflicts)");
        SystemConsole.WriteLine("  log                 Show sync history");
        SystemConsole.WriteLine("  clone <url>         Clone from existing sync repository");
        SystemConsole.WriteLine("  auto [on|off]       Enable/disable automatic background sync");
        SystemConsole.WriteLine("  resolve             Auto-resolve merge conflicts");
        SystemConsole.WriteLine("  abort               Abort current merge and restore previous state");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Setup (new sync):");
        SystemConsole.WriteLine("  1. koware sync init     # Auto-creates GitHub repo if gh CLI installed");
        SystemConsole.WriteLine("  2. koware sync push");
        SystemConsole.WriteLine("  3. koware sync auto on  # Enable automatic syncing");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Setup (existing sync):");
        SystemConsole.WriteLine("  koware sync clone git@github.com:user/koware-sync.git");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("Daily use:");
        SystemConsole.WriteLine("  With auto-sync ON:  Changes sync automatically");
        SystemConsole.WriteLine("  With auto-sync OFF: koware sync push / pull manually");
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
        
        // Linux/macOS: respect XDG_CONFIG_HOME
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(configHome, "koware");
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
        
        try
        {
            process.Start();
            
            // Read stdout and stderr concurrently to avoid deadlocks
            // Using ReadToEndAsync is safer than event-based reading which can cause AccessViolationException
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            
            // Wait for both streams to complete
            await Task.WhenAll(outputTask, errorTask);
            
            // Wait for process to exit
            await process.WaitForExitAsync();
            
            return (process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Process may have exited prematurely
            return (-1, "", ex.Message);
        }
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
