// Author: Ilgaz Mehmetoğlu
// Comprehensive diagnostics engine for the "koware doctor" command.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Koware.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Koware.Cli.Health;

/// <summary>
/// Severity level for a diagnostic check result.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Check passed successfully.</summary>
    Ok,
    /// <summary>Check passed with warnings (non-critical issue).</summary>
    Warning,
    /// <summary>Check failed (critical issue).</summary>
    Error,
    /// <summary>Check was skipped (not applicable or dependency failed).</summary>
    Skipped,
    /// <summary>Informational result (no pass/fail).</summary>
    Info
}

/// <summary>
/// Category of diagnostic check.
/// </summary>
public enum DiagnosticCategory
{
    Environment,
    Configuration,
    Storage,
    Network,
    Providers,
    Toolchain,
    Data,
    Security,
    Terminal,
    Updates,
    Engine
}

/// <summary>
/// Result of a single diagnostic check.
/// </summary>
public sealed record DiagnosticResult
{
    public required string Name { get; init; }
    public required DiagnosticCategory Category { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public string? Message { get; init; }
    public string? Detail { get; init; }
    public TimeSpan? Duration { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public bool IsOk => Severity == DiagnosticSeverity.Ok || Severity == DiagnosticSeverity.Info;
    public bool IsCritical => Severity == DiagnosticSeverity.Error;
}

/// <summary>
/// Comprehensive diagnostics engine that runs all health checks.
/// </summary>
public sealed class DiagnosticsEngine
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<AllAnimeOptions> _animeOptions;
    private readonly IOptions<AllMangaOptions> _mangaOptions;
    private readonly string _configPath;
    private readonly string _dataDir;
    private readonly string _dbPath;

    public DiagnosticsEngine(
        HttpClient httpClient,
        IOptions<AllAnimeOptions> animeOptions,
        IOptions<AllMangaOptions> mangaOptions,
        string configPath)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _animeOptions = animeOptions;
        _mangaOptions = mangaOptions;
        _configPath = configPath;

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        _dataDir = Path.Combine(baseDir, "koware");
        _dbPath = Path.Combine(_dataDir, "history.db");
    }

    /// <summary>
    /// Run all diagnostic checks.
    /// </summary>
    public async Task<IReadOnlyList<DiagnosticResult>> RunAllAsync(CancellationToken cancellationToken = default)
    {
        return await RunAllWithProgressAsync(null, cancellationToken);
    }

    /// <summary>
    /// Run all diagnostic checks with progress reporting.
    /// </summary>
    /// <param name="progress">Progress reporter that receives (current, total, categoryName).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<DiagnosticResult>> RunAllWithProgressAsync(
        IProgress<(int current, int total, string category)>? progress,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DiagnosticResult>();
        const int totalCategories = 11;
        var currentCategory = 0;

        // Environment checks
        progress?.Report((++currentCategory, totalCategories, "Environment"));
        results.AddRange(RunEnvironmentChecks());

        // Terminal checks
        progress?.Report((++currentCategory, totalCategories, "Terminal"));
        results.AddRange(RunTerminalChecks());

        // Configuration checks
        progress?.Report((++currentCategory, totalCategories, "Configuration"));
        results.AddRange(RunConfigurationChecks());

        // Storage checks
        progress?.Report((++currentCategory, totalCategories, "Storage"));
        results.AddRange(await RunStorageChecksAsync(cancellationToken));

        // Data integrity checks
        progress?.Report((++currentCategory, totalCategories, "Data Integrity"));
        results.AddRange(await RunDataIntegrityChecksAsync(cancellationToken));

        // Network checks
        progress?.Report((++currentCategory, totalCategories, "Network"));
        results.AddRange(await RunNetworkChecksAsync(cancellationToken));

        // Security checks
        progress?.Report((++currentCategory, totalCategories, "Security"));
        results.AddRange(await RunSecurityChecksAsync(cancellationToken));

        // Provider checks
        progress?.Report((++currentCategory, totalCategories, "Providers"));
        results.AddRange(await RunProviderChecksAsync(cancellationToken));

        // Toolchain checks
        progress?.Report((++currentCategory, totalCategories, "Toolchain"));
        results.AddRange(RunToolchainChecks());

        // Update checks
        progress?.Report((++currentCategory, totalCategories, "Updates"));
        results.AddRange(await RunUpdateChecksAsync(cancellationToken));

        // Engine checks (core functionality)
        progress?.Report((++currentCategory, totalCategories, "Engine"));
        results.AddRange(await RunEngineChecksAsync(cancellationToken));

        return results;
    }

    /// <summary>
    /// Run only a specific category of checks.
    /// </summary>
    public async Task<IReadOnlyList<DiagnosticResult>> RunCategoryAsync(DiagnosticCategory category, CancellationToken cancellationToken = default)
    {
        return category switch
        {
            DiagnosticCategory.Environment => RunEnvironmentChecks(),
            DiagnosticCategory.Terminal => RunTerminalChecks(),
            DiagnosticCategory.Configuration => RunConfigurationChecks(),
            DiagnosticCategory.Storage => await RunStorageChecksAsync(cancellationToken),
            DiagnosticCategory.Data => await RunDataIntegrityChecksAsync(cancellationToken),
            DiagnosticCategory.Network => await RunNetworkChecksAsync(cancellationToken),
            DiagnosticCategory.Security => await RunSecurityChecksAsync(cancellationToken),
            DiagnosticCategory.Providers => await RunProviderChecksAsync(cancellationToken),
            DiagnosticCategory.Toolchain => RunToolchainChecks(),
            DiagnosticCategory.Updates => await RunUpdateChecksAsync(cancellationToken),
            DiagnosticCategory.Engine => await RunEngineChecksAsync(cancellationToken),
            _ => Array.Empty<DiagnosticResult>()
        };
    }

    #region Environment Checks

    private List<DiagnosticResult> RunEnvironmentChecks()
    {
        var results = new List<DiagnosticResult>();

        // OS Information
        results.Add(new DiagnosticResult
        {
            Name = "Operating System",
            Category = DiagnosticCategory.Environment,
            Severity = DiagnosticSeverity.Info,
            Message = RuntimeInformation.OSDescription,
            Detail = $"{RuntimeInformation.OSArchitecture}"
        });

        // .NET Runtime
        results.Add(new DiagnosticResult
        {
            Name = ".NET Runtime",
            Category = DiagnosticCategory.Environment,
            Severity = DiagnosticSeverity.Info,
            Message = RuntimeInformation.FrameworkDescription,
            Detail = Environment.Version.ToString()
        });

        // CLI Version
        var version = GetVersionLabel();
        results.Add(new DiagnosticResult
        {
            Name = "Koware Version",
            Category = DiagnosticCategory.Environment,
            Severity = DiagnosticSeverity.Info,
            Message = version
        });

        // CLI Path
        var cliPath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        var cliPathValid = !string.IsNullOrWhiteSpace(cliPath) && File.Exists(cliPath);
        results.Add(new DiagnosticResult
        {
            Name = "CLI Path",
            Category = DiagnosticCategory.Environment,
            Severity = cliPathValid ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
            Message = cliPath ?? "(unknown)",
            Detail = cliPathValid ? null : "Could not determine CLI location"
        });

        // Disk Space
        try
        {
            var dataPath = _dataDir;
            if (!Directory.Exists(dataPath))
            {
                dataPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            var driveInfo = new DriveInfo(Path.GetPathRoot(dataPath) ?? dataPath);
            var freeGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var totalGb = driveInfo.TotalSize / (1024.0 * 1024 * 1024);
            var severity = freeGb < 1 ? DiagnosticSeverity.Error : freeGb < 5 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;
            results.Add(new DiagnosticResult
            {
                Name = "Disk Space",
                Category = DiagnosticCategory.Environment,
                Severity = severity,
                Message = $"{freeGb:F1} GB free of {totalGb:F1} GB",
                Detail = severity == DiagnosticSeverity.Error ? "Low disk space may cause issues" : null
            });
        }
        catch (Exception ex)
        {
            results.Add(new DiagnosticResult
            {
                Name = "Disk Space",
                Category = DiagnosticCategory.Environment,
                Severity = DiagnosticSeverity.Warning,
                Message = "Could not determine disk space",
                Detail = ex.Message
            });
        }

        // Memory
        try
        {
            var process = Process.GetCurrentProcess();
            var memoryMb = process.WorkingSet64 / (1024.0 * 1024);
            results.Add(new DiagnosticResult
            {
                Name = "Memory Usage",
                Category = DiagnosticCategory.Environment,
                Severity = DiagnosticSeverity.Info,
                Message = $"{memoryMb:F1} MB"
            });
        }
        catch
        {
            // Ignore memory check failures
        }

        // Terminal capabilities
        results.AddRange(RunTerminalCapabilityChecks());

        // Processor info
        results.Add(new DiagnosticResult
        {
            Name = "Processor",
            Category = DiagnosticCategory.Environment,
            Severity = DiagnosticSeverity.Info,
            Message = $"{Environment.ProcessorCount} cores",
            Detail = RuntimeInformation.ProcessArchitecture.ToString()
        });

        // 64-bit check
        results.Add(new DiagnosticResult
        {
            Name = "64-bit Process",
            Category = DiagnosticCategory.Environment,
            Severity = Environment.Is64BitProcess ? DiagnosticSeverity.Ok : DiagnosticSeverity.Info,
            Message = Environment.Is64BitProcess ? "Yes" : "No (32-bit)"
        });

        return results;
    }

    private static List<DiagnosticResult> RunTerminalCapabilityChecks()
    {
        var results = new List<DiagnosticResult>();

        // Check if running in interactive terminal
        var isInteractive = !System.Console.IsInputRedirected && !System.Console.IsOutputRedirected;
        results.Add(new DiagnosticResult
        {
            Name = "Interactive Terminal",
            Category = DiagnosticCategory.Environment,
            Severity = isInteractive ? DiagnosticSeverity.Ok : DiagnosticSeverity.Info,
            Message = isInteractive ? "Yes" : "Redirected I/O"
        });

        // Terminal size
        try
        {
            if (isInteractive)
            {
                var width = System.Console.WindowWidth;
                var height = System.Console.WindowHeight;
                results.Add(new DiagnosticResult
                {
                    Name = "Terminal Size",
                    Category = DiagnosticCategory.Environment,
                    Severity = width >= 80 ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
                    Message = $"{width}x{height}",
                    Detail = width < 80 ? "Narrow terminal may affect display" : null
                });
            }
        }
        catch
        {
            // Terminal size not available
        }

        return results;
    }

    #endregion

    #region Terminal Checks

    private List<DiagnosticResult> RunTerminalChecks()
    {
        var results = new List<DiagnosticResult>();

        // Color support detection
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        var term = Environment.GetEnvironmentVariable("TERM");
        var supportsColor = !string.IsNullOrEmpty(colorTerm) || 
                           (term?.Contains("color", StringComparison.OrdinalIgnoreCase) ?? false) ||
                           (term?.Contains("xterm", StringComparison.OrdinalIgnoreCase) ?? false) ||
                           RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        results.Add(new DiagnosticResult
        {
            Name = "Color Support",
            Category = DiagnosticCategory.Terminal,
            Severity = supportsColor ? DiagnosticSeverity.Ok : DiagnosticSeverity.Info,
            Message = supportsColor ? "Supported" : "Limited",
            Detail = !string.IsNullOrEmpty(colorTerm) ? colorTerm : term
        });

        // Unicode support detection
        var unicodeSupport = CheckUnicodeSupport();
        results.Add(new DiagnosticResult
        {
            Name = "Unicode Support",
            Category = DiagnosticCategory.Terminal,
            Severity = unicodeSupport ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
            Message = unicodeSupport ? "Supported" : "Limited",
            Detail = unicodeSupport ? null : "Some characters may not display correctly"
        });

        // Shell detection
        var shell = DetectShell();
        results.Add(new DiagnosticResult
        {
            Name = "Shell",
            Category = DiagnosticCategory.Terminal,
            Severity = DiagnosticSeverity.Info,
            Message = shell
        });

        // Terminal emulator
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? 
                         Environment.GetEnvironmentVariable("TERMINAL_EMULATOR");
        if (!string.IsNullOrEmpty(termProgram))
        {
            results.Add(new DiagnosticResult
            {
                Name = "Terminal Emulator",
                Category = DiagnosticCategory.Terminal,
                Severity = DiagnosticSeverity.Info,
                Message = termProgram
            });
        }

        // Check encoding
        try
        {
            var outputEncoding = System.Console.OutputEncoding.WebName;
            var isUtf8 = outputEncoding.Contains("utf-8", StringComparison.OrdinalIgnoreCase) ||
                        outputEncoding.Contains("utf8", StringComparison.OrdinalIgnoreCase);
            results.Add(new DiagnosticResult
            {
                Name = "Output Encoding",
                Category = DiagnosticCategory.Terminal,
                Severity = isUtf8 ? DiagnosticSeverity.Ok : DiagnosticSeverity.Info,
                Message = outputEncoding,
                Detail = isUtf8 ? null : "Non-UTF8 encoding may affect character display"
            });
        }
        catch
        {
            // Encoding detection not available
        }

        return results;
    }

    private static bool CheckUnicodeSupport()
    {
        // Check various indicators of Unicode support
        var lang = Environment.GetEnvironmentVariable("LANG") ?? string.Empty;
        var lcAll = Environment.GetEnvironmentVariable("LC_ALL") ?? string.Empty;
        
        if (lang.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ||
            lang.Contains("UTF8", StringComparison.OrdinalIgnoreCase) ||
            lcAll.Contains("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Windows Terminal and modern Windows consoles support Unicode
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wtSession = Environment.GetEnvironmentVariable("WT_SESSION");
            return !string.IsNullOrEmpty(wtSession);
        }

        return false;
    }

    private static string DetectShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var psVersion = Environment.GetEnvironmentVariable("PSVersionTable");
            if (!string.IsNullOrEmpty(psVersion) || 
                Environment.GetEnvironmentVariable("PSModulePath") != null)
            {
                return "PowerShell";
            }
            return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shell))
        {
            return Path.GetFileName(shell);
        }

        return "Unknown";
    }

    #endregion

    #region Configuration Checks

    private List<DiagnosticResult> RunConfigurationChecks()
    {
        var results = new List<DiagnosticResult>();

        // Config directory
        var configDir = Path.GetDirectoryName(_configPath);
        var configDirExists = !string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir);
        results.Add(new DiagnosticResult
        {
            Name = "Config Directory",
            Category = DiagnosticCategory.Configuration,
            Severity = configDirExists ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
            Message = configDirExists ? configDir! : "Not created yet",
            Detail = configDirExists ? null : "Will be created on first config change"
        });

        // Config file
        var configExists = File.Exists(_configPath);
        if (configExists)
        {
            // Validate JSON
            try
            {
                var content = File.ReadAllText(_configPath);
                JsonDocument.Parse(content);
                results.Add(new DiagnosticResult
                {
                    Name = "Config File",
                    Category = DiagnosticCategory.Configuration,
                    Severity = DiagnosticSeverity.Ok,
                    Message = _configPath,
                    Detail = $"{new FileInfo(_configPath).Length} bytes"
                });
            }
            catch (JsonException ex)
            {
                results.Add(new DiagnosticResult
                {
                    Name = "Config File",
                    Category = DiagnosticCategory.Configuration,
                    Severity = DiagnosticSeverity.Error,
                    Message = "Invalid JSON",
                    Detail = ex.Message
                });
            }
        }
        else
        {
            results.Add(new DiagnosticResult
            {
                Name = "Config File",
                Category = DiagnosticCategory.Configuration,
                Severity = DiagnosticSeverity.Info,
                Message = "Not created yet",
                Detail = "Default settings in use"
            });
        }

        // Provider configuration validation
        var animeOpts = _animeOptions.Value;
        var mangaOpts = _mangaOptions.Value;

        results.Add(new DiagnosticResult
        {
            Name = "Anime Provider Config",
            Category = DiagnosticCategory.Configuration,
            Severity = animeOpts.IsConfigured ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
            Message = animeOpts.IsConfigured ? "Configured" : "Not configured",
            Detail = animeOpts.IsConfigured 
                ? $"API: {animeOpts.ApiBase}" 
                : "Run 'koware provider' to configure"
        });

        results.Add(new DiagnosticResult
        {
            Name = "Manga Provider Config",
            Category = DiagnosticCategory.Configuration,
            Severity = mangaOpts.IsConfigured ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
            Message = mangaOpts.IsConfigured ? "Configured" : "Not configured",
            Detail = mangaOpts.IsConfigured 
                ? $"API: {mangaOpts.ApiBase}" 
                : "Run 'koware provider' to configure"
        });

        return results;
    }

    #endregion

    #region Storage Checks

    private async Task<List<DiagnosticResult>> RunStorageChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        // Data directory
        var dataDirExists = Directory.Exists(_dataDir);
        results.Add(new DiagnosticResult
        {
            Name = "Data Directory",
            Category = DiagnosticCategory.Storage,
            Severity = dataDirExists ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
            Message = _dataDir,
            Detail = dataDirExists ? null : "Will be created on first use"
        });

        if (!dataDirExists)
        {
            return results;
        }

        // Write permission test
        var canWrite = await TestWritePermissionAsync(_dataDir);
        results.Add(new DiagnosticResult
        {
            Name = "Write Permission",
            Category = DiagnosticCategory.Storage,
            Severity = canWrite ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
            Message = canWrite ? "Writable" : "No write access",
            Detail = canWrite ? null : "Cannot write to data directory"
        });

        // SQLite database
        if (File.Exists(_dbPath))
        {
            var dbResult = await CheckDatabaseHealthAsync(cancellationToken);
            results.Add(dbResult);

            // Database size
            try
            {
                var dbSize = new FileInfo(_dbPath).Length;
                var sizeKb = dbSize / 1024.0;
                var sizeMb = sizeKb / 1024.0;
                var sizeStr = sizeMb >= 1 ? $"{sizeMb:F2} MB" : $"{sizeKb:F1} KB";
                
                var severity = sizeMb > 500 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info;
                results.Add(new DiagnosticResult
                {
                    Name = "Database Size",
                    Category = DiagnosticCategory.Storage,
                    Severity = severity,
                    Message = sizeStr,
                    Detail = severity == DiagnosticSeverity.Warning ? "Consider running cleanup" : null
                });
            }
            catch
            {
                // Ignore size check failure
            }

            // Table statistics
            var tableStats = await GetTableStatsAsync(cancellationToken);
            if (tableStats.Count > 0)
            {
                results.Add(new DiagnosticResult
                {
                    Name = "Database Tables",
                    Category = DiagnosticCategory.Storage,
                    Severity = DiagnosticSeverity.Info,
                    Message = $"{tableStats.Count} tables",
                    Metadata = tableStats
                });
            }
        }
        else
        {
            results.Add(new DiagnosticResult
            {
                Name = "Database",
                Category = DiagnosticCategory.Storage,
                Severity = DiagnosticSeverity.Info,
                Message = "Not created yet",
                Detail = "Will be created on first use"
            });
        }

        // Calculate total storage used
        try
        {
            var totalSize = Directory.GetFiles(_dataDir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            var sizeMb = totalSize / (1024.0 * 1024);
            results.Add(new DiagnosticResult
            {
                Name = "Total Storage",
                Category = DiagnosticCategory.Storage,
                Severity = DiagnosticSeverity.Info,
                Message = sizeMb >= 1 ? $"{sizeMb:F2} MB" : $"{totalSize / 1024.0:F1} KB"
            });
        }
        catch
        {
            // Ignore total size calculation failure
        }

        return results;
    }

    private static async Task<bool> TestWritePermissionAsync(string directory)
    {
        var testFile = Path.Combine(directory, $".koware_write_test_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<DiagnosticResult> CheckDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var connectionString = $"Data Source={_dbPath};Cache=Shared";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Run integrity check
            await using var cmd = new SqliteCommand("PRAGMA integrity_check;", connection);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            sw.Stop();

            var isOk = result?.ToString() == "ok";
            return new DiagnosticResult
            {
                Name = "Database Health",
                Category = DiagnosticCategory.Storage,
                Severity = isOk ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = isOk ? "Healthy" : "Integrity check failed",
                Detail = isOk ? null : result?.ToString(),
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = "Database Health",
                Category = DiagnosticCategory.Storage,
                Severity = DiagnosticSeverity.Error,
                Message = "Cannot open database",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private async Task<Dictionary<string, string>> GetTableStatsAsync(CancellationToken cancellationToken)
    {
        var stats = new Dictionary<string, string>();
        try
        {
            var connectionString = $"Data Source={_dbPath};Cache=Shared";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Get all tables
            await using var tablesCmd = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';", 
                connection);
            await using var reader = await tablesCmd.ExecuteReaderAsync(cancellationToken);
            var tables = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }

            // Get row counts
            foreach (var table in tables)
            {
                try
                {
                    await using var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM [{table}];", connection);
                    var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
                    stats[table] = $"{count} rows";
                }
                catch
                {
                    stats[table] = "error";
                }
            }
        }
        catch
        {
            // Ignore table stats failures
        }

        return stats;
    }

    #endregion

    #region Data Integrity Checks

    private async Task<List<DiagnosticResult>> RunDataIntegrityChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        if (!File.Exists(_dbPath))
        {
            results.Add(new DiagnosticResult
            {
                Name = "Data Integrity",
                Category = DiagnosticCategory.Data,
                Severity = DiagnosticSeverity.Skipped,
                Message = "No database yet"
            });
            return results;
        }

        try
        {
            var connectionString = $"Data Source={_dbPath};Cache=Shared";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Check watch history integrity
            var watchHistoryResult = await CheckTableIntegrityAsync(connection, "watch_history", cancellationToken);
            if (watchHistoryResult != null) results.Add(watchHistoryResult);

            // Check anime list integrity
            var animeListResult = await CheckTableIntegrityAsync(connection, "anime_list", cancellationToken);
            if (animeListResult != null) results.Add(animeListResult);

            // Check downloads integrity and orphaned entries
            var downloadsResult = await CheckDownloadsIntegrityAsync(connection, cancellationToken);
            if (downloadsResult != null) results.Add(downloadsResult);

            // Check read history integrity
            var readHistoryResult = await CheckTableIntegrityAsync(connection, "read_history", cancellationToken);
            if (readHistoryResult != null) results.Add(readHistoryResult);

            // Check manga list integrity
            var mangaListResult = await CheckTableIntegrityAsync(connection, "manga_list", cancellationToken);
            if (mangaListResult != null) results.Add(mangaListResult);

            // Check for duplicate entries
            var duplicatesResult = await CheckDuplicateEntriesAsync(connection, cancellationToken);
            results.Add(duplicatesResult);

            // Check for old/stale data
            var staleDataResult = await CheckStaleDataAsync(connection, cancellationToken);
            results.Add(staleDataResult);
        }
        catch (Exception ex)
        {
            results.Add(new DiagnosticResult
            {
                Name = "Data Integrity",
                Category = DiagnosticCategory.Data,
                Severity = DiagnosticSeverity.Error,
                Message = "Check failed",
                Detail = ex.Message
            });
        }

        return results;
    }

    private async Task<DiagnosticResult?> CheckTableIntegrityAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        try
        {
            // Check if table exists
            await using var existsCmd = new SqliteCommand(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}';",
                connection);
            var exists = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(cancellationToken)) > 0;

            if (!exists)
            {
                return null; // Table doesn't exist yet, skip
            }

            // Get row count
            await using var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM [{tableName}];", connection);
            var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));

            // Check for NULL values in required columns
            var nullCheckSql = tableName switch
            {
                "watch_history" => "SELECT COUNT(*) FROM watch_history WHERE anime_title IS NULL OR anime_title = ''",
                "anime_list" => "SELECT COUNT(*) FROM anime_list WHERE anime_title IS NULL OR anime_title = ''",
                "downloads" => "SELECT COUNT(*) FROM downloads WHERE file_path IS NULL OR file_path = ''",
                "read_history" => "SELECT COUNT(*) FROM read_history WHERE manga_title IS NULL OR manga_title = ''",
                "manga_list" => "SELECT COUNT(*) FROM manga_list WHERE manga_title IS NULL OR manga_title = ''",
                _ => null
            };

            var nullCount = 0L;
            if (nullCheckSql != null)
            {
                try
                {
                    await using var nullCmd = new SqliteCommand(nullCheckSql, connection);
                    nullCount = Convert.ToInt64(await nullCmd.ExecuteScalarAsync(cancellationToken));
                }
                catch
                {
                    // Column might not exist in this schema version
                }
            }

            var displayName = tableName.Replace("_", " ");
            displayName = char.ToUpper(displayName[0]) + displayName[1..];

            return new DiagnosticResult
            {
                Name = displayName,
                Category = DiagnosticCategory.Data,
                Severity = nullCount > 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok,
                Message = $"{count} entries",
                Detail = nullCount > 0 ? $"{nullCount} entries with missing data" : null
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<DiagnosticResult?> CheckDownloadsIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            // Check if downloads table exists
            await using var existsCmd = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='downloads';",
                connection);
            var exists = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(cancellationToken)) > 0;

            if (!exists)
            {
                return null;
            }

            // Get total downloads and check for missing files
            await using var cmd = new SqliteCommand(
                "SELECT file_path FROM downloads;",
                connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var total = 0;
            var missing = 0;
            var totalSize = 0L;

            while (await reader.ReadAsync(cancellationToken))
            {
                total++;
                var filePath = reader.GetString(0);
                if (!File.Exists(filePath))
                {
                    missing++;
                }
                else
                {
                    try
                    {
                        totalSize += new FileInfo(filePath).Length;
                    }
                    catch
                    {
                        // Ignore file access errors
                    }
                }
            }

            var sizeMb = totalSize / (1024.0 * 1024);
            var sizeGb = sizeMb / 1024.0;
            var sizeStr = sizeGb >= 1 ? $"{sizeGb:F2} GB" : $"{sizeMb:F1} MB";

            if (missing > 0)
            {
                return new DiagnosticResult
                {
                    Name = "Downloads",
                    Category = DiagnosticCategory.Data,
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"{total} entries, {missing} missing files",
                    Detail = $"Run 'koware offline cleanup' to remove orphaned entries. Total size: {sizeStr}"
                };
            }

            return new DiagnosticResult
            {
                Name = "Downloads",
                Category = DiagnosticCategory.Data,
                Severity = DiagnosticSeverity.Ok,
                Message = $"{total} entries ({sizeStr})"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<DiagnosticResult> CheckDuplicateEntriesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var duplicates = new Dictionary<string, int>();

        try
        {
            // Check for duplicate anime titles in list
            await using var animeCmd = new SqliteCommand(
                @"SELECT anime_title, COUNT(*) as cnt FROM anime_list 
                  GROUP BY anime_title COLLATE NOCASE HAVING cnt > 1;",
                connection);
            try
            {
                await using var reader = await animeCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    duplicates["anime_list"] = (duplicates.GetValueOrDefault("anime_list") + reader.GetInt32(1));
                }
            }
            catch
            {
                // Table might not exist
            }

            // Check for duplicate manga titles in list
            await using var mangaCmd = new SqliteCommand(
                @"SELECT manga_title, COUNT(*) as cnt FROM manga_list 
                  GROUP BY manga_title COLLATE NOCASE HAVING cnt > 1;",
                connection);
            try
            {
                await using var reader = await mangaCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    duplicates["manga_list"] = (duplicates.GetValueOrDefault("manga_list") + reader.GetInt32(1));
                }
            }
            catch
            {
                // Table might not exist
            }
        }
        catch
        {
            // Ignore errors
        }

        var totalDuplicates = duplicates.Values.Sum();
        return new DiagnosticResult
        {
            Name = "Duplicate Entries",
            Category = DiagnosticCategory.Data,
            Severity = totalDuplicates > 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok,
            Message = totalDuplicates > 0 ? $"{totalDuplicates} duplicates found" : "None"
        };
    }

    private async Task<DiagnosticResult> CheckStaleDataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var staleCount = 0;
        var oldestDate = DateTimeOffset.MaxValue;

        try
        {
            // Check for very old watch history entries (older than 2 years)
            var twoYearsAgo = DateTimeOffset.UtcNow.AddYears(-2).ToString("O");
            await using var cmd = new SqliteCommand(
                $"SELECT COUNT(*), MIN(watched_at_utc) FROM watch_history WHERE watched_at_utc < '{twoYearsAgo}';",
                connection);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
                {
                    staleCount = reader.GetInt32(0);
                    if (!reader.IsDBNull(1))
                    {
                        var dateStr = reader.GetString(1);
                        if (DateTimeOffset.TryParse(dateStr, out var parsed))
                        {
                            oldestDate = parsed;
                        }
                    }
                }
            }
            catch
            {
                // Table might not exist or have different schema
            }
        }
        catch
        {
            // Ignore errors
        }

        if (staleCount > 0 && oldestDate != DateTimeOffset.MaxValue)
        {
            return new DiagnosticResult
            {
                Name = "Data Age",
                Category = DiagnosticCategory.Data,
                Severity = DiagnosticSeverity.Info,
                Message = $"{staleCount} entries older than 2 years",
                Detail = $"Oldest: {oldestDate:yyyy-MM-dd}"
            };
        }

        return new DiagnosticResult
        {
            Name = "Data Age",
            Category = DiagnosticCategory.Data,
            Severity = DiagnosticSeverity.Ok,
            Message = "All data is recent"
        };
    }

    #endregion

    #region Security Checks

    private async Task<List<DiagnosticResult>> RunSecurityChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        // SSL/TLS verification
        var sslResult = await CheckSslConnectivityAsync(cancellationToken);
        results.Add(sslResult);

        // Proxy detection
        var proxyResult = CheckProxyConfiguration();
        results.Add(proxyResult);

        // Config file permissions (sensitive data protection)
        var configPermResult = CheckConfigFilePermissions();
        if (configPermResult != null) results.Add(configPermResult);

        // Check for insecure HTTP in provider configs
        var httpSecurityResult = CheckProviderSecurity();
        results.Add(httpSecurityResult);

        // Environment variable security
        var envSecurityResult = CheckEnvironmentSecurity();
        results.Add(envSecurityResult);

        return results;
    }

    private async Task<DiagnosticResult> CheckSslConnectivityAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Test SSL/TLS with a known secure endpoint
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    // Log certificate info but still validate
                    return errors == System.Net.Security.SslPolicyErrors.None;
                }
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(10000);

            var response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", cts.Token);
            sw.Stop();

            return new DiagnosticResult
            {
                Name = "SSL/TLS",
                Category = DiagnosticCategory.Security,
                Severity = response.IsSuccessStatusCode ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
                Message = response.IsSuccessStatusCode ? "Working" : $"HTTP {(int)response.StatusCode}",
                Duration = sw.Elapsed
            };
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("SSL") || ex.Message.Contains("certificate"))
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = "SSL/TLS",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Error,
                Message = "Certificate error",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = "SSL/TLS",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Warning,
                Message = "Check failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private DiagnosticResult CheckProxyConfiguration()
    {
        var httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY") ?? 
                       Environment.GetEnvironmentVariable("http_proxy");
        var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? 
                        Environment.GetEnvironmentVariable("https_proxy");
        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY") ?? 
                     Environment.GetEnvironmentVariable("no_proxy");

        var hasProxy = !string.IsNullOrEmpty(httpProxy) || !string.IsNullOrEmpty(httpsProxy);

        if (hasProxy)
        {
            var proxyInfo = new List<string>();
            if (!string.IsNullOrEmpty(httpProxy)) proxyInfo.Add($"HTTP: {httpProxy}");
            if (!string.IsNullOrEmpty(httpsProxy)) proxyInfo.Add($"HTTPS: {httpsProxy}");

            return new DiagnosticResult
            {
                Name = "Proxy",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Info,
                Message = "Configured",
                Detail = string.Join(", ", proxyInfo)
            };
        }

        // Check system proxy on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var webProxy = WebRequest.GetSystemWebProxy();
                var testUri = new Uri("https://api.github.com");
                var proxyUri = webProxy.GetProxy(testUri);
                if (proxyUri != null && proxyUri != testUri)
                {
                    return new DiagnosticResult
                    {
                        Name = "Proxy",
                        Category = DiagnosticCategory.Security,
                        Severity = DiagnosticSeverity.Info,
                        Message = "System proxy detected",
                        Detail = proxyUri.ToString()
                    };
                }
            }
            catch
            {
                // Ignore proxy detection errors
            }
        }

        return new DiagnosticResult
        {
            Name = "Proxy",
            Category = DiagnosticCategory.Security,
            Severity = DiagnosticSeverity.Ok,
            Message = "No proxy configured"
        };
    }

    private DiagnosticResult? CheckConfigFilePermissions()
    {
        if (!File.Exists(_configPath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(_configPath);
            
            // On Unix-like systems, check if file is world-readable
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Check using Unix file mode if available (.NET 7+)
                try
                {
                    var unixMode = File.GetUnixFileMode(_configPath);
                    var isWorldReadable = (unixMode & UnixFileMode.OtherRead) != 0;
                    var isWorldWritable = (unixMode & UnixFileMode.OtherWrite) != 0;

                    if (isWorldWritable)
                    {
                        return new DiagnosticResult
                        {
                            Name = "Config Permissions",
                            Category = DiagnosticCategory.Security,
                            Severity = DiagnosticSeverity.Warning,
                            Message = "World-writable",
                            Detail = $"chmod 600 {_configPath}"
                        };
                    }

                    if (isWorldReadable)
                    {
                        return new DiagnosticResult
                        {
                            Name = "Config Permissions",
                            Category = DiagnosticCategory.Security,
                            Severity = DiagnosticSeverity.Info,
                            Message = "World-readable",
                            Detail = "Consider restricting permissions if config contains sensitive data"
                        };
                    }
                }
                catch
                {
                    // Unix file mode not available
                }
            }

            return new DiagnosticResult
            {
                Name = "Config Permissions",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Ok,
                Message = "Secure"
            };
        }
        catch
        {
            return null;
        }
    }

    private DiagnosticResult CheckProviderSecurity()
    {
        var issues = new List<string>();

        var animeApi = _animeOptions.Value.ApiBase;
        var mangaApi = _mangaOptions.Value.ApiBase;

        if (!string.IsNullOrEmpty(animeApi) && animeApi.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("Anime provider uses HTTP");
        }

        if (!string.IsNullOrEmpty(mangaApi) && mangaApi.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("Manga provider uses HTTP");
        }

        if (issues.Count > 0)
        {
            return new DiagnosticResult
            {
                Name = "Provider Security",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Warning,
                Message = "Insecure HTTP detected",
                Detail = string.Join("; ", issues)
            };
        }

        return new DiagnosticResult
        {
            Name = "Provider Security",
            Category = DiagnosticCategory.Security,
            Severity = DiagnosticSeverity.Ok,
            Message = "All providers use HTTPS"
        };
    }

    private DiagnosticResult CheckEnvironmentSecurity()
    {
        var concerns = new List<string>();

        // Check if running as root/admin (potentially risky)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    concerns.Add("Running as Administrator");
                }
            }
            catch
            {
                // Ignore
            }
        }
        else
        {
            var uid = Environment.GetEnvironmentVariable("UID") ?? 
                     Environment.GetEnvironmentVariable("EUID");
            if (uid == "0")
            {
                concerns.Add("Running as root");
            }
        }

        // Check for debug mode or verbose logging that might expose data
        var aspnetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (aspnetEnv?.Equals("Development", StringComparison.OrdinalIgnoreCase) == true)
        {
            concerns.Add("Development mode enabled");
        }

        if (concerns.Count > 0)
        {
            return new DiagnosticResult
            {
                Name = "Environment",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Info,
                Message = string.Join(", ", concerns)
            };
        }

        return new DiagnosticResult
        {
            Name = "Environment",
            Category = DiagnosticCategory.Security,
            Severity = DiagnosticSeverity.Ok,
            Message = "Standard user context"
        };
    }

    #endregion

    #region Network Checks

    private async Task<List<DiagnosticResult>> RunNetworkChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        // Basic internet connectivity
        var connectivityResult = await CheckInternetConnectivityAsync(cancellationToken);
        results.Add(connectivityResult);

        if (connectivityResult.Severity == DiagnosticSeverity.Error)
        {
            // Skip other network checks if no internet
            return results;
        }

        // DNS resolution test
        var dnsResult = await CheckDnsResolutionAsync("github.com", cancellationToken);
        results.Add(dnsResult);

        // HTTPS connectivity
        var httpsResult = await CheckHttpsConnectivityAsync("https://api.github.com", cancellationToken);
        results.Add(httpsResult);

        return results;
    }

    private async Task<DiagnosticResult> CheckInternetConnectivityAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Try to ping a reliable host
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 3000);
            sw.Stop();

            if (reply.Status == IPStatus.Success)
            {
                return new DiagnosticResult
                {
                    Name = "Internet Connectivity",
                    Category = DiagnosticCategory.Network,
                    Severity = DiagnosticSeverity.Ok,
                    Message = "Connected",
                    Detail = $"Latency: {reply.RoundtripTime}ms",
                    Duration = sw.Elapsed
                };
            }

            return new DiagnosticResult
            {
                Name = "Internet Connectivity",
                Category = DiagnosticCategory.Network,
                Severity = DiagnosticSeverity.Error,
                Message = "No connection",
                Detail = reply.Status.ToString(),
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Ping might be blocked, try HTTP instead
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(5000);
                var response = await _httpClient.GetAsync("https://www.google.com/generate_204", cts.Token);
                sw.Stop();
                
                return new DiagnosticResult
                {
                    Name = "Internet Connectivity",
                    Category = DiagnosticCategory.Network,
                    Severity = DiagnosticSeverity.Ok,
                    Message = "Connected (via HTTP)",
                    Duration = sw.Elapsed
                };
            }
            catch
            {
                return new DiagnosticResult
                {
                    Name = "Internet Connectivity",
                    Category = DiagnosticCategory.Network,
                    Severity = DiagnosticSeverity.Error,
                    Message = "No connection",
                    Detail = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }
    }

    private async Task<DiagnosticResult> CheckDnsResolutionAsync(string hostname, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken);
            sw.Stop();

            return new DiagnosticResult
            {
                Name = "DNS Resolution",
                Category = DiagnosticCategory.Network,
                Severity = addresses.Length > 0 ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = addresses.Length > 0 ? $"Resolved {hostname}" : "Failed",
                Detail = addresses.Length > 0 ? $"{addresses.Length} address(es)" : null,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = "DNS Resolution",
                Category = DiagnosticCategory.Network,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private async Task<DiagnosticResult> CheckHttpsConnectivityAsync(string url, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(10000);
            
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            return new DiagnosticResult
            {
                Name = "HTTPS Connectivity",
                Category = DiagnosticCategory.Network,
                Severity = response.IsSuccessStatusCode ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
                Message = response.IsSuccessStatusCode ? "Working" : $"HTTP {(int)response.StatusCode}",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = "HTTPS Connectivity",
                Category = DiagnosticCategory.Network,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    #endregion

    #region Provider Checks

    private async Task<List<DiagnosticResult>> RunProviderChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        var providers = new (string name, AllAnimeOptions opts, bool isManga)[]
        {
            ("Anime Provider (allanime)", _animeOptions.Value, false),
            ("Manga Provider (allmanga)", CreateMangaAsAnimeOptions(_mangaOptions.Value), true)
        };

        foreach (var (name, opts, _) in providers)
        {
            if (!opts.IsConfigured || string.IsNullOrWhiteSpace(opts.ApiBase))
            {
                results.Add(new DiagnosticResult
                {
                    Name = name,
                    Category = DiagnosticCategory.Providers,
                    Severity = DiagnosticSeverity.Skipped,
                    Message = "Not configured"
                });
                continue;
            }

            // DNS check
            var dnsResult = await CheckProviderDnsAsync(name, opts.ApiBase, cancellationToken);
            results.Add(dnsResult);

            if (dnsResult.Severity == DiagnosticSeverity.Error)
            {
                continue;
            }

            // HTTP check
            var httpResult = await CheckProviderHttpAsync(name, opts, cancellationToken);
            results.Add(httpResult);

            // API validation (try a simple query)
            if (httpResult.Severity == DiagnosticSeverity.Ok)
            {
                var apiResult = await CheckProviderApiAsync(name, opts, cancellationToken);
                results.Add(apiResult);
            }
        }

        return results;
    }

    private static AllAnimeOptions CreateMangaAsAnimeOptions(AllMangaOptions manga) => new()
    {
        Enabled = manga.Enabled,
        BaseHost = manga.BaseHost,
        ApiBase = manga.ApiBase,
        Referer = manga.Referer,
        UserAgent = manga.UserAgent
    };

    private async Task<DiagnosticResult> CheckProviderDnsAsync(string providerName, string apiBase, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var uri = new Uri(apiBase);
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            sw.Stop();

            return new DiagnosticResult
            {
                Name = $"{providerName} DNS",
                Category = DiagnosticCategory.Providers,
                Severity = addresses.Length > 0 ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = addresses.Length > 0 ? $"Resolved ({uri.Host})" : "Failed",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = $"{providerName} DNS",
                Category = DiagnosticCategory.Providers,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private async Task<DiagnosticResult> CheckProviderHttpAsync(string providerName, AllAnimeOptions opts, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var baseUri = new Uri(opts.ApiBase!.EndsWith('/') ? opts.ApiBase : opts.ApiBase + "/");
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/api"));
            
            if (!string.IsNullOrWhiteSpace(opts.UserAgent))
            {
                request.Headers.UserAgent.ParseAdd(opts.UserAgent);
            }
            request.Headers.Accept.ParseAdd("application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(10000);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            return new DiagnosticResult
            {
                Name = $"{providerName} HTTP",
                Category = DiagnosticCategory.Providers,
                Severity = response.IsSuccessStatusCode || (int)response.StatusCode < 500 ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = $"HTTP {(int)response.StatusCode}",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = $"{providerName} HTTP",
                Category = DiagnosticCategory.Providers,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private async Task<DiagnosticResult> CheckProviderApiAsync(string providerName, AllAnimeOptions opts, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Try a minimal GraphQL query to validate API is working
            var baseUri = new Uri(opts.ApiBase!.EndsWith('/') ? opts.ApiBase : opts.ApiBase + "/");
            var graphqlUri = new Uri(baseUri, "api");
            
            var query = new { query = "{ __typename }" };
            var content = new StringContent(
                JsonSerializer.Serialize(query),
                System.Text.Encoding.UTF8,
                "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, graphqlUri) { Content = content };
            if (!string.IsNullOrWhiteSpace(opts.UserAgent))
            {
                request.Headers.UserAgent.ParseAdd(opts.UserAgent);
            }
            if (!string.IsNullOrWhiteSpace(opts.Referer))
            {
                request.Headers.Referrer = new Uri(opts.Referer);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(10000);

            var response = await _httpClient.SendAsync(request, cts.Token);
            sw.Stop();

            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
            var hasData = responseBody.Contains("\"data\"") || responseBody.Contains("\"__typename\"");
            var hasError = responseBody.Contains("\"errors\"");

            if (response.IsSuccessStatusCode && (hasData || !hasError))
            {
                return new DiagnosticResult
                {
                    Name = $"{providerName} API",
                    Category = DiagnosticCategory.Providers,
                    Severity = DiagnosticSeverity.Ok,
                    Message = "Responding",
                    Duration = sw.Elapsed
                };
            }

            return new DiagnosticResult
            {
                Name = $"{providerName} API",
                Category = DiagnosticCategory.Providers,
                Severity = DiagnosticSeverity.Warning,
                Message = hasError ? "API returned errors" : $"HTTP {(int)response.StatusCode}",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticResult
            {
                Name = $"{providerName} API",
                Category = DiagnosticCategory.Providers,
                Severity = DiagnosticSeverity.Warning,
                Message = "API check failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    #endregion

    #region Toolchain Checks

    private List<DiagnosticResult> RunToolchainChecks()
    {
        var results = new List<DiagnosticResult>();

        // External tools
        var tools = new[] { "ffmpeg", "yt-dlp", "aria2c" };
        foreach (var tool in tools)
        {
            var path = ResolveExecutablePath(tool);
            if (path is null)
            {
                results.Add(new DiagnosticResult
                {
                    Name = tool,
                    Category = DiagnosticCategory.Toolchain,
                    Severity = DiagnosticSeverity.Warning,
                    Message = "Not found",
                    Detail = "Some features may not work"
                });
                continue;
            }

            var version = TryGetCommandVersion(path);
            results.Add(new DiagnosticResult
            {
                Name = tool,
                Category = DiagnosticCategory.Toolchain,
                Severity = DiagnosticSeverity.Ok,
                Message = path,
                Detail = version
            });
        }

        return results;
    }

    private static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        // If it's an absolute path and exists, return it
        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            return command;
        }

        // Search in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var basePath in paths)
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(basePath, command + ext);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static string? TryGetCommandVersion(string path, string args = "--version")
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
            {
                output = process.StandardError.ReadToEnd();
            }

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Engine Checks

    private async Task<List<DiagnosticResult>> RunEngineChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        // Check 1: Assembly loading - verify core assemblies are loadable
        var sw = Stopwatch.StartNew();
        try
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            var referencedAssemblies = entryAssembly?.GetReferencedAssemblies() ?? Array.Empty<AssemblyName>();
            var loadedCount = 0;
            var failedAssemblies = new List<string>();

            foreach (var asmName in referencedAssemblies.Take(20)) // Check first 20 to avoid long delays
            {
                try
                {
                    Assembly.Load(asmName);
                    loadedCount++;
                }
                catch
                {
                    failedAssemblies.Add(asmName.Name ?? "unknown");
                }
            }
            sw.Stop();

            results.Add(new DiagnosticResult
            {
                Name = "Assembly Loading",
                Category = DiagnosticCategory.Engine,
                Severity = failedAssemblies.Count == 0 ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning,
                Message = failedAssemblies.Count == 0 ? $"{loadedCount} assemblies verified" : $"{failedAssemblies.Count} failed to load",
                Detail = failedAssemblies.Count > 0 ? string.Join(", ", failedAssemblies.Take(5)) : null,
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "Assembly Loading",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed to verify assemblies",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 2: Configuration serialization - verify JSON serialization works
        sw = Stopwatch.StartNew();
        try
        {
            var testObject = new { test = "value", number = 42, nested = new { inner = true } };
            var json = JsonSerializer.Serialize(testObject);
            var parsed = JsonDocument.Parse(json);
            var valid = parsed.RootElement.TryGetProperty("test", out var prop) && prop.GetString() == "value";
            sw.Stop();

            results.Add(new DiagnosticResult
            {
                Name = "JSON Serialization",
                Category = DiagnosticCategory.Engine,
                Severity = valid ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = valid ? "Working" : "Serialization mismatch",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "JSON Serialization",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 3: Async/Task infrastructure
        sw = Stopwatch.StartNew();
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            tcs.SetResult(true);
            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            sw.Stop();

            results.Add(new DiagnosticResult
            {
                Name = "Async Infrastructure",
                Category = DiagnosticCategory.Engine,
                Severity = result ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = result ? "Working" : "Task completion failed",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "Async Infrastructure",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 4: File I/O - verify temp file operations work
        sw = Stopwatch.StartNew();
        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"koware-engine-test-{Guid.NewGuid():N}.tmp");
            var testContent = "Koware engine test " + DateTime.UtcNow.Ticks;
            await File.WriteAllTextAsync(tempFile, testContent, cancellationToken);
            var readBack = await File.ReadAllTextAsync(tempFile, cancellationToken);
            File.Delete(tempFile);
            sw.Stop();

            var valid = readBack == testContent;
            results.Add(new DiagnosticResult
            {
                Name = "File I/O",
                Category = DiagnosticCategory.Engine,
                Severity = valid ? DiagnosticSeverity.Ok : DiagnosticSeverity.Error,
                Message = valid ? "Working" : "Read/write mismatch",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "File I/O",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Error,
                Message = "Failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 5: HTTP client functionality
        sw = Stopwatch.StartNew();
        try
        {
            using var testClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            testClient.DefaultRequestHeaders.UserAgent.ParseAdd("Koware-EngineTest/1.0");
            
            // Simple connectivity test - just check if we can create and configure the client properly
            var baseAddress = new Uri("https://api.github.com");
            var request = new HttpRequestMessage(HttpMethod.Head, baseAddress);
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(5000);
            
            var response = await testClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            results.Add(new DiagnosticResult
            {
                Name = "HTTP Client",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Ok,
                Message = "Working",
                Detail = $"Response: {(int)response.StatusCode}",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "HTTP Client",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Warning,
                Message = "Limited connectivity",
                Detail = ex.Message.Length > 100 ? ex.Message[..100] + "..." : ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 6: Garbage collection / memory management
        sw = Stopwatch.StartNew();
        try
        {
            var beforeGc = GC.GetTotalMemory(forceFullCollection: false);
            GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            var afterGc = GC.GetTotalMemory(forceFullCollection: false);
            sw.Stop();

            results.Add(new DiagnosticResult
            {
                Name = "Memory Management",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Ok,
                Message = "Working",
                Detail = $"Heap: {afterGc / (1024.0 * 1024):F1} MB",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "Memory Management",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Warning,
                Message = "Check failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 7: Thread pool health
        sw = Stopwatch.StartNew();
        try
        {
            ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
            ThreadPool.GetMaxThreads(out var maxWorker, out var maxCompletion);
            sw.Stop();

            var workerUsage = 100 - (workerThreads * 100 / maxWorker);
            var severity = workerUsage > 90 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;

            results.Add(new DiagnosticResult
            {
                Name = "Thread Pool",
                Category = DiagnosticCategory.Engine,
                Severity = severity,
                Message = severity == DiagnosticSeverity.Ok ? "Healthy" : "High usage",
                Detail = $"Workers: {maxWorker - workerThreads}/{maxWorker} in use",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "Thread Pool",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Warning,
                Message = "Check failed",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        // Check 8: Provider options validation
        sw = Stopwatch.StartNew();
        try
        {
            var animeOpts = _animeOptions.Value;
            var mangaOpts = _mangaOptions.Value;
            var animeConfigured = animeOpts?.IsConfigured ?? false;
            var mangaConfigured = mangaOpts?.IsConfigured ?? false;
            sw.Stop();

            results.Add(new DiagnosticResult
            {
                Name = "Provider Options",
                Category = DiagnosticCategory.Engine,
                Severity = (animeConfigured || mangaConfigured) ? DiagnosticSeverity.Ok : DiagnosticSeverity.Info,
                Message = (animeConfigured || mangaConfigured) ? "Configured" : "Using defaults",
                Detail = $"Anime: {(animeConfigured ? "configured" : "default")}, Manga: {(mangaConfigured ? "configured" : "default")}",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "Provider Options",
                Category = DiagnosticCategory.Engine,
                Severity = DiagnosticSeverity.Warning,
                Message = "Failed to load",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        return results;
    }

    #endregion

    #region Update Checks

    private async Task<List<DiagnosticResult>> RunUpdateChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        var sw = Stopwatch.StartNew();
        try
        {
            // Use /releases?per_page=1 instead of /releases/latest to include prereleases (e.g., beta versions)
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/S1mplector/Koware/releases?per_page=1");
            request.Headers.UserAgent.ParseAdd("Koware-Doctor/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(10000);

            var response = await _httpClient.SendAsync(request, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                results.Add(new DiagnosticResult
                {
                    Name = "Update Check",
                    Category = DiagnosticCategory.Updates,
                    Severity = DiagnosticSeverity.Warning,
                    Message = "Could not check for updates",
                    Detail = $"HTTP {(int)response.StatusCode}",
                    Duration = sw.Elapsed
                });
                return results;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            
            // /releases returns an array, so get the first element
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                results.Add(new DiagnosticResult
                {
                    Name = "Update Check",
                    Category = DiagnosticCategory.Updates,
                    Severity = DiagnosticSeverity.Warning,
                    Message = "No releases found",
                    Duration = sw.Elapsed
                });
                return results;
            }
            
            var release = doc.RootElement[0];
            var tagName = release.GetProperty("tag_name").GetString() ?? "unknown";
            var currentVersion = GetVersionLabel();

            // Simple version comparison (remove 'v' prefix if present)
            var latestClean = tagName.TrimStart('v', 'V');
            var currentClean = currentVersion.TrimStart('v', 'V').Split('+')[0].Split('-')[0];

            var isUpToDate = string.Equals(latestClean, currentClean, StringComparison.OrdinalIgnoreCase) ||
                            IsVersionNewer(currentClean, latestClean);

            results.Add(new DiagnosticResult
            {
                Name = "Update Check",
                Category = DiagnosticCategory.Updates,
                Severity = isUpToDate ? DiagnosticSeverity.Ok : DiagnosticSeverity.Info,
                Message = isUpToDate ? "Up to date" : $"Update available: {tagName}",
                Detail = $"Current: {currentVersion}",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new DiagnosticResult
            {
                Name = "Update Check",
                Category = DiagnosticCategory.Updates,
                Severity = DiagnosticSeverity.Warning,
                Message = "Could not check for updates",
                Detail = ex.Message,
                Duration = sw.Elapsed
            });
        }

        return results;
    }

    private static bool IsVersionNewer(string current, string latest)
    {
        try
        {
            if (Version.TryParse(current, out var currentVer) && Version.TryParse(latest, out var latestVer))
            {
                return currentVer >= latestVer;
            }
        }
        catch
        {
            // Ignore version parsing errors
        }
        return false;
    }

    #endregion

    #region Helpers

    private static string GetVersionLabel()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        var version = assembly.GetName().Version;
        return version?.ToString() ?? "unknown";
    }

    #endregion
}
