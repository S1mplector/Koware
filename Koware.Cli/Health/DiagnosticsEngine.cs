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
    Updates
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
        var results = new List<DiagnosticResult>();

        // Environment checks
        results.AddRange(RunEnvironmentChecks());

        // Configuration checks
        results.AddRange(RunConfigurationChecks());

        // Storage checks
        results.AddRange(await RunStorageChecksAsync(cancellationToken));

        // Network checks
        results.AddRange(await RunNetworkChecksAsync(cancellationToken));

        // Provider checks
        results.AddRange(await RunProviderChecksAsync(cancellationToken));

        // Toolchain checks
        results.AddRange(RunToolchainChecks());

        // Update checks
        results.AddRange(await RunUpdateChecksAsync(cancellationToken));

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
            DiagnosticCategory.Configuration => RunConfigurationChecks(),
            DiagnosticCategory.Storage => await RunStorageChecksAsync(cancellationToken),
            DiagnosticCategory.Network => await RunNetworkChecksAsync(cancellationToken),
            DiagnosticCategory.Providers => await RunProviderChecksAsync(cancellationToken),
            DiagnosticCategory.Toolchain => RunToolchainChecks(),
            DiagnosticCategory.Updates => await RunUpdateChecksAsync(cancellationToken),
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

        return results;
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

    #region Update Checks

    private async Task<List<DiagnosticResult>> RunUpdateChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        var sw = Stopwatch.StartNew();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/S1mplector/Koware/releases/latest");
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
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
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
