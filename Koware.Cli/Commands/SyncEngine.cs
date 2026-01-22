// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using System.Text;
using SystemConsole = System.Console;

namespace Koware.Cli.Commands;

/// <summary>
/// Intelligent background sync engine that automatically detects changes
/// and syncs data to the configured git remote.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly string _dataDir;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Timers.Timer _debounceTimer;
    private readonly object _syncLock = new();
    private bool _pendingSync;
    private bool _disposed;
    private bool _enabled;
    
    /// <summary>
    /// Debounce delay in milliseconds. Changes within this window are batched.
    /// </summary>
    public int DebounceDelayMs { get; set; } = 5000; // 5 seconds default
    
    /// <summary>
    /// Whether to show sync status messages.
    /// </summary>
    public bool Verbose { get; set; }
    
    /// <summary>
    /// Event raised when a sync operation completes.
    /// </summary>
    public event EventHandler<SyncEventArgs>? SyncCompleted;

    public SyncEngine()
    {
        _dataDir = GetDataDirectory();
        
        _watcher = new FileSystemWatcher(_dataDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        
        _debounceTimer = new System.Timers.Timer(DebounceDelayMs);
        _debounceTimer.Elapsed += async (_, _) => await ExecuteSyncAsync();
        _debounceTimer.AutoReset = false;
    }

    /// <summary>
    /// Start watching for changes and auto-syncing.
    /// </summary>
    public bool Start()
    {
        if (_disposed) return false;
        
        // Check if git is initialized and remote is configured
        if (!IsGitConfigured())
        {
            return false;
        }
        
        _enabled = true;
        _watcher.EnableRaisingEvents = true;
        
        if (Verbose)
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("[sync] Auto-sync enabled, watching for changes...");
            SystemConsole.ResetColor();
        }
        
        return true;
    }

    /// <summary>
    /// Stop watching for changes.
    /// </summary>
    public void Stop()
    {
        _enabled = false;
        _watcher.EnableRaisingEvents = false;
        _debounceTimer.Stop();
        
        if (Verbose)
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine("[sync] Auto-sync disabled");
            SystemConsole.ResetColor();
        }
    }

    /// <summary>
    /// Check if sync engine can run (git initialized with remote).
    /// </summary>
    public bool IsGitConfigured()
    {
        var gitDir = Path.Combine(_dataDir, ".git");
        if (!Directory.Exists(gitDir))
        {
            return false;
        }
        
        // Check for remote
        var (code, output, _) = RunGitSync("remote get-url origin");
        return code == 0 && !string.IsNullOrWhiteSpace(output);
    }

    /// <summary>
    /// Force an immediate sync.
    /// </summary>
    public async Task<SyncResult> ForceSyncAsync()
    {
        return await ExecuteSyncAsync(force: true);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore .git directory changes
        if (e.FullPath.Contains(".git")) return;
        
        // Ignore temporary/journal files
        if (e.Name?.EndsWith("-journal") == true ||
            e.Name?.EndsWith("-wal") == true ||
            e.Name?.EndsWith("-shm") == true ||
            e.Name?.EndsWith(".tmp") == true ||
            e.Name?.EndsWith(".bak") == true)
        {
            return;
        }

        lock (_syncLock)
        {
            _pendingSync = true;
            
            // Reset debounce timer
            _debounceTimer.Stop();
            _debounceTimer.Interval = DebounceDelayMs;
            _debounceTimer.Start();
        }
        
        if (Verbose)
        {
            SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
            SystemConsole.WriteLine($"[sync] Change detected: {e.Name}, syncing in {DebounceDelayMs/1000}s...");
            SystemConsole.ResetColor();
        }
    }

    private async Task<SyncResult> ExecuteSyncAsync(bool force = false)
    {
        _debounceTimer.Stop();
        
        lock (_syncLock)
        {
            if (!force && !_pendingSync)
            {
                return new SyncResult { Success = true, Message = "No changes to sync" };
            }
            _pendingSync = false;
        }

        if (!_enabled && !force)
        {
            return new SyncResult { Success = false, Message = "Sync engine not enabled" };
        }

        try
        {
            // Stage all changes
            var (addCode, _, addError) = await RunGitAsync("add -A");
            if (addCode != 0)
            {
                return new SyncResult { Success = false, Message = $"Failed to stage: {addError}" };
            }

            // Check if there are changes to commit
            var (_, statusOutput, _) = await RunGitAsync("status --porcelain");
            if (string.IsNullOrWhiteSpace(statusOutput))
            {
                // No local changes, but try to push any unpushed commits
                var (pushCode, _, pushError) = await RunGitAsync("push -u origin HEAD");
                if (pushCode != 0 && !pushError.Contains("Everything up-to-date"))
                {
                    return new SyncResult { Success = false, Message = $"Push failed: {pushError}" };
                }
                return new SyncResult { Success = true, Message = "Already in sync" };
            }

            // Commit with auto-generated message
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var message = $"Auto-sync from {Environment.MachineName} at {timestamp}";
            var (commitCode, _, commitError) = await RunGitAsync($"commit -m \"{message}\"");
            
            if (commitCode != 0 && !commitError.Contains("nothing to commit"))
            {
                return new SyncResult { Success = false, Message = $"Commit failed: {commitError}" };
            }

            // Push to remote
            var (pushResultCode, _, pushResultError) = await RunGitAsync("push -u origin HEAD");
            if (pushResultCode != 0)
            {
                // Try to pull and rebase first if push fails
                var (pullCode, _, _) = await RunGitAsync("pull --rebase origin HEAD");
                if (pullCode == 0)
                {
                    (pushResultCode, _, pushResultError) = await RunGitAsync("push -u origin HEAD");
                }
            }

            if (pushResultCode != 0)
            {
                return new SyncResult { Success = false, Message = $"Push failed: {pushResultError}" };
            }

            var result = new SyncResult { Success = true, Message = "Synced successfully" };
            
            if (Verbose)
            {
                SystemConsole.ForegroundColor = ConsoleColor.Green;
                SystemConsole.WriteLine($"[sync] {result.Message}");
                SystemConsole.ResetColor();
            }
            
            SyncCompleted?.Invoke(this, new SyncEventArgs(result));
            return result;
        }
        catch (Exception ex)
        {
            var result = new SyncResult { Success = false, Message = ex.Message };
            SyncCompleted?.Invoke(this, new SyncEventArgs(result));
            return result;
        }
    }

    private async Task<(int exitCode, string output, string error)> RunGitAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _dataDir,
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

    private (int exitCode, string output, string error) RunGitSync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _dataDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output.Trim(), error.Trim());
    }

    private static string GetDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "koware");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "koware");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounceTimer.Stop();
        _debounceTimer.Dispose();
    }
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Event args for sync completion.
/// </summary>
public sealed class SyncEventArgs : EventArgs
{
    public SyncResult Result { get; }
    
    public SyncEventArgs(SyncResult result)
    {
        Result = result;
    }
}
