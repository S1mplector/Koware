// Author: Ilgaz Mehmetoğlu
// Entry point and command routing for the Koware CLI, including playback orchestration and configuration handling.

using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Koware.Application.DependencyInjection;
using Koware.Application.Models;
using Koware.Application.UseCases;
using Koware.Domain.Models;
using Koware.Application.Abstractions;
using Koware.Infrastructure.DependencyInjection;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Koware.Cli.Configuration;
using Koware.Cli.Config;
using Koware.Cli.History;
using Koware.Cli.Console;
using Koware.Cli.Health;
using Koware.Updater;
using Koware.Autoconfig.DependencyInjection;
using Koware.Autoconfig.Orchestration;
using Koware.Autoconfig.Storage;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using var host = BuildHost(args);
var exitCode = await RunAsync(host, args);
return exitCode;

/// <summary>
/// Gets the user-writable configuration directory for Koware.
/// </summary>
/// <returns>
/// On Windows: %APPDATA%\koware
/// On macOS/Linux: ~/.config/koware
/// </returns>
static string GetUserConfigDirectory()
{
    if (OperatingSystem.IsWindows())
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "koware");
    }
    else
    {
        // macOS and Linux: use XDG config directory
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(configHome, "koware");
    }
}

/// <summary>
/// Gets the full path to the user configuration file (appsettings.user.json).
/// Creates the directory if it doesn't exist.
/// </summary>
static string GetUserConfigFilePath()
{
    var dir = GetUserConfigDirectory();
    if (!Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    return Path.Combine(dir, "appsettings.user.json");
}

/// <summary>
/// Configure and build the host that wires up dependency injection, configuration, and logging.
/// </summary>
/// <param name="args">Command-line arguments passed to the CLI.</param>
/// <returns>A fully configured <see cref="IHost"/> ready to run commands.</returns>
/// <remarks>
/// Loads appsettings.json and appsettings.user.json from the base directory.
/// Registers application, infrastructure, player, and watch history services.
/// </remarks>
static IHost BuildHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: true);
    // Load user config from user-writable directory (cross-platform)
    builder.Configuration.AddJsonFile(GetUserConfigFilePath(), optional: true, reloadOnChange: true);
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection("Player"));
    builder.Services.Configure<ReaderOptions>(builder.Configuration.GetSection("Reader"));
    builder.Services.Configure<DefaultCliOptions>(builder.Configuration.GetSection("Defaults"));
    builder.Services.Configure<ThemeOptions>(builder.Configuration.GetSection("Theme"));
    builder.Services.AddSingleton<IWatchHistoryStore, SqliteWatchHistoryStore>();
    builder.Services.AddSingleton<IReadHistoryStore, SqliteReadHistoryStore>();
    builder.Services.AddSingleton<IAnimeListStore, SqliteAnimeListStore>();
    builder.Services.AddSingleton<IMangaListStore, SqliteMangaListStore>();
    builder.Services.AddSingleton<IDownloadStore, SqliteDownloadStore>();
    builder.Services.AddAutoconfigServices();
    builder.Services.AddHttpClient();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("koware.cli", LogLevel.Warning);
    builder.Logging.AddFilter("Koware.Infrastructure", LogLevel.Warning);

    return builder.Build();
}

/// <summary>
/// Main entry for the CLI logic: dispatches commands, sets up cancellation, and top-level error handling.
/// </summary>
/// <param name="host">The configured dependency injection host.</param>
/// <param name="args">Command-line arguments; first element is the command name.</param>
/// <returns>
/// Exit code: 0 on success, 1 on error, 2 on user cancellation.
/// </returns>
/// <remarks>
/// Handles Ctrl+C via <see cref="CancellationTokenSource"/>.
/// Catches network errors and logs user-friendly hints.
/// </remarks>
static async Task<int> RunAsync(IHost host, string[] args)
{
    using var scope = host.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("koware.cli");
    var defaults = services.GetService<IOptions<DefaultCliOptions>>()?.Value ?? new DefaultCliOptions();
    var themeOptions = services.GetService<IOptions<ThemeOptions>>()?.Value;
    Theme.Initialize(themeOptions);
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        logger.LogInformation("Cancellation requested. Stopping...");
        e.Cancel = true;
        cts.Cancel();
    };

    if (args.Length == 0)
    {
        PrintBanner();
        // Show warning if no providers configured on first run
        WarnIfNoProvidersConfigured(services);
        return 0;
    }

    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();
    var command = args[0].ToLowerInvariant();

    // Commands that require configured providers (will block if not configured)
    var providerRequiredCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "search", "plan", "stream", "watch", "play", "download", "read", "last", "continue", "list", "recommend", "rec"
    };

    // Commands that should NOT show the provider warning (utility/setup commands)
    var utilityCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "help", "version", "update", "doctor", "provider", "config", "theme", "mode"
    };

    // Show non-blocking warning for commands that don't require providers but aren't utility commands
    if (!providerRequiredCommands.Contains(command) && !utilityCommands.Contains(command))
    {
        WarnIfNoProvidersConfigured(services);
    }

    try
    {
        // Check providers early for commands that need them
        if (providerRequiredCommands.Contains(command))
        {
            var mode = defaults.GetMode();
            // For anime-only commands (plan/stream/play/download), use anime mode
            if (command is "plan" or "stream" or "watch" or "play" or "download")
            {
                mode = CliMode.Anime;
            }
            // For manga-only commands (read), use manga mode
            else if (command == "read")
            {
                mode = CliMode.Manga;
            }
            
            if (!CheckProvidersConfigured(services, mode, logger))
            {
                return 1;
            }
        }

        switch (command)
        {
            case "search":
                return await HandleSearchAsync(orchestrator, args, services, logger, defaults, cts.Token);
            case "plan":
            case "stream":
                return await HandlePlanAsync(orchestrator, args, logger, defaults, cts.Token);
            case "watch":
            case "play":
                return await HandlePlayAsync(orchestrator, args, services, logger, defaults, cts.Token);
            case "download":
                return await HandleDownloadAsync(orchestrator, args, services, logger, defaults, cts.Token);
            case "read":
                return await HandleReadAsync(args, services, logger, defaults, cts.Token);
            case "last":
                return await HandleLastAsync(args, services, logger, defaults, cts.Token);
            case "continue":
                return await HandleContinueAsync(args, services, logger, defaults, cts.Token);
            case "history":
                return await HandleHistoryAsync(args, services, logger, defaults, cts.Token);
            case "list":
                return await HandleListAsync(args, services, logger, defaults, cts.Token);
            case "recommend":
            case "rec":
                return await HandleRecommendAsync(args, services, logger, defaults, cts.Token);
            case "offline":
            case "downloads":
                return await HandleOfflineAsync(args, services, logger, defaults, cts.Token);
            case "config":
                return HandleConfig(args);
            case "theme":
                return HandleTheme(args);
            case "stats":
                return await HandleStatsAsync(args, services, logger, defaults, cts.Token);
            case "doctor":
                return await HandleDoctorAsync(args, services, logger, cts.Token);
            case "provider":
                return await HandleProviderAsync(args, services);
            case "mode":
                return await HandleModeAsync(args, logger);
            case "update":
                return await HandleUpdateAsync(args, logger, cts.Token);
            case "version":
            case "--version":
            case "-v":
                return HandleVersion();
            case "help":
            case "--help":
            case "-h":
                return HandleHelp(args, defaults.GetMode());
            default:
                Koware.Cli.Console.ErrorDisplay.UnknownCommand(command);
                return 1;
        }
    }
    catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
    {
        Koware.Cli.Console.ErrorDisplay.Timeout("request");
        return 1;
    }
    catch (OperationCanceledException)
    {
        Koware.Cli.Console.ErrorDisplay.Cancelled();
        return 2;
    }
    catch (HttpRequestException ex)
    {
        Koware.Cli.Console.ErrorDisplay.NetworkError(ex.Message);
        return 1;
    }
    catch (Exception ex)
    {
        Koware.Cli.Console.ErrorDisplay.Generic("An unexpected error occurred", ex.Message, "Run 'koware doctor' to check your setup.");
        return 1;
    }
}

/// <summary>
/// Check if any providers are configured for the given mode (anime or manga).
/// Returns true if at least one provider is ready; otherwise prints guidance and returns false.
/// </summary>
static bool CheckProvidersConfigured(IServiceProvider services, CliMode mode, ILogger logger)
{
    var allAnime = services.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
    var allManga = services.GetRequiredService<IOptions<AllMangaOptions>>().Value;

    bool hasConfiguredProvider;
    string modeLabel;

    if (mode == CliMode.Manga)
    {
        hasConfiguredProvider = allManga.IsConfigured;
        modeLabel = "manga";
    }
    else
    {
        hasConfiguredProvider = allAnime.IsConfigured;
        modeLabel = "anime";
    }

    if (!hasConfiguredProvider)
    {
        Koware.Cli.Console.ErrorDisplay.ProviderNotConfigured(modeLabel);
        return false;
    }

    return true;
}

/// <summary>
/// Show a non-blocking warning if no providers are configured at all.
/// This is displayed on startup for informational purposes.
/// </summary>
static void WarnIfNoProvidersConfigured(IServiceProvider services)
{
    var allAnime = services.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
    var allManga = services.GetRequiredService<IOptions<AllMangaOptions>>().Value;

    // Check if ANY provider is configured
    var hasAnyProvider = allAnime.IsConfigured || allManga.IsConfigured;

    if (!hasAnyProvider)
    {
        Koware.Cli.Console.ErrorDisplay.NoProvidersWarning();
    }
}

/// <summary>
/// Given a scrape plan, resolve streams, launch the player, and update watch history.
/// </summary>
/// <param name="orchestrator">The scraping orchestrator that resolves anime/episodes/streams.</param>
/// <param name="plan">The query, episode, quality, and match preferences.</param>
/// <param name="services">Service provider for player options and AllAnime config.</param>
/// <param name="history">Watch history store to record successful plays.</param>
/// <param name="logger">Logger for debug and info messages.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
/// <returns>Exit code from the player process; 0 means success.</returns>
/// <remarks>
/// Filters streams by player capability, picks the best one, launches the player,
/// and optionally lets the user retry with a different quality.
/// </remarks>
static async Task<int> ExecuteAndPlayAsync(
    ScrapeOrchestrator orchestrator,
    ScrapePlan plan,
    IServiceProvider services,
    IWatchHistoryStore history,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var step = ConsoleStep.Start("Resolving streams");
    ScrapeResult result;
    try
    {
        result = await orchestrator.ExecuteAsync(plan, cancellationToken);
        step.Succeed("Streams ready");
    }
    catch (Exception)
    {
        step.Fail("Failed to resolve streams");
        throw;
    }
    if (result.SelectedEpisode is null || result.Streams is null || result.Streams.Count == 0)
    {
        logger.LogWarning("No streams found for the query/episode.");
        RenderPlan(plan, result);
        return 1;
    }

    var playerOptions = services.GetRequiredService<IOptions<PlayerOptions>>().Value;
    var playerResolution = ResolvePlayerExecutable(playerOptions);
    var defaultReferrer = services.GetService<IOptions<AllAnimeOptions>>()?.Value?.Referer;
    var normalizedStreams = ApplyDefaultReferrer(result.Streams, defaultReferrer);
    var filteredStreams = FilterStreamsForPlayer(normalizedStreams, playerResolution.Name, logger);

    var stream = PickBestStream(filteredStreams);
    if (stream is null)
    {
        logger.LogWarning("No playable streams found.");
        return 1;
    }
    logger.LogDebug("Selected stream {Quality} from host {Host}", stream.Quality ?? "unknown", stream.Url.Host);

    var allAnimeOptions = services.GetService<IOptions<AllAnimeOptions>>()?.Value;
    var displayTitle = BuildPlayerTitle(result, stream);
    var httpReferrer = stream.Referrer ?? allAnimeOptions?.Referer;
    var prettyTitle = string.IsNullOrWhiteSpace(displayTitle) ? stream.Url.ToString() : displayTitle!;
    logger.LogInformation("Playing {Title} via {Host} [{Quality}]", prettyTitle, stream.Url.Host, stream.Quality ?? "auto");

    var exitCode = LaunchPlayer(playerOptions, stream, logger, httpReferrer, allAnimeOptions?.UserAgent, prettyTitle, playerResolution);

    if (!plan.NonInteractive && filteredStreams.Count > 1)
    {
        exitCode = ReplayWithDifferentQuality(filteredStreams, playerOptions, playerResolution, logger, httpReferrer, allAnimeOptions?.UserAgent, prettyTitle, exitCode);
    }

    if (result.SelectedAnime is not null && result.SelectedEpisode is not null)
    {
        var entry = new WatchHistoryEntry
        {
            Provider = stream.SourceTag ?? stream.Provider,
            AnimeId = result.SelectedAnime.Id.Value,
            AnimeTitle = result.SelectedAnime.Title,
            EpisodeNumber = result.SelectedEpisode.Number,
            EpisodeTitle = result.SelectedEpisode.Title,
            Quality = stream.Quality,
            WatchedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await history.AddAsync(entry, cancellationToken);
            if (exitCode != 0)
            {
                logger.LogWarning("Player exited with code {ExitCode}, but history was saved so you can retry with 'koware last --play'.", exitCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update watch history.");
        }

        // Update anime tracking list (auto-adds if not present, auto-completes if finished)
        try
        {
            var animeList = services.GetRequiredService<IAnimeListStore>();
            var totalEpisodes = result.Episodes?.Count;
            await animeList.RecordEpisodeWatchedAsync(
                result.SelectedAnime.Id.Value,
                result.SelectedAnime.Title,
                result.SelectedEpisode.Number,
                totalEpisodes,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to update anime list tracking.");
        }
    }

    return exitCode;
}

/// <summary>
/// Implement the <c>koware last</c> command: show or replay the most recent history entry.
/// </summary>
/// <param name="args">CLI arguments; supports --play and --json flags.</param>
/// <param name="services">Service provider for history store.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success, 1 if no history exists.</returns>
/// <remarks>Mode-aware: shows last watched anime or last read manga based on current mode.</remarks>
static async Task<int> HandleLastAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
    var mode = defaults.GetMode();

    if (mode == CliMode.Manga)
    {
        // Manga mode - show last read
        var readHistory = services.GetRequiredService<IReadHistoryStore>();
        var readEntry = await readHistory.GetLastAsync(cancellationToken);
        if (readEntry is null)
        {
            logger.LogWarning("No read history found.");
            return 1;
        }

        if (json)
        {
            var jsonText = JsonSerializer.Serialize(readEntry, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(jsonText);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("Last Read");
            Console.ResetColor();
            Console.WriteLine(new string('─', 40));

            WriteLastField("Manga", readEntry.MangaTitle, ConsoleColor.White);
            
            var chText = readEntry.ChapterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(readEntry.ChapterTitle) && readEntry.ChapterTitle != $"Chapter {readEntry.ChapterNumber}")
            {
                chText += $" - {readEntry.ChapterTitle}";
            }
            WriteLastField("Chapter", chText, ConsoleColor.Yellow);
            
            WriteLastField("Provider", readEntry.Provider, ConsoleColor.Gray);

            var ago = DateTimeOffset.UtcNow - readEntry.ReadAt;
            var agoText = ago.TotalMinutes < 1 ? "just now" :
                         ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago" :
                         ago.TotalHours < 24 ? $"{(int)ago.TotalHours}h ago" :
                         ago.TotalDays < 7 ? $"{(int)ago.TotalDays}d ago" :
                         readEntry.ReadAt.LocalDateTime.ToString("MMM dd, yyyy");
            WriteLastField("Read", $"{readEntry.ReadAt.LocalDateTime:g} ({agoText})", ConsoleColor.DarkGray);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Tip: Use 'koware continue' to read the next chapter.");
            Console.ResetColor();
        }

        return 0;
    }

    // Anime mode (default) - show last watched
    var history = services.GetRequiredService<IWatchHistoryStore>();
    var entry = await history.GetLastAsync(cancellationToken);
    if (entry is null)
    {
        logger.LogWarning("No watch history found.");
        return 1;
    }

    var play = args.Any(a => string.Equals(a, "--play", StringComparison.OrdinalIgnoreCase));

    if (!play)
    {
        if (json)
        {
            var jsonText = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(jsonText);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Last Watched");
            Console.ResetColor();
            Console.WriteLine(new string('─', 40));

            WriteLastField("Anime", entry.AnimeTitle, ConsoleColor.White);
            
            var epText = entry.EpisodeNumber.ToString();
            if (!string.IsNullOrWhiteSpace(entry.EpisodeTitle))
            {
                epText += $" - {entry.EpisodeTitle}";
            }
            WriteLastField("Episode", epText, ConsoleColor.Yellow);
            
            WriteLastField("Provider", entry.Provider, ConsoleColor.Gray);
            
            if (!string.IsNullOrWhiteSpace(entry.Quality))
            {
                WriteLastField("Quality", entry.Quality, ConsoleColor.Gray);
            }

            var ago = DateTimeOffset.UtcNow - entry.WatchedAt;
            var agoText = ago.TotalMinutes < 1 ? "just now" :
                         ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago" :
                         ago.TotalHours < 24 ? $"{(int)ago.TotalHours}h ago" :
                         ago.TotalDays < 7 ? $"{(int)ago.TotalDays}d ago" :
                         entry.WatchedAt.LocalDateTime.ToString("MMM dd, yyyy");
            WriteLastField("Watched", $"{entry.WatchedAt.LocalDateTime:g} ({agoText})", ConsoleColor.DarkGray);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Tip: Use 'koware last --play' to replay, or 'koware continue' for next episode.");
            Console.ResetColor();
        }

        return 0;
    }

    return 0;
}

/// <summary>
/// Render a one-line status entry like "DNS: OK" or "HTTP: FAIL - details".
/// </summary>
/// <param name="label">Short label for the check (e.g., "DNS", "HTTP", "ffmpeg").</param>
/// <param name="success">True if the check passed, false otherwise.</param>
/// <param name="detail">Optional detail string shown after the status.</param>
/// <remarks>Used by <see cref="HandleDoctorAsync"/> to summarize connectivity and tool checks.</remarks>
static void WriteStatus(string label, bool success, string? detail = null)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write($"  {label,-10}: ");
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.Write(success ? "OK" : "FAIL");
    if (!string.IsNullOrWhiteSpace(detail))
    {
        Console.Write($" - {detail}");
    }
    Console.WriteLine();
    Console.ForegroundColor = prev;
}

/// <summary>
/// Implement the <c>koware doctor</c> command.
/// </summary>
/// <param name="args">Command-line arguments for filtering categories.</param>
/// <param name="services">Service provider for provider options.</param>
/// <param name="logger">Logger instance for errors.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 if all critical checks pass, 1 otherwise.</returns>
/// <remarks>
/// Runs comprehensive diagnostics including:
/// - Environment: OS, .NET runtime, disk space, memory
/// - Configuration: Config file validation, provider settings
/// - Storage: SQLite database health, permissions, storage usage
/// - Network: Internet connectivity, DNS resolution, HTTPS
/// - Providers: DNS, HTTP, and API validation for configured providers
/// - Toolchain: External tools (ffmpeg, yt-dlp, aria2c)
/// - Updates: Check for newer versions
/// 
/// Flags:
///   --category, -c  Run only a specific category (env, config, storage, network, providers, tools, updates)
///   --verbose, -v   Show detailed output including metadata
///   --json          Output results as JSON
///   --help, -h      Show help message
/// </remarks>
static async Task<int> HandleDoctorAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    // Parse arguments
    var verbose = args.Any(a => a is "--verbose" or "-v");
    var jsonOutput = args.Any(a => a == "--json");
    var showHelp = args.Any(a => a is "--help" or "-h");
    DiagnosticCategory? categoryFilter = null;

    if (showHelp)
    {
        PrintDoctorHelp();
        return 0;
    }

    // Parse category filter
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--category" or "-c" && i + 1 < args.Length)
        {
            categoryFilter = ParseDiagnosticCategory(args[i + 1]);
            if (categoryFilter is null)
            {
                Console.WriteLine($"Unknown category: {args[i + 1]}");
                Console.WriteLine("Valid categories: env, config, storage, network, providers, tools, updates");
                return 1;
            }
        }
    }

    var animeOptions = services.GetRequiredService<IOptions<AllAnimeOptions>>();
    var mangaOptions = services.GetRequiredService<IOptions<AllMangaOptions>>();
    var configPath = GetUserConfigFilePath();

    var engine = new DiagnosticsEngine(
        new HttpClient(),
        animeOptions,
        mangaOptions,
        configPath);

    if (!jsonOutput)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                     Koware Diagnostics                       ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    IReadOnlyList<DiagnosticResult> results;
    
    try
    {
        if (categoryFilter.HasValue)
        {
            if (!jsonOutput)
            {
                var step = ConsoleStep.Start($"Running {categoryFilter.Value} diagnostics");
                results = await engine.RunCategoryAsync(categoryFilter.Value, cancellationToken);
                step.Succeed($"{results.Count} checks completed");
            }
            else
            {
                results = await engine.RunCategoryAsync(categoryFilter.Value, cancellationToken);
            }
        }
        else
        {
            if (!jsonOutput)
            {
                // Show progress bar while running diagnostics
                var lastCategory = "";
                var progress = new Progress<(int current, int total, string category)>(p =>
                {
                    lastCategory = p.category;
                    var percent = (int)(p.current * 100.0 / p.total);
                    var filled = (int)(p.current * 30.0 / p.total);
                    var empty = 30 - filled;
                    var bar = new string('█', filled) + new string('░', empty);
                    
                    Console.Write($"\r  [{bar}] {percent,3}% - {p.category,-20}");
                });
                
                results = await engine.RunAllWithProgressAsync(progress, cancellationToken);
                
                // Clear progress bar and show completion
                Console.Write("\r" + new string(' ', 70) + "\r");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✔ {results.Count} checks completed");
                Console.ResetColor();
            }
            else
            {
                results = await engine.RunAllAsync(cancellationToken);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Diagnostics engine failed.");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Diagnostics failed: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    if (jsonOutput)
    {
        var jsonResults = results.Select(r => new
        {
            name = r.Name,
            category = r.Category.ToString(),
            severity = r.Severity.ToString(),
            message = r.Message,
            detail = r.Detail,
            durationMs = r.Duration?.TotalMilliseconds,
            metadata = r.Metadata
        });
        Console.WriteLine(JsonSerializer.Serialize(jsonResults, new JsonSerializerOptions { WriteIndented = true }));
        return results.Any(r => r.IsCritical) ? 1 : 0;
    }

    Console.WriteLine();

    // Group results by category
    var grouped = results.GroupBy(r => r.Category).OrderBy(g => (int)g.Key);
    
    foreach (var group in grouped)
    {
        WriteDiagnosticCategoryHeader(group.Key);
        
        foreach (var result in group)
        {
            WriteDiagnosticResult(result, verbose);
        }
        
        Console.WriteLine();
    }

    // Summary
    var errorCount = results.Count(r => r.Severity == DiagnosticSeverity.Error);
    var warningCount = results.Count(r => r.Severity == DiagnosticSeverity.Warning);
    var okCount = results.Count(r => r.Severity == DiagnosticSeverity.Ok);
    var infoCount = results.Count(r => r.Severity == DiagnosticSeverity.Info);
    var skippedCount = results.Count(r => r.Severity == DiagnosticSeverity.Skipped);

    Console.WriteLine(new string('─', 64));
    Console.Write("Summary: ");
    
    if (okCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{okCount} OK  ");
    }
    if (infoCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write($"{infoCount} Info  ");
    }
    if (warningCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{warningCount} Warning  ");
    }
    if (errorCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"{errorCount} Error  ");
    }
    if (skippedCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{skippedCount} Skipped");
    }
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine();

    if (errorCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("✗ Some critical checks failed. Address the errors above, then rerun 'koware doctor'.");
        Console.ResetColor();
        return 1;
    }

    if (warningCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠ Some checks have warnings. Review the items above for potential issues.");
        Console.ResetColor();
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("✓ All systems look good!");
    Console.ResetColor();
    return 0;
}

/// <summary>
/// Print help for the doctor command.
/// </summary>
static void PrintDoctorHelp()
{
    Console.WriteLine("Koware Doctor - Comprehensive System Diagnostics");
    Console.WriteLine();
    Console.WriteLine("Usage: koware doctor [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --category, -c <name>  Run only a specific category");
    Console.WriteLine("  --verbose, -v          Show detailed output including metadata and timing");
    Console.WriteLine("  --json                 Output results as JSON for scripting");
    Console.WriteLine("  --help, -h             Show this help message");
    Console.WriteLine();
    Console.WriteLine("Categories:");
    Console.WriteLine("  env        Environment (OS, runtime, disk space, memory, processor)");
    Console.WriteLine("  terminal   Terminal (color support, unicode, shell, encoding)");
    Console.WriteLine("  config     Configuration (config file validation, provider settings)");
    Console.WriteLine("  storage    Storage (database health, permissions, size, tables)");
    Console.WriteLine("  data       Data Integrity (history validation, orphaned downloads, duplicates)");
    Console.WriteLine("  network    Network (connectivity, DNS resolution, HTTPS)");
    Console.WriteLine("  security   Security (SSL/TLS, proxy, permissions, provider security)");
    Console.WriteLine("  providers  Providers (DNS, HTTP, API validation for anime/manga)");
    Console.WriteLine("  tools      Toolchain (ffmpeg, yt-dlp, aria2c)");
    Console.WriteLine("  updates    Updates (check for newer versions)");
    Console.WriteLine("  engine     Engine (core functionality: assemblies, I/O, HTTP, threading)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  koware doctor                     Run all diagnostics");
    Console.WriteLine("  koware doctor -c network          Check network only");
    Console.WriteLine("  koware doctor -c security         Check security settings");
    Console.WriteLine("  koware doctor --verbose           Show detailed results with timing");
    Console.WriteLine("  koware doctor --json              Output as JSON for automation");
}

/// <summary>
/// Parse a category name string to DiagnosticCategory enum.
/// </summary>
static DiagnosticCategory? ParseDiagnosticCategory(string name) => name.ToLowerInvariant() switch
{
    "env" or "environment" => DiagnosticCategory.Environment,
    "terminal" or "term" => DiagnosticCategory.Terminal,
    "config" or "configuration" => DiagnosticCategory.Configuration,
    "storage" => DiagnosticCategory.Storage,
    "data" or "integrity" => DiagnosticCategory.Data,
    "network" or "net" => DiagnosticCategory.Network,
    "security" or "sec" => DiagnosticCategory.Security,
    "providers" or "provider" => DiagnosticCategory.Providers,
    "tools" or "toolchain" => DiagnosticCategory.Toolchain,
    "updates" or "update" => DiagnosticCategory.Updates,
    "engine" => DiagnosticCategory.Engine,
    _ => null
};

/// <summary>
/// Write a category header for diagnostic output.
/// </summary>
static void WriteDiagnosticCategoryHeader(DiagnosticCategory category)
{
    var name = category switch
    {
        DiagnosticCategory.Environment => "Environment",
        DiagnosticCategory.Terminal => "Terminal",
        DiagnosticCategory.Configuration => "Configuration",
        DiagnosticCategory.Storage => "Storage",
        DiagnosticCategory.Data => "Data Integrity",
        DiagnosticCategory.Network => "Network",
        DiagnosticCategory.Security => "Security",
        DiagnosticCategory.Providers => "Providers",
        DiagnosticCategory.Toolchain => "Toolchain",
        DiagnosticCategory.Updates => "Updates",
        DiagnosticCategory.Engine => "Engine",
        _ => category.ToString()
    };

    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"┌─ {name} ─────────────────────────────────────────────────");
    Console.ResetColor();
}

/// <summary>
/// Write a single diagnostic result line.
/// </summary>
static void WriteDiagnosticResult(DiagnosticResult result, bool verbose)
{
    var (icon, color) = result.Severity switch
    {
        DiagnosticSeverity.Ok => ("✓", ConsoleColor.Green),
        DiagnosticSeverity.Warning => ("⚠", ConsoleColor.Yellow),
        DiagnosticSeverity.Error => ("✗", ConsoleColor.Red),
        DiagnosticSeverity.Skipped => ("○", ConsoleColor.DarkGray),
        DiagnosticSeverity.Info => ("ℹ", ConsoleColor.Blue),
        _ => ("?", ConsoleColor.Gray)
    };

    Console.Write("│ ");
    Console.ForegroundColor = color;
    Console.Write($"{icon} ");
    Console.ResetColor();
    
    Console.Write($"{result.Name,-24}");
    
    Console.ForegroundColor = color;
    Console.Write(result.Message ?? "-");
    Console.ResetColor();

    if (!string.IsNullOrWhiteSpace(result.Detail))
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  ({result.Detail})");
        Console.ResetColor();
    }

    if (result.Duration.HasValue && verbose)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  [{result.Duration.Value.TotalMilliseconds:F0}ms]");
        Console.ResetColor();
    }

    Console.WriteLine();

    // Show metadata if verbose
    if (verbose && result.Metadata?.Count > 0)
    {
        foreach (var (key, value) in result.Metadata)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"│     {key}: {value}");
            Console.ResetColor();
        }
    }
}

/// <summary>
/// Implement the <c>koware update</c> command (Windows only).
/// </summary>
/// <param name="args">CLI arguments; supports --check, --force, --help flags.</param>
/// <param name="logger">Logger for progress and errors.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success, 1 on failure, 2 if update available (with --check).</returns>
/// <remarks>
/// Queries GitHub Releases for the latest Koware installer.
/// Prints current vs latest version, downloads the installer, and launches it.
/// 
/// Flags:
///   --check, -c   Check for updates without downloading
///   --force, -f   Download even if already on the latest version
///   --help, -h    Show help message
/// </remarks>
static async Task<int> HandleUpdateAsync(string[] args, ILogger logger, CancellationToken cancellationToken)
{
    // Parse flags
    var checkOnly = args.Contains("--check", StringComparer.OrdinalIgnoreCase) 
                 || args.Contains("-c", StringComparer.OrdinalIgnoreCase);
    var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase)
             || args.Contains("-f", StringComparer.OrdinalIgnoreCase);
    var showHelp = args.Contains("--help", StringComparer.OrdinalIgnoreCase)
                || args.Contains("-h", StringComparer.OrdinalIgnoreCase);

    if (showHelp)
    {
        Console.WriteLine("Usage: koware update [options]");
        Console.WriteLine();
        Console.WriteLine("Check for updates and download the latest version of Koware.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -c, --check    Check for updates without downloading");
        Console.WriteLine("  -f, --force    Download even if already on the latest version");
        Console.WriteLine("  -h, --help     Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  koware update           Download and install the latest version");
        Console.WriteLine("  koware update --check   Check if an update is available");
        Console.WriteLine("  koware update --force   Force re-download of the latest version");
        return 0;
    }

    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("The 'update' command is only available on Windows.");
        Console.WriteLine("Please download the latest release manually from:");
        Console.WriteLine("  https://github.com/S1mplector/Koware/releases");
        return 1;
    }

    var current = GetVersionLabel();
    string? latestLabel = null;
    try
    {
        var latest = await KowareUpdater.GetLatestVersionAsync(cancellationToken);
        latestLabel = !string.IsNullOrWhiteSpace(latest.Tag) ? latest.Tag : latest.Name;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to query latest Koware release information.");
    }

    var currentLabel = !string.IsNullOrWhiteSpace(current) ? current : "unknown";
    var latestDisplay = !string.IsNullOrWhiteSpace(latestLabel) ? latestLabel : "unknown";
    var currentCore = TryParseVersionCore(current);
    var latestCore = TryParseVersionCore(latestLabel);
    var isUpToDate = currentCore is not null && latestCore is not null && currentCore >= latestCore;

    Console.WriteLine($"Current version: {currentLabel}");
    Console.WriteLine($"Latest version:  {latestDisplay}");
    Console.WriteLine();

    if (isUpToDate && !force)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("You are already running the latest version of Koware.");
        Console.ResetColor();
        return 0;
    }

    if (!isUpToDate)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("A new version is available!");
        Console.ResetColor();
    }

    if (checkOnly)
    {
        if (!isUpToDate)
        {
            Console.WriteLine();
            Console.WriteLine("Run 'koware update' to download and install.");
        }
        return isUpToDate ? 0 : 2; // Exit code 2 = update available
    }

    if (force && isUpToDate)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Forcing update (already on latest version)...");
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.Write("Do you want to download and install? [y/N] ");
    var response = Console.ReadLine()?.Trim();
    if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Update cancelled.");
        return 0;
    }

    Console.WriteLine("Checking for the latest Koware installer...");

    var lastMessage = string.Empty;
    var progress = new Progress<string>(message =>
    {
        // Only update console when message changes (avoid spamming same percentage)
        if (message == lastMessage) return;
        lastMessage = message;

        // For download progress, overwrite the same line
        if (message.StartsWith("Downloaded"))
        {
            Console.Write($"\r{message,-40}");
        }
        else
        {
            Console.WriteLine(message);
        }

        logger.LogInformation("{Message}", message);
    });

    var result = await KowareUpdater.DownloadAndRunLatestInstallerAsync(progress, cancellationToken);

    // Ensure we move to a new line after progress overwrites
    Console.WriteLine();

    if (!result.Success)
    {
        var description = result.Error ?? "Unknown error";
        logger.LogError("Update failed: {Error}", description);
        Console.WriteLine($"Update failed: {description}");
        return 1;
    }

    // Log successful update details
    logger.LogInformation(
        "Update downloaded from release {ReleaseTag} ({ReleaseName}) asset {AssetName}.",
        result.ReleaseTag ?? "(unknown)",
        result.ReleaseName ?? "(unknown)",
        result.AssetName ?? "(unknown)");

    // Provide user-friendly output based on what happened
    Console.WriteLine();
    Console.WriteLine($"Release: {result.ReleaseTag ?? result.ReleaseName ?? "latest"}");
    
    if (!string.IsNullOrEmpty(result.ExtractPath))
    {
        Console.WriteLine($"Extracted to: {result.ExtractPath}");
    }

    if (result.InstallerLaunched)
    {
        Console.WriteLine();
        Console.WriteLine("Installer launched successfully!");
        Console.WriteLine("Follow the installer GUI to complete the update.");
        
        if (!string.IsNullOrEmpty(result.InstallerPath))
        {
            logger.LogInformation("Launched installer: {InstallerPath}", result.InstallerPath);
        }
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("No installer found in the release package.");
        Console.WriteLine("The update folder has been opened in Explorer.");
        Console.WriteLine("To complete the update manually:");
        Console.WriteLine("  1. Close all Koware processes");
        Console.WriteLine("  2. Copy the new files to your Koware installation folder");
        Console.WriteLine("  3. Restart Koware");
    }

    return 0;
}

/// <summary>
/// Implement the <c>koware provider</c> command.
/// </summary>
/// <param name="args">CLI arguments; supports subcommands: list, add, edit, init, test, --enable, --disable.</param>
/// <param name="services">Service provider for provider toggle options.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// Subcommands:
/// - list: Show all providers with configuration status
/// - add [name]: Interactive wizard to configure a provider  
/// - edit: Open config file in default editor
/// - init: Create template configuration file
/// - test [name]: Test provider connectivity
/// - --enable/--disable: Toggle provider on/off
/// </remarks>
static Task<int> HandleProviderAsync(string[] args, IServiceProvider services)
{
    var allAnime = services.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
    var allManga = services.GetRequiredService<IOptions<AllMangaOptions>>().Value;
    var configPath = GetUserConfigFilePath();

    // Parse subcommand
    var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "list";

    switch (subcommand)
    {
        case "list":
            return Task.FromResult(HandleProviderList(allAnime, allManga));
            
        case "add":
            var providerToAdd = args.Length > 2 ? args[2] : null;
            return HandleProviderAddAsync(providerToAdd, configPath);
            
        case "edit":
            return Task.FromResult(HandleProviderEdit(configPath));
            
        case "init":
            return Task.FromResult(HandleProviderInit(configPath));
            
        case "test":
            var providerToTest = args.Length > 2 ? args[2] : null;
            return HandleProviderTestAsync(providerToTest, allAnime, allManga);
            
        case "autoconfig":
        case "auto":
            var providerToAuto = args.Length > 2 ? args[2] : null;
            var listOnly = args.Skip(2).Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase));
            return HandleProviderAutoConfigAsync(providerToAuto, args.Skip(2).ToArray(), listOnly, configPath, services);
            
        case "--enable":
        case "--disable":
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: koware provider --enable <name> | --disable <name>");
                return Task.FromResult(1);
            }
            return Task.FromResult(HandleProviderToggle(args[2], subcommand == "--enable", configPath));
            
        case "help":
        case "--help":
        case "-h":
            PrintProviderHelp();
            return Task.FromResult(0);
            
        default:
            // Legacy: if arg looks like a provider name, show its status
            PrintProviderHelp();
            return Task.FromResult(1);
    }
}

/// <summary>
/// List all providers with their configuration status.
/// </summary>
static int HandleProviderList(AllAnimeOptions allAnime, AllMangaOptions allManga)
{
    Console.WriteLine("Provider Status:");
    Console.WriteLine(new string('─', 60));
    
    var providers = new[]
    {
        ("AllAnime", allAnime.IsConfigured, allAnime.Enabled, allAnime.ApiBase, "Anime"),
        ("AllManga", allManga.IsConfigured, allManga.Enabled, allManga.ApiBase, "Manga"),
    };
    
    foreach (var (name, isConfigured, isEnabled, apiBase, type) in providers)
    {
        Console.Write($"  {name,-12} ");
        
        if (!isConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"{Icons.Error} Not configured");
            Console.ResetColor();
        }
        else if (!isEnabled)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("○ Disabled     ");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{Icons.Success} Ready        ");
            Console.ResetColor();
        }
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($" [{type}]");
        if (!string.IsNullOrWhiteSpace(apiBase))
        {
            Console.Write($" {apiBase}");
        }
        Console.ResetColor();
        Console.WriteLine();
    }
    
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  koware provider add <name>    Configure a provider");
    Console.WriteLine("  koware provider edit          Open config file");
    Console.WriteLine("  koware provider init          Create template config");
    Console.WriteLine("  koware provider test [name]   Test connectivity");
    
    return 0;
}

/// <summary>
/// Interactive wizard to add/configure a provider.
/// </summary>
static Task<int> HandleProviderAddAsync(string? providerName, string configPath)
{
    var validProviders = new[] { "allanime", "allmanga" };
    
    if (string.IsNullOrWhiteSpace(providerName))
    {
        Console.WriteLine("Available providers:");
        foreach (var p in validProviders)
        {
            Console.WriteLine($"  - {p}");
        }
        Console.WriteLine();
        Console.Write("Enter provider name: ");
        providerName = Console.ReadLine()?.Trim().ToLowerInvariant();
    }
    else
    {
        providerName = providerName.ToLowerInvariant();
    }
    
    if (string.IsNullOrWhiteSpace(providerName) || !validProviders.Contains(providerName))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid provider. Choose from: {string.Join(", ", validProviders)}");
        Console.ResetColor();
        return Task.FromResult(1);
    }
    
    // Load existing config
    var json = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();
    
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Configuring {providerName}...");
    Console.ResetColor();
    Console.WriteLine("(Leave blank to skip optional fields)");
    Console.WriteLine();
    
    var providerNode = new JsonObject();
    
    // Common fields
    Console.Write("API Base URL: ");
    var apiBase = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(apiBase))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("API Base URL is required.");
        Console.ResetColor();
        return Task.FromResult(1);
    }
    providerNode["ApiBase"] = apiBase;
    
    Console.Write("Base Host (e.g., example.com): ");
    var baseHost = Console.ReadLine()?.Trim();
    if (!string.IsNullOrWhiteSpace(baseHost))
    {
        providerNode["BaseHost"] = baseHost;
    }
    
    Console.Write("Referer URL: ");
    var referer = Console.ReadLine()?.Trim();
    if (!string.IsNullOrWhiteSpace(referer))
    {
        providerNode["Referer"] = referer;
    }
    
    Console.Write("Translation type (sub/dub) [sub]: ");
    var transType = Console.ReadLine()?.Trim();
    providerNode["TranslationType"] = string.IsNullOrWhiteSpace(transType) ? "sub" : transType;
    
    Console.Write("Enable this provider? [Y/n]: ");
    var enableInput = Console.ReadLine()?.Trim();
    var enabled = string.IsNullOrWhiteSpace(enableInput) || enableInput.Equals("y", StringComparison.OrdinalIgnoreCase) || enableInput.Equals("yes", StringComparison.OrdinalIgnoreCase);
    providerNode["Enabled"] = enabled;
    
    // Map to correct section name
    var sectionName = providerName switch
    {
        "allanime" => "AllAnime",
        "allmanga" => "AllManga",
        _ => providerName
    };
    
    json[sectionName] = providerNode;
    
    // Ensure directory exists
    var configDir = Path.GetDirectoryName(configPath);
    if (!string.IsNullOrWhiteSpace(configDir) && !Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir);
    }
    
    File.WriteAllText(configPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"{Icons.Success} Provider '{sectionName}' configured successfully!");
    Console.ResetColor();
    Console.WriteLine($"Config saved to: {configPath}");
    
    return Task.FromResult(0);
}

/// <summary>
/// Open the config file in the default editor.
/// </summary>
static int HandleProviderEdit(string configPath)
{
    // Ensure file exists with at least empty JSON
    var configDir = Path.GetDirectoryName(configPath);
    if (!string.IsNullOrWhiteSpace(configDir) && !Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir);
    }
    
    if (!File.Exists(configPath))
    {
        File.WriteAllText(configPath, "{\n}\n");
    }
    
    Console.WriteLine($"Opening: {configPath}");
    
    try
    {
        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", $"-e \"{configPath}\"");
        }
        else if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = configPath, UseShellExecute = true });
        }
        else
        {
            // Linux - try common editors
            var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "nano";
            Process.Start(editor, configPath);
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed to open editor: {ex.Message}");
        Console.ResetColor();
        Console.WriteLine($"Manually edit: {configPath}");
        return 1;
    }
}

/// <summary>
/// Create a template configuration file.
/// </summary>
static int HandleProviderInit(string configPath)
{
    if (File.Exists(configPath))
    {
        Console.Write($"Config file already exists at {configPath}. Overwrite? [y/N]: ");
        var confirm = Console.ReadLine()?.Trim();
        if (!confirm?.Equals("y", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }
    }
    
    var template = new JsonObject
    {
        ["AllAnime"] = new JsonObject
        {
            ["Enabled"] = false,
            ["BaseHost"] = "your-host.example",
            ["ApiBase"] = "https://api.your-host.example",
            ["Referer"] = "https://your-host.example",
            ["TranslationType"] = "sub"
        },
        ["AllManga"] = new JsonObject
        {
            ["Enabled"] = false,
            ["BaseHost"] = "your-manga-host.example",
            ["ApiBase"] = "https://api.your-manga-host.example",
            ["Referer"] = "https://your-manga-host.example",
            ["TranslationType"] = "sub"
        }
    };
    
    var configDir = Path.GetDirectoryName(configPath);
    if (!string.IsNullOrWhiteSpace(configDir) && !Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir);
    }
    
    File.WriteAllText(configPath, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
    
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"{Icons.Success} Template config created: {configPath}");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("Next steps:");
    Console.WriteLine("  1. Edit the config file with your source URLs");
    Console.WriteLine("  2. Set 'Enabled' to true for sources you want to use");
    Console.WriteLine("  3. Run 'koware provider list' to verify");
    
    return 0;
}

/// <summary>
/// Test provider connectivity.
/// </summary>
static async Task<int> HandleProviderTestAsync(string? providerName, AllAnimeOptions allAnime, AllMangaOptions allManga)
{
    var providers = new Dictionary<string, (bool configured, string? apiBase, string? referer, string? userAgent)>(StringComparer.OrdinalIgnoreCase)
    {
        ["allanime"] = (allAnime.IsConfigured, allAnime.ApiBase, allAnime.Referer, allAnime.UserAgent),
        ["allmanga"] = (allManga.IsConfigured, allManga.ApiBase, allManga.Referer, allManga.UserAgent),
    };
    
    var toTest = string.IsNullOrWhiteSpace(providerName)
        ? providers.Keys.ToList()
        : new List<string> { providerName.ToLowerInvariant() };
    
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var allPassed = true;
    
    foreach (var name in toTest)
    {
        if (!providers.TryGetValue(name, out var info))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unknown provider: {name}");
            Console.ResetColor();
            continue;
        }
        
        Console.Write($"Testing {name}... ");
        
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(info.apiBase)) missing.Add("ApiBase");
        if (string.IsNullOrWhiteSpace(info.referer)) missing.Add("Referer");
        if (string.IsNullOrWhiteSpace(info.userAgent)) missing.Add("UserAgent");

        if (!info.configured || missing.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            if (missing.Count > 0)
            {
                Console.WriteLine($"{Icons.Warning} Not configured (missing: {string.Join(", ", missing)})");
            }
            else
            {
                Console.WriteLine($"{Icons.Warning} Not configured");
            }
            Console.ResetColor();
            continue;
        }
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, info.apiBase);
            if (!string.IsNullOrWhiteSpace(info.referer))
                request.Headers.TryAddWithoutValidation("Referer", info.referer);
            if (!string.IsNullOrWhiteSpace(info.userAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", info.userAgent);
            
            var response = await http.SendAsync(request);
            var code = (int)response.StatusCode;
            
            // Success, BadRequest (API needs query), or Cloudflare codes (520-529) = reachable
            if (response.IsSuccessStatusCode || code == 400 || (code >= 520 && code <= 529))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                if (code >= 520)
                    Console.WriteLine($"{Icons.Success} Reachable (Cloudflare {code})");
                else
                    Console.WriteLine($"{Icons.Success} Connected ({code})");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{Icons.Error} HTTP {code}");
                Console.ResetColor();
                if (code is 401 or 403)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  Hint: Access denied. This is commonly caused by a wrong/missing Referer/Origin header or a blocked User-Agent.");
                    Console.WriteLine("  Try: koware provider autoconfig  (or verify ApiBase + Referer in appsettings.user.json)");
                    Console.ResetColor();
                }
                else if (code == 429)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  Hint: Rate limited (HTTP 429). Wait a bit, reduce SearchLimit, or rotate provider hosts.");
                    Console.ResetColor();
                }
                allPassed = false;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Icons.Error} {ex.Message}");
            Console.ResetColor();
            allPassed = false;
        }
    }
    
    return allPassed ? 0 : 1;
}

/// <summary>
/// Auto-configure providers - either from a URL (intelligent analysis) or from remote manifest.
/// </summary>
static async Task<int> HandleProviderAutoConfigAsync(string? providerName, string[] subArgs, bool listOnly, string configPath, IServiceProvider services)
{
    // Check if this is a URL - use intelligent autoconfig
    if (!string.IsNullOrWhiteSpace(providerName) && 
        (providerName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         providerName.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         providerName.Contains(".")))
    {
        return await HandleUrlAutoconfigAsync(providerName, subArgs, services);
    }
    
    // Otherwise use remote manifest approach
    return await HandleRemoteManifestAutoconfigAsync(providerName, listOnly, configPath);
}

/// <summary>
/// Intelligent autoconfig - analyze a website URL and generate provider config.
/// </summary>
static async Task<int> HandleUrlAutoconfigAsync(string urlString, string[] args, IServiceProvider services)
{
    // Normalize URL
    if (!urlString.StartsWith("http", StringComparison.OrdinalIgnoreCase))
    {
        urlString = "https://" + urlString;
    }
    
    if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{Icons.Error} Invalid URL: {urlString}");
        Console.ResetColor();
        return 1;
    }
    
    // Parse options
    var customName = GetArgValue(args, "--name");
    var testQuery = GetArgValue(args, "--test-query");
    var skipValidation = args.Any(a => a.Equals("--skip-validation", StringComparison.OrdinalIgnoreCase));
    var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
    var forceType = GetArgValue(args, "--type");
    
    var options = new AutoconfigOptions
    {
        ProviderName = customName,
        TestQuery = testQuery,
        SkipValidation = skipValidation,
        DryRun = dryRun,
        ForceType = forceType?.ToLowerInvariant() switch
        {
            "anime" => ProviderType.Anime,
            "manga" => ProviderType.Manga,
            "both" => ProviderType.Both,
            _ => null
        }
    };
    
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  {Icons.Warning} EXPERIMENTAL FEATURE");
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine("  URL-based autoconfig is experimental. Provider configs will be saved");
    Console.WriteLine("  but are NOT yet integrated with koware watch/read commands.");
    Console.WriteLine("  This feature is for testing and development purposes only.");
    Console.ResetColor();
    Console.WriteLine();
    
    Console.Write("  Do you want to continue? [y/N]: ");
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input) || !input.Equals("y", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  Cancelled.");
        return 0;
    }
    
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  Analyzing {url.Host}...");
    Console.ResetColor();
    Console.WriteLine();
    
    var orchestrator = services.GetRequiredService<IAutoconfigOrchestrator>();
    
    // Progress display
    var currentPhase = "";
    var progress = new Progress<AutoconfigProgress>(p =>
    {
        if (p.Phase != currentPhase)
        {
            currentPhase = p.Phase;
            Console.Write($"  [{p.Phase}] ");
        }
        
        if (p.Succeeded.HasValue)
        {
            if (p.Succeeded.Value)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"{Icons.Success} {p.Step}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{Icons.Error} {p.Step}");
            }
            Console.ResetColor();
        }
    });
    
    try
    {
        var result = await orchestrator.AnalyzeAndConfigureAsync(url, options, progress);
        
        Console.WriteLine();
        
        if (result.IsSuccess && result.Config != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{Icons.Success} Provider '{result.Config.Name}' created successfully!");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"  Slug:      {result.Config.Slug}");
            Console.WriteLine($"  Type:      {result.Config.Type}");
            Console.WriteLine($"  Base Host: {result.Config.Hosts.BaseHost}");
            Console.WriteLine($"  Duration:  {result.Duration.TotalSeconds:F1}s");
            
            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Warnings:");
                Console.ResetColor();
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"  {Icons.Warning} {warning}");
                }
            }
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Note: This provider config is saved but NOT yet integrated with");
            Console.WriteLine("      koware watch/read commands. Full integration coming soon.");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"Config saved to: ~/.config/koware/providers/custom/{result.Config.Slug}.json");
            
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Icons.Error} Autoconfig failed: {result.ErrorMessage}");
            Console.ResetColor();
            
            if (result.Phases.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Phases completed:");
                foreach (var phase in result.Phases)
                {
                    var icon = phase.Succeeded ? Icons.Success : Icons.Error;
                    Console.WriteLine($"  {icon} {phase.Name}: {phase.Message}");
                }
            }
            
            return 1;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{Icons.Error} Error: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
}

static string? GetArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}

/// <summary>
/// Auto-configure providers from the remote koware-providers repository.
/// </summary>
static async Task<int> HandleRemoteManifestAutoconfigAsync(string? providerName, bool listOnly, string configPath)
{
    const string repoBase = "https://raw.githubusercontent.com/S1mplector/koware-providers/main";
    const string manifestUrl = $"{repoBase}/providers.json";
    
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "Koware-CLI");
    
    // Fetch manifest
    Console.Write("Fetching provider manifest... ");
    JsonObject manifest;
    try
    {
        var manifestJson = await http.GetStringAsync(manifestUrl);
        manifest = JsonNode.Parse(manifestJson) as JsonObject ?? throw new Exception("Invalid manifest format");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{Icons.Success}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{Icons.Error} {ex.Message}");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Could not fetch provider manifest. Check your internet connection.");
        Console.WriteLine("Alternatively, configure providers manually with 'koware provider add <name>'.");
        return 1;
    }
    
    var providers = manifest["providers"] as JsonObject;
    if (providers is null)
    {
        Console.WriteLine("No providers found in manifest.");
        return 1;
    }
    
    // List mode
    if (listOnly || providerName == "--list")
    {
        Console.WriteLine();
        Console.WriteLine("Available remote providers:");
        Console.WriteLine(new string('─', 50));
        foreach (var (key, value) in providers)
        {
            var info = value as JsonObject;
            var name = info?["name"]?.GetValue<string>() ?? key;
            var type = info?["type"]?.GetValue<string>() ?? "unknown";
            var desc = info?["description"]?.GetValue<string>() ?? "";
            Console.WriteLine($"  {name,-12} [{type,-6}] {desc}");
        }
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  koware provider autoconfig <name>           Configure from remote manifest");
        Console.WriteLine("  koware provider autoconfig <url>            Analyze website and generate config");
        return 0;
    }
    
    // Determine which providers to configure
    var toConfig = string.IsNullOrWhiteSpace(providerName)
        ? providers.Select(p => p.Key).ToList()
        : new List<string> { providerName.ToLowerInvariant() };
    
    // Load existing config
    var configJson = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();
    
    var configured = 0;
    
    foreach (var key in toConfig)
    {
        if (!providers.ContainsKey(key))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Icons.Warning} Unknown provider: {key}");
            Console.ResetColor();
            continue;
        }
        
        var providerInfo = providers[key] as JsonObject;
        var configFile = providerInfo?["config"]?.GetValue<string>();
        var displayName = providerInfo?["name"]?.GetValue<string>() ?? key;
        
        if (string.IsNullOrWhiteSpace(configFile))
        {
            Console.WriteLine($"{Icons.Warning} No config file for {displayName}");
            continue;
        }
        
        Console.Write($"Configuring {displayName}... ");
        
        try
        {
            var providerConfigJson = await http.GetStringAsync($"{repoBase}/{configFile}");
            var providerConfig = JsonNode.Parse(providerConfigJson) as JsonObject;
            var config = providerConfig?["config"] as JsonObject;
            
            if (config is null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"{Icons.Warning} Invalid config format");
                Console.ResetColor();
                continue;
            }
            
            // Map to section name
            var sectionName = key switch
            {
                "allanime" => "AllAnime",
                "allmanga" => "AllManga",
                _ => displayName
            };
            
            // Merge config (preserves user overrides for fields not in remote)
            var existingSection = configJson[sectionName] as JsonObject ?? new JsonObject();
            foreach (var (field, value) in config)
            {
                existingSection[field] = value?.DeepClone();
            }
            configJson[sectionName] = existingSection;
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{Icons.Success}");
            Console.ResetColor();
            configured++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Icons.Error} {ex.Message}");
            Console.ResetColor();
        }
    }
    
    if (configured > 0)
    {
        // Save config
        var configDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(configDir) && !Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
        }
        
        File.WriteAllText(configPath, JsonSerializer.Serialize(configJson, new JsonSerializerOptions { WriteIndented = true }));
        
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{Icons.Success} Configured {configured} provider(s).");
        Console.ResetColor();
        Console.WriteLine($"  Config saved to: {configPath}");
        Console.WriteLine();
        Console.WriteLine("Test connectivity with: koware provider test");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("No providers were configured.");
    }
    
    return configured > 0 ? 0 : 1;
}

/// <summary>
/// Toggle provider enabled/disabled state.
/// </summary>
static int HandleProviderToggle(string target, bool enable, string configPath)
{
    var json = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();
    
    // Map provider name to section
    var sectionName = target.ToLowerInvariant() switch
    {
        "allanime" => "AllAnime",
        "allmanga" => "AllManga",
        _ => target
    };
    
    var providerNode = json[sectionName] as JsonObject ?? new JsonObject();
    providerNode["Enabled"] = enable;
    json[sectionName] = providerNode;
    
    var configDir = Path.GetDirectoryName(configPath);
    if (!string.IsNullOrWhiteSpace(configDir) && !Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir);
    }
    
    File.WriteAllText(configPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    
    Console.ForegroundColor = enable ? ConsoleColor.Green : ConsoleColor.Yellow;
    Console.WriteLine($"{(enable ? $"{Icons.Success} Enabled" : "[-] Disabled")} provider '{sectionName}'.");
    Console.ResetColor();
    
    return 0;
}

/// <summary>
/// Print help for the provider command.
/// </summary>
static void PrintProviderHelp()
{
    Console.WriteLine("Usage: koware provider <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  list                  Show all providers and their status (default)");
    Console.WriteLine("  autoconfig <url>      Analyze a website and generate provider config");
    Console.WriteLine("  autoconfig [name]     Auto-configure from remote repository");
    Console.WriteLine("  add [name]            Configure a provider interactively");
    Console.WriteLine("  edit                  Open config file in default editor");
    Console.WriteLine("  init                  Create template configuration file");
    Console.WriteLine("  test [name]           Test provider connectivity");
    Console.WriteLine("  --enable <name>       Enable a provider");
    Console.WriteLine("  --disable <name>      Disable a provider");
    Console.WriteLine();
    Console.WriteLine("Autoconfig from URL (intelligent analysis):");
    Console.WriteLine("  koware provider autoconfig https://example-anime.com");
    Console.WriteLine("  koware provider autoconfig mangadex.org --name \"MangaDex\"");
    Console.WriteLine();
    Console.WriteLine("  Options:");
    Console.WriteLine("    --name <name>       Custom provider name");
    Console.WriteLine("    --type <anime|manga> Force content type detection");
    Console.WriteLine("    --test-query <q>    Custom search query for validation");
    Console.WriteLine("    --skip-validation   Skip the live testing phase");
    Console.WriteLine("    --dry-run           Analyze without saving config");
    Console.WriteLine();
    Console.WriteLine("Autoconfig from remote manifest:");
    Console.WriteLine("  koware provider autoconfig              # Configure all providers");
    Console.WriteLine("  koware provider autoconfig allanime     # Configure specific provider");
    Console.WriteLine("  koware provider autoconfig --list       # List available providers");
    Console.WriteLine();
    Console.WriteLine("Other examples:");
    Console.WriteLine("  koware provider list");
    Console.WriteLine("  koware provider test");
}

/// <summary>
/// Implement the <c>koware continue</c> command: resume watching/reading from history.
/// </summary>
/// <param name="args">CLI arguments; optional query, --from, and --quality (anime) or --chapter (manga).</param>
/// <param name="services">Service provider for history and orchestrator.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for quality fallback and mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code from playback/reading.</returns>
/// <remarks>
/// Mode-aware: continues anime (next episode) or manga (next chapter) based on current mode.
/// Finds the most recent history entry (or matches by title),
/// then plays/reads the next episode/chapter (or a specific one via --from/--chapter).
/// </remarks>
static async Task<int> HandleContinueAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var mode = defaults.GetMode();

    if (mode == CliMode.Manga)
    {
        return await HandleContinueMangaAsync(args, services, logger, cancellationToken);
    }

    // Anime mode (default)
    string? animeQuery;
    int? fromEpisode = null;
    string? preferredQuality = null;

    var queryParts = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var parsedFrom))
            {
                logger.LogWarning("Episode number for --from must be an integer.");
                PrintUsage();
                return 1;
            }

            fromEpisode = parsedFrom;
            i++;
            continue;
        }

        if (arg.Equals("--quality", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            preferredQuality = args[i + 1];
            i++;
            continue;
        }

        queryParts.Add(arg);
    }

    animeQuery = queryParts.Count == 0 ? null : string.Join(' ', queryParts).Trim();

    var history = services.GetRequiredService<IWatchHistoryStore>();
    var entry = await ResolveWatchHistoryAsync(history, animeQuery, logger, cancellationToken);

    if (entry is null)
    {
        logger.LogWarning("No watch history found to continue from.");
        return 1;
    }

    var targetEpisode = fromEpisode ?? (entry.EpisodeNumber + 1);
    if (targetEpisode <= 0)
    {
        targetEpisode = 1;
    }

    var quality = preferredQuality ?? entry.Quality;

    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();
    if (string.IsNullOrWhiteSpace(quality))
    {
        quality = defaults.Quality;
    }

    var plan = new ScrapePlan(entry.AnimeTitle, targetEpisode, quality);

    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
}

/// <summary>
/// Handle continue command in manga mode: resume reading from history.
/// </summary>
static async Task<int> HandleContinueMangaAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    string? mangaQuery = null;
    float? fromChapter = null;

    var queryParts = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if ((arg.Equals("--from", StringComparison.OrdinalIgnoreCase) || arg.Equals("--chapter", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
        {
            if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                fromChapter = parsed;
                i++;
                continue;
            }
            logger.LogWarning("Chapter number must be a number.");
            return 1;
        }

        queryParts.Add(arg);
    }

    mangaQuery = queryParts.Count == 0 ? null : string.Join(' ', queryParts).Trim();

    var readHistory = services.GetRequiredService<IReadHistoryStore>();
    var entry = await ResolveReadHistoryAsync(readHistory, mangaQuery, logger, cancellationToken);

    if (entry is null)
    {
        logger.LogWarning("No read history found to continue from.");
        return 1;
    }

    // If resuming from the same chapter (not explicitly overridden), use the last page
    // Otherwise, start at page 1 of the new chapter
    var targetChapter = fromChapter ?? entry.ChapterNumber;
    var startPage = (fromChapter == null && Math.Abs(targetChapter - entry.ChapterNumber) < 0.001f) ? entry.LastPage : 1;
    
    if (targetChapter <= 0)
    {
        targetChapter = 1;
    }

    if (startPage > 1)
    {
        logger.LogInformation("Resuming {Manga} chapter {Chapter} from page {Page}", entry.MangaTitle, targetChapter, startPage);
    }
    else
    {
        logger.LogInformation("Continuing {Manga} from chapter {Chapter}", entry.MangaTitle, targetChapter);
    }

    // Build args for HandleReadAsync with start page
    var readArgs = new List<string> { 
        "read", entry.MangaTitle, 
        "--chapter", targetChapter.ToString(System.Globalization.CultureInfo.InvariantCulture), 
        "--index", "1", 
        "--non-interactive",
        "--start-page", startPage.ToString()
    };
    var defaults = services.GetRequiredService<IOptions<DefaultCliOptions>>().Value;
    return await HandleReadAsync(readArgs.ToArray(), services, logger, defaults, cancellationToken);
}

/// <summary>
/// Implement the <c>koware history</c> command: browse, filter, and replay watch/read history.
/// </summary>
/// <param name="args">CLI arguments; supports search, --anime/--manga, --limit, --after, --before, --from, --to, --json, --stats, --play, --next.</param>
/// <param name="services">Service provider for history store and orchestrator.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for quality fallback and mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// Mode-aware: shows watch history (anime) or read history (manga) based on current mode.
/// With --stats, shows aggregated counts per anime/manga.
/// With --play N, replays the Nth entry in the filtered list.
/// With --next, plays/reads the next episode/chapter of the first matched entry.
/// </remarks>
static async Task<int> HandleHistoryAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var mode = defaults.GetMode();

    if (mode == CliMode.Manga)
    {
        return await HandleMangaHistoryAsync(args, services, logger, cancellationToken);
    }

    // Anime mode (default)
    var history = services.GetRequiredService<IWatchHistoryStore>();
    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();

    // Handle 'koware history clear' subcommand
    if (args.Length > 1 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        return await HandleHistoryClearAsync(args, history, cancellationToken);
    }

    string? search = null;
    int limit = 10;
    DateTimeOffset? after = null;
    DateTimeOffset? before = null;
    int? fromEpisode = null;
    int? toEpisode = null;
    bool json = false;
    bool stats = false;
    int? playIndex = null;
    bool playNext = false;
    bool browse = false;

    var idx = 1;
    if (args.Length > 1 && args[1].Equals("search", StringComparison.OrdinalIgnoreCase))
    {
        idx = 2;
        if (args.Length > 2)
        {
            search = string.Join(' ', args.Skip(2)).Trim();
        }
    }

    while (idx < args.Length)
    {
        var arg = args[idx];
        switch (arg.ToLowerInvariant())
        {
            case "--anime":
                if (idx + 1 >= args.Length) return UsageErrorWithReturn("Missing value for --anime");
                search = args[++idx];
                break;
            case "--limit":
                if (idx + 1 >= args.Length || !int.TryParse(args[idx + 1], out limit))
                    return UsageErrorWithReturn("Value for --limit must be an integer.");
                idx++;
                break;
            case "--after":
                if (idx + 1 >= args.Length || !DateTimeOffset.TryParse(args[idx + 1], out var parsedAfter))
                    return UsageErrorWithReturn("Value for --after must be a date or datetime.");
                after = parsedAfter;
                idx++;
                break;
            case "--before":
                if (idx + 1 >= args.Length || !DateTimeOffset.TryParse(args[idx + 1], out var parsedBefore))
                    return UsageErrorWithReturn("Value for --before must be a date or datetime.");
                before = parsedBefore;
                idx++;
                break;
            case "--from":
                if (idx + 1 >= args.Length || !int.TryParse(args[idx + 1], out var f))
                    return UsageErrorWithReturn("Value for --from must be an integer.");
                fromEpisode = f;
                idx++;
                break;
            case "--to":
                if (idx + 1 >= args.Length || !int.TryParse(args[idx + 1], out var t))
                    return UsageErrorWithReturn("Value for --to must be an integer.");
                toEpisode = t;
                idx++;
                break;
            case "--json":
                json = true;
                break;
            case "--stats":
                stats = true;
                break;
            case "--play":
                if (idx + 1 >= args.Length || !int.TryParse(args[idx + 1], out var pi) || pi < 1)
                    return UsageErrorWithReturn("Value for --play must be a positive integer.");
                playIndex = pi;
                idx++;
                break;
            case "--next":
                playNext = true;
                break;
            case "--browse":
            case "-b":
                browse = true;
                break;
        }
        idx++;
    }

    // Interactive mode when no flags (default)
    var hasFilters = search != null || after != null || before != null || fromEpisode != null || toEpisode != null || json || stats || playIndex.HasValue || playNext;
    if (!hasFilters && !browse)
    {
        var historyManager = new Koware.Cli.Console.InteractiveHistoryManager(
            history,
            onPlayAnime: async (entry, episode) =>
            {
                await LaunchFromHistory(entry, orchestrator, services, history, logger, defaults, episode, cancellationToken);
            },
            cancellationToken: cancellationToken);
        return await historyManager.RunAsync();
    }

    // Interactive browse mode
    if (browse)
    {
        var browseQuery = new HistoryQuery(search, after, before, fromEpisode, toEpisode, 50);
        var browseEntries = await history.QueryAsync(browseQuery, cancellationToken);

        if (browseEntries.Count == 0)
        {
            Console.WriteLine("No history matches your filters.");
            return 0;
        }

        // Group by anime and get latest entry per anime
        var grouped = browseEntries
            .GroupBy(e => e.AnimeTitle)
            .Select(g => g.OrderByDescending(e => e.WatchedAt).First())
            .OrderByDescending(e => e.WatchedAt)
            .ToList();

        var historyItems = grouped.Select(e => new HistoryItem
        {
            Title = e.AnimeTitle,
            LastEpisode = e.EpisodeNumber,
            TotalEpisodes = null,
            WatchedAt = e.WatchedAt,
            Provider = e.Provider,
            Quality = e.Quality
        }).ToList();

        var selected = InteractiveBrowser.BrowseHistory(historyItems);
        if (selected == null) return 0;

        // Find the original entry and replay
        var selectedEntry = browseEntries.First(e => e.AnimeTitle == selected.Title);
        return await LaunchFromHistory(selectedEntry, orchestrator, services, history, logger, defaults, selectedEntry.EpisodeNumber + 1, cancellationToken);
    }

    if (stats)
    {
        var statsResult = await history.GetStatsAsync(search, cancellationToken);
        if (json)
        {
            var payload = new
            {
                total = statsResult.Sum(s => s.Count),
                uniqueAnime = statsResult.Count,
                top = statsResult.Select(s => new { title = s.AnimeTitle, count = s.Count, lastWatched = s.LastWatched })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("History stats");
        Console.ResetColor();
        Console.WriteLine($"Total watches: {statsResult.Sum(s => s.Count)}");
        Console.WriteLine($"Unique anime : {statsResult.Count}");
        Console.WriteLine("Top shows:");
        foreach (var s in statsResult.Take(10))
        {
            Console.WriteLine($"  {s.AnimeTitle} ({s.Count} entries, last: {s.LastWatched:u})");
        }
        return 0;
    }

    var query = new HistoryQuery(search, after, before, fromEpisode, toEpisode, limit);
    var entries = await history.QueryAsync(query, cancellationToken);

    if (entries.Count == 0)
    {
        Console.WriteLine("No history matches your filters.");
        return 0;
    }

    if (json)
    {
        var payload = new
        {
            total = entries.Count,
            entries = entries.Select(e => new
            {
                animeId = e.AnimeId,
                animeTitle = e.AnimeTitle,
                episode = e.EpisodeNumber,
                episodeTitle = e.EpisodeTitle,
                quality = e.Quality,
                watchedAt = e.WatchedAt
            })
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    RenderHistory(entries);

    if (playNext)
    {
        var first = entries[0];
        return await LaunchFromHistory(first, orchestrator, services, history, logger, defaults, first.EpisodeNumber + 1, cancellationToken);
    }

    if (playIndex.HasValue)
    {
        var idxToPlay = playIndex.Value - 1;
        if (idxToPlay < 0 || idxToPlay >= entries.Count)
        {
            Console.WriteLine($"--play value out of range (1-{entries.Count}).");
            return 1;
        }
        var entry = entries[idxToPlay];
        return await LaunchFromHistory(entry, orchestrator, services, history, logger, defaults, entry.EpisodeNumber, cancellationToken);
    }

    return 0;
}

/// <summary>
/// Render a table of watch history entries to the console.
/// </summary>
/// <param name="entries">List of history entries to display.</param>
static void RenderHistory(IReadOnlyList<WatchHistoryEntry> entries)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("History");
    Console.ResetColor();
    Console.WriteLine($"Showing {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}:");

    Console.WriteLine($"{"#",3} {"Anime",-30} {"Ep",4} {"Quality",-8} {"Watched",-20} Note");
    var index = 1;
    foreach (var e in entries)
    {
        Console.WriteLine($"{index,3} {Truncate(e.AnimeTitle,30),-30} {e.EpisodeNumber,4} {e.Quality ?? "?",-8} {e.WatchedAt,-20:u} {e.EpisodeTitle ?? string.Empty}");
        index++;
    }
}

/// <summary>
/// Handle 'koware history clear' subcommand for anime watch history.
/// </summary>
static async Task<int> HandleHistoryClearAsync(string[] args, IWatchHistoryStore history, CancellationToken cancellationToken)
{
    string? animeFilter = null;
    bool confirmed = false;

    // Parse args: koware history clear [--anime <title>] [--confirm]
    for (int i = 2; i < args.Length; i++)
    {
        var arg = args[i].ToLowerInvariant();
        switch (arg)
        {
            case "--anime":
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("Missing value for --anime");
                    return 1;
                }
                animeFilter = args[++i];
                break;
            case "--confirm":
            case "-y":
                confirmed = true;
                break;
        }
    }

    // Require confirmation for destructive action
    if (!confirmed)
    {
        var target = string.IsNullOrWhiteSpace(animeFilter) ? "ALL watch history" : $"watch history for '{animeFilter}'";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"⚠ This will permanently delete {target}.");
        Console.ResetColor();
        Console.WriteLine();
        Console.Write("Are you sure? Type 'yes' to confirm: ");
        var response = Console.ReadLine()?.Trim();
        if (!string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }
    }

    int deleted;
    if (string.IsNullOrWhiteSpace(animeFilter))
    {
        deleted = await history.ClearAsync(cancellationToken);
    }
    else
    {
        deleted = await history.ClearForAnimeAsync(animeFilter, cancellationToken);
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("✓ ");
    Console.ResetColor();
    Console.WriteLine($"Cleared {deleted} watch history {(deleted == 1 ? "entry" : "entries")}.");
    return 0;
}

/// <summary>
/// Handle 'koware history clear' subcommand for manga read history.
/// </summary>
static async Task<int> HandleMangaHistoryClearAsync(string[] args, IReadHistoryStore history, CancellationToken cancellationToken)
{
    string? mangaFilter = null;
    bool confirmed = false;

    // Parse args: koware history clear [--manga <title>] [--confirm]
    for (int i = 2; i < args.Length; i++)
    {
        var arg = args[i].ToLowerInvariant();
        switch (arg)
        {
            case "--manga":
            case "--anime": // Allow --anime as alias
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("Missing value for --manga");
                    return 1;
                }
                mangaFilter = args[++i];
                break;
            case "--confirm":
            case "-y":
                confirmed = true;
                break;
        }
    }

    // Require confirmation for destructive action
    if (!confirmed)
    {
        var target = string.IsNullOrWhiteSpace(mangaFilter) ? "ALL read history" : $"read history for '{mangaFilter}'";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"⚠ This will permanently delete {target}.");
        Console.ResetColor();
        Console.WriteLine();
        Console.Write("Are you sure? Type 'yes' to confirm: ");
        var response = Console.ReadLine()?.Trim();
        if (!string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }
    }

    int deleted;
    if (string.IsNullOrWhiteSpace(mangaFilter))
    {
        deleted = await history.ClearAsync(cancellationToken);
    }
    else
    {
        deleted = await history.ClearForMangaAsync(mangaFilter, cancellationToken);
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("✓ ");
    Console.ResetColor();
    Console.WriteLine($"Cleared {deleted} read history {(deleted == 1 ? "entry" : "entries")}.");
    return 0;
}

/// <summary>
/// Handle history command in manga mode: browse read history.
/// </summary>
static async Task<int> HandleMangaHistoryAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    var readHistory = services.GetRequiredService<IReadHistoryStore>();

    // Handle 'koware history clear' subcommand in manga mode
    if (args.Length > 1 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        return await HandleMangaHistoryClearAsync(args, readHistory, cancellationToken);
    }

    string? search = null;
    int limit = 10;
    DateTimeOffset? after = null;
    DateTimeOffset? before = null;
    float? fromChapter = null;
    float? toChapter = null;
    bool json = false;
    bool stats = false;

    var idx = 1;
    if (args.Length > 1 && args[1].Equals("search", StringComparison.OrdinalIgnoreCase))
    {
        idx = 2;
        if (args.Length > 2)
        {
            search = string.Join(' ', args.Skip(2)).Trim();
        }
    }

    while (idx < args.Length)
    {
        var arg = args[idx];
        switch (arg.ToLowerInvariant())
        {
            case "--manga":
            case "--anime": // Allow --anime as alias for compatibility
                if (idx + 1 >= args.Length) { Console.WriteLine("Missing value for --manga"); return 1; }
                search = args[++idx];
                break;
            case "--limit":
                if (idx + 1 >= args.Length || !int.TryParse(args[idx + 1], out limit))
                { Console.WriteLine("Value for --limit must be an integer."); return 1; }
                idx++;
                break;
            case "--after":
                if (idx + 1 >= args.Length || !DateTimeOffset.TryParse(args[idx + 1], out var parsedAfter))
                { Console.WriteLine("Value for --after must be a date."); return 1; }
                after = parsedAfter;
                idx++;
                break;
            case "--before":
                if (idx + 1 >= args.Length || !DateTimeOffset.TryParse(args[idx + 1], out var parsedBefore))
                { Console.WriteLine("Value for --before must be a date."); return 1; }
                before = parsedBefore;
                idx++;
                break;
            case "--from":
                if (idx + 1 >= args.Length || !float.TryParse(args[idx + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                { Console.WriteLine("Value for --from must be a number."); return 1; }
                fromChapter = f;
                idx++;
                break;
            case "--to":
                if (idx + 1 >= args.Length || !float.TryParse(args[idx + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                { Console.WriteLine("Value for --to must be a number."); return 1; }
                toChapter = t;
                idx++;
                break;
            case "--json":
                json = true;
                break;
            case "--stats":
                stats = true;
                break;
        }
        idx++;
    }

    // Interactive mode when no flags (default)
    var hasFilters = search != null || after != null || before != null || fromChapter != null || toChapter != null || json || stats;
    if (!hasFilters)
    {
        var historyManager = new Koware.Cli.Console.InteractiveMangaHistoryManager(
            readHistory,
            onReadManga: async (entry, chapter) =>
            {
                var readArgs = new List<string> { 
                    "read", entry.MangaTitle, 
                    "--chapter", chapter.ToString(System.Globalization.CultureInfo.InvariantCulture), 
                    "--index", "1", 
                    "--non-interactive"
                };
                var cliDefaults = services.GetRequiredService<IOptions<DefaultCliOptions>>().Value;
                await HandleReadAsync(readArgs.ToArray(), services, logger, cliDefaults, cancellationToken);
            },
            cancellationToken: cancellationToken);
        return await historyManager.RunAsync();
    }

    if (stats)
    {
        var statsResult = await readHistory.GetStatsAsync(search, cancellationToken);
        if (json)
        {
            var payload = new
            {
                mode = "manga",
                total = statsResult.Sum(s => s.Count),
                uniqueManga = statsResult.Count,
                top = statsResult.Select(s => new { title = s.MangaTitle, count = s.Count, lastRead = s.LastRead })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Reading History Stats");
        Console.ResetColor();
        Console.WriteLine($"Total reads : {statsResult.Sum(s => s.Count)}");
        Console.WriteLine($"Unique manga: {statsResult.Count}");
        Console.WriteLine("Top manga:");
        foreach (var s in statsResult.Take(10))
        {
            Console.WriteLine($"  {s.MangaTitle} ({s.Count} entries, last: {s.LastRead:u})");
        }
        return 0;
    }

    var query = new ReadHistoryQuery(search, after, before, fromChapter, toChapter, limit);
    var entries = await readHistory.QueryAsync(query, cancellationToken);

    if (entries.Count == 0)
    {
        Console.WriteLine("No reading history matches your filters.");
        return 0;
    }

    if (json)
    {
        var payload = new
        {
            mode = "manga",
            total = entries.Count,
            entries = entries.Select(e => new
            {
                mangaId = e.MangaId,
                mangaTitle = e.MangaTitle,
                chapter = e.ChapterNumber,
                chapterTitle = e.ChapterTitle,
                readAt = e.ReadAt
            })
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // Render manga history
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("Reading History");
    Console.ResetColor();
    Console.WriteLine($"Showing {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}:");

    Console.WriteLine($"{"#",3} {"Manga",-35} {"Ch",6} {"Read At",-20}");
    var index = 1;
    foreach (var e in entries)
    {
        var chNum = e.ChapterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{index,3} {Truncate(e.MangaTitle, 35),-35} {chNum,6} {e.ReadAt:u,-20}");
        index++;
    }

    return 0;
}

/// <summary>
/// Implement the <c>koware list</c> command: manage anime tracking list.
/// </summary>
/// <param name="args">CLI arguments; supports add, update, remove, stats subcommands.</param>
/// <param name="services">Service provider for anime list store.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// Subcommands:
///   list                          - Show all tracked anime
///   list --status &lt;status&gt;       - Filter by status (watching, completed, plan, hold, dropped)
///   list add "&lt;title&gt;"           - Add anime to list
///   list update "&lt;title&gt;" --status &lt;status&gt; - Update anime status
///   list remove "&lt;title&gt;"        - Remove anime from list
///   list stats                    - Show aggregated stats
/// </remarks>
static async Task<int> HandleListAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var mode = defaults.GetMode();

    if (mode == CliMode.Manga)
    {
        return await HandleMangaListAsync(args, services, logger, cancellationToken);
    }

    var animeList = services.GetRequiredService<IAnimeListStore>();
    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();

    if (args.Length < 2)
    {
        // Default: interactive list manager
        var listManager = new Koware.Cli.Console.InteractiveListManager(animeList, cancellationToken: cancellationToken);
        return await listManager.RunAsync();
    }

    var subcommand = args[1].ToLowerInvariant();

    switch (subcommand)
    {
        case "add":
            return await HandleListAddAsync(args, animeList, orchestrator, logger, cancellationToken);
        case "update":
            return await HandleListUpdateAsync(args, animeList, logger, cancellationToken);
        case "remove":
        case "delete":
            return await HandleListRemoveAsync(args, animeList, logger, cancellationToken);
        case "stats":
            return await HandleListStatsAsync(animeList, args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)), cancellationToken);
        default:
            // Check for --status flag on main list command
            AnimeWatchStatus? statusFilter = null;
            bool json = false;

            for (var i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    statusFilter = AnimeWatchStatusExtensions.ParseStatusArg(args[i + 1]);
                    if (statusFilter is null)
                    {
                        Console.WriteLine($"Unknown status '{args[i + 1]}'. Valid: watching, completed, plan, hold, dropped");
                        return 1;
                    }
                    i++;
                }
                else if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    json = true;
                }
            }

            return await ShowListAsync(animeList, statusFilter, json, cancellationToken);
    }
}

static async Task<int> HandleListAddAsync(string[] args, IAnimeListStore animeList, ScrapeOrchestrator orchestrator, ILogger logger, CancellationToken cancellationToken)
{
    string? query = null;
    AnimeWatchStatus status = AnimeWatchStatus.PlanToWatch;
    int? episodes = null;

    for (var i = 2; i < args.Length; i++)
    {
        if (args[i].Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var parsed = AnimeWatchStatusExtensions.ParseStatusArg(args[i + 1]);
            if (parsed is null)
            {
                Console.WriteLine($"Unknown status '{args[i + 1]}'. Valid: watching, completed, plan, hold, dropped");
                return 1;
            }
            status = parsed.Value;
            i++;
        }
        else if (args[i].Equals("--episodes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var ep) || ep <= 0)
            {
                Console.WriteLine("--episodes must be a positive integer.");
                return 1;
            }
            episodes = ep;
            i++;
        }
        else if (query is null)
        {
            query = args[i];
        }
    }

    if (string.IsNullOrWhiteSpace(query))
    {
        Console.WriteLine("Usage: koware list add \"<anime title>\" [--status <status>] [--episodes <count>]");
        return 1;
    }

    // Search for matching anime
    Console.WriteLine($"Searching for '{query}'...");
    var matches = (await orchestrator.SearchAsync(query, cancellationToken)).ToList();

    if (matches.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"No anime found matching '{query}'.");
        Console.ResetColor();
        return 1;
    }

    // Display matches with colored formatting
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Anime matches for \"{query}\":");
    Console.ResetColor();

    for (var i = 0; i < matches.Count; i++)
    {
        var color = TextColorer.ForMatchIndex(i, matches.Count);
        Console.ForegroundColor = color;
        Console.Write($"  [{i + 1}] {matches[i].Title}");
        Console.ResetColor();
        Console.WriteLine($" -> {matches[i].DetailPage}");
    }

    string animeId;
    string animeTitle;

    if (matches.Count == 1)
    {
        // Single match - use it directly
        animeId = matches[0].Id.Value;
        animeTitle = matches[0].Title;
    }
    else
    {
        // Multiple matches - use interactive selector
        var result = InteractiveSelect.Show(
            matches.ToList(),
            a => a.Title,
            new SelectorOptions<Anime>
            {
                Prompt = "Select anime to add",
                MaxVisibleItems = 10,
                ShowSearch = true,
                SecondaryDisplayFunc = a => a.Synopsis ?? ""
            });

        if (result.Cancelled)
        {
            WriteColoredLine("Cancelled.", ConsoleColor.Yellow);
            return 1;
        }

        animeId = result.Selected!.Id.Value;
        animeTitle = result.Selected!.Title;
    }

    // Check if already in list
    var existing = await animeList.GetByTitleAsync(animeTitle, cancellationToken);
    if (existing is not null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"'{animeTitle}' is already in your list as '{existing.Status.ToDisplayString()}'.");
        Console.ResetColor();
        return 1;
    }

    try
    {
        var entry = await animeList.AddAsync(animeId, animeTitle, status, episodes, cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Added '{entry.AnimeTitle}' to your list as '{status.ToDisplayString()}'.");
        Console.ResetColor();
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        return 1;
    }
}

static async Task<int> HandleListUpdateAsync(string[] args, IAnimeListStore animeList, ILogger logger, CancellationToken cancellationToken)
{
    string? title = null;
    AnimeWatchStatus? status = null;
    int? score = null;
    int? episodes = null;
    string? notes = null;

    for (var i = 2; i < args.Length; i++)
    {
        if (args[i].Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            status = AnimeWatchStatusExtensions.ParseStatusArg(args[i + 1]);
            if (status is null)
            {
                Console.WriteLine($"Unknown status '{args[i + 1]}'. Valid: watching, completed, plan, hold, dropped");
                return 1;
            }
            i++;
        }
        else if (args[i].Equals("--score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var s) || s < 1 || s > 10)
            {
                Console.WriteLine("--score must be an integer between 1 and 10.");
                return 1;
            }
            score = s;
            i++;
        }
        else if (args[i].Equals("--episodes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var ep) || ep <= 0)
            {
                Console.WriteLine("--episodes must be a positive integer.");
                return 1;
            }
            episodes = ep;
            i++;
        }
        else if (args[i].Equals("--notes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            notes = args[i + 1];
            i++;
        }
        else if (title is null)
        {
            title = args[i];
        }
    }

    if (string.IsNullOrWhiteSpace(title))
    {
        Console.WriteLine("Usage: koware list update \"<anime title>\" [--status <status>] [--score <1-10>] [--episodes <count>] [--notes \"...\"]");
        return 1;
    }

    // Try exact match first, then fuzzy search
    var existing = await animeList.GetByTitleAsync(title, cancellationToken)
                   ?? await animeList.SearchAsync(title, cancellationToken);

    if (existing is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Anime '{title}' not found in your list.");
        Console.ResetColor();
        return 1;
    }

    var updated = await animeList.UpdateAsync(existing.AnimeTitle, status, null, episodes, score, notes, cancellationToken);

    if (updated)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"Updated '{existing.AnimeTitle}'");
        if (status.HasValue) Console.Write($" → {status.Value.ToDisplayString()}");
        if (score.HasValue) Console.Write($" (score: {score}/10)");
        Console.WriteLine();
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine("No changes made.");
    }

    return 0;
}

static async Task<int> HandleListRemoveAsync(string[] args, IAnimeListStore animeList, ILogger logger, CancellationToken cancellationToken)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: koware list remove \"<anime title>\"");
        return 1;
    }

    var title = args[2];

    // Try exact match first, then fuzzy search
    var existing = await animeList.GetByTitleAsync(title, cancellationToken)
                   ?? await animeList.SearchAsync(title, cancellationToken);

    if (existing is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Anime '{title}' not found in your list.");
        Console.ResetColor();
        return 1;
    }

    var removed = await animeList.RemoveAsync(existing.AnimeTitle, cancellationToken);

    if (removed)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Removed '{existing.AnimeTitle}' from your list.");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine("Failed to remove anime.");
        return 1;
    }

    return 0;
}

static async Task<int> HandleListStatsAsync(IAnimeListStore animeList, bool json, CancellationToken cancellationToken)
{
    var stats = await animeList.GetStatsAsync(cancellationToken);

    if (json)
    {
        var payload = new
        {
            watching = stats.Watching,
            completed = stats.Completed,
            planToWatch = stats.PlanToWatch,
            onHold = stats.OnHold,
            dropped = stats.Dropped,
            totalEpisodesWatched = stats.TotalEpisodesWatched,
            total = stats.Watching + stats.Completed + stats.PlanToWatch + stats.OnHold + stats.Dropped
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Anime List Stats");
    Console.ResetColor();
    Console.WriteLine($"  Watching:      {stats.Watching}");
    Console.WriteLine($"  Completed:     {stats.Completed}");
    Console.WriteLine($"  Plan to Watch: {stats.PlanToWatch}");
    Console.WriteLine($"  On Hold:       {stats.OnHold}");
    Console.WriteLine($"  Dropped:       {stats.Dropped}");
    Console.WriteLine($"  ─────────────────────");
    Console.WriteLine($"  Total:         {stats.Watching + stats.Completed + stats.PlanToWatch + stats.OnHold + stats.Dropped}");
    Console.WriteLine($"  Episodes:      {stats.TotalEpisodesWatched}");
    return 0;
}

static async Task<int> ShowListAsync(IAnimeListStore animeList, AnimeWatchStatus? statusFilter, bool json, CancellationToken cancellationToken)
{
    var entries = await animeList.GetAllAsync(statusFilter, cancellationToken);

    if (entries.Count == 0)
    {
        if (statusFilter.HasValue)
        {
            Console.WriteLine($"No anime with status '{statusFilter.Value.ToDisplayString()}' in your list.");
        }
        else
        {
            Console.WriteLine("Your anime list is empty. Use 'koware list add \"<title>\"' to add anime.");
        }
        return 0;
    }

    if (json)
    {
        var payload = entries.Select(e => new
        {
            title = e.AnimeTitle,
            status = e.Status.ToString().ToLowerInvariant(),
            episodesWatched = e.EpisodesWatched,
            totalEpisodes = e.TotalEpisodes,
            score = e.Score,
            addedAt = e.AddedAt,
            updatedAt = e.UpdatedAt,
            completedAt = e.CompletedAt
        });
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(statusFilter.HasValue ? $"Anime List ({statusFilter.Value.ToDisplayString()})" : "Anime List");
    Console.ResetColor();
    Console.WriteLine($"{"#",3} {"Status",-14} {"Progress",-10} {"Score",-6} {"Title",-40}");
    Console.WriteLine(new string('─', 80));

    var index = 1;
    foreach (var e in entries)
    {
        var progress = e.TotalEpisodes.HasValue
            ? $"{e.EpisodesWatched}/{e.TotalEpisodes}"
            : $"{e.EpisodesWatched}/?";
        var scoreStr = e.Score.HasValue ? $"{e.Score}/10" : "-";

        Console.Write($"{index,3} ");
        Console.ForegroundColor = e.Status.ToColor();
        Console.Write($"{e.Status.ToDisplayString(),-14}");
        Console.ResetColor();
        Console.WriteLine($" {progress,-10} {scoreStr,-6} {Truncate(e.AnimeTitle, 40),-40}");
        index++;
    }

    return 0;
}

// ===== Manga List Functions =====

static async Task<int> HandleMangaListAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    var mangaList = services.GetRequiredService<IMangaListStore>();
    var catalog = services.GetRequiredService<IMangaCatalog>();

    if (args.Length < 2)
    {
        // Default: interactive list manager with read callback
        var svc = services;
        var log = logger;
        var ct = cancellationToken;
        var defaults = svc.GetRequiredService<IOptions<DefaultCliOptions>>().Value;
        
        Func<MangaListEntry, Task> onRead = async entry =>
        {
            var nextChapter = entry.ChaptersRead + 1;
            await HandleReadAsync(
                new[] { "read", entry.MangaTitle, "--chapter", nextChapter.ToString() },
                svc,
                log,
                defaults,
                ct);
        };

        var listManager = new Koware.Cli.Console.InteractiveMangaListManager(
            mangaList,
            onRead: onRead,
            cancellationToken: cancellationToken);
        return await listManager.RunAsync();
    }

    var subcommand = args[1].ToLowerInvariant();

    switch (subcommand)
    {
        case "add":
            return await HandleMangaListAddAsync(args, mangaList, catalog, logger, cancellationToken);
        case "update":
            return await HandleMangaListUpdateAsync(args, mangaList, logger, cancellationToken);
        case "remove":
        case "delete":
            return await HandleMangaListRemoveAsync(args, mangaList, logger, cancellationToken);
        case "stats":
            return await HandleMangaListStatsAsync(mangaList, args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)), cancellationToken);
        default:
            MangaReadStatus? statusFilter = null;
            bool json = false;

            for (var i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    statusFilter = MangaReadStatusExtensions.ParseStatusArg(args[i + 1]);
                    if (statusFilter is null)
                    {
                        Console.WriteLine($"Unknown status '{args[i + 1]}'. Valid: reading, completed, plan, hold, dropped");
                        return 1;
                    }
                    i++;
                }
                else if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    json = true;
                }
            }

            return await ShowMangaListAsync(mangaList, statusFilter, json, cancellationToken);
    }
}

static async Task<int> HandleMangaListAddAsync(string[] args, IMangaListStore mangaList, IMangaCatalog catalog, ILogger logger, CancellationToken cancellationToken)
{
    string? query = null;
    MangaReadStatus status = MangaReadStatus.PlanToRead;
    int? chapters = null;

    for (var i = 2; i < args.Length; i++)
    {
        if (args[i].Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var parsed = MangaReadStatusExtensions.ParseStatusArg(args[i + 1]);
            if (parsed is null)
            {
                Console.WriteLine($"Unknown status '{args[i + 1]}'. Valid: reading, completed, plan, hold, dropped");
                return 1;
            }
            status = parsed.Value;
            i++;
        }
        else if (args[i].Equals("--chapters", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var ch) || ch <= 0)
            {
                Console.WriteLine("--chapters must be a positive integer.");
                return 1;
            }
            chapters = ch;
            i++;
        }
        else if (query is null)
        {
            query = args[i];
        }
    }

    if (string.IsNullOrWhiteSpace(query))
    {
        Console.WriteLine("Usage: koware list add \"<manga title>\" [--status <status>] [--chapters <count>]");
        return 1;
    }

    Console.WriteLine($"Searching for '{query}'...");
    var matches = (await catalog.SearchAsync(query, cancellationToken)).ToList();

    if (matches.Count == 0)
    {
        WriteColoredLine($"No manga found matching '{query}'.", ConsoleColor.Yellow);
        return 1;
    }

    // Display matches with colored formatting
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine($"Manga matches for \"{query}\":");
    Console.ResetColor();

    for (var i = 0; i < matches.Count; i++)
    {
        var color = TextColorer.ForMatchIndex(i, matches.Count);
        Console.ForegroundColor = color;
        Console.Write($"  [{i + 1}] {matches[i].Title}");
        Console.ResetColor();
        Console.WriteLine($" -> {matches[i].DetailPage}");
    }

    string mangaId;
    string mangaTitle;

    if (matches.Count == 1)
    {
        mangaId = matches[0].Id.Value;
        mangaTitle = matches[0].Title;
    }
    else
    {
        // Interactive selection
        var result = InteractiveSelect.Show(
            matches.ToList(),
            m => m.Title,
            new SelectorOptions<Manga>
            {
                Prompt = "Select manga to add",
                MaxVisibleItems = 10,
                ShowSearch = true,
                SecondaryDisplayFunc = m => m.Synopsis ?? ""
            });

        if (result.Cancelled)
        {
            WriteColoredLine("Cancelled.", ConsoleColor.Yellow);
            return 1;
        }

        mangaId = result.Selected!.Id.Value;
        mangaTitle = result.Selected!.Title;
    }

    var existing = await mangaList.GetByTitleAsync(mangaTitle, cancellationToken);
    if (existing is not null)
    {
        WriteColoredLine($"'{mangaTitle}' is already in your list as '{existing.Status.ToDisplayString()}'.", ConsoleColor.Yellow);
        return 1;
    }

    try
    {
        await mangaList.AddAsync(mangaId, mangaTitle, status, chapters, cancellationToken);
        WriteColoredLine($"Added '{mangaTitle}' to your list as '{status.ToDisplayString()}'.", ConsoleColor.Green);
        return 0;
    }
    catch (Exception ex)
    {
        WriteColoredLine($"Failed to add manga: {ex.Message}", ConsoleColor.Red);
        return 1;
    }
}

static async Task<int> HandleMangaListUpdateAsync(string[] args, IMangaListStore mangaList, ILogger logger, CancellationToken cancellationToken)
{
    string? query = null;
    MangaReadStatus? status = null;
    int? chaptersRead = null;
    int? totalChapters = null;
    int? score = null;

    for (var i = 2; i < args.Length; i++)
    {
        if (args[i].Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            status = MangaReadStatusExtensions.ParseStatusArg(args[i + 1]);
            if (status is null)
            {
                Console.WriteLine($"Unknown status '{args[i + 1]}'. Valid: reading, completed, plan, hold, dropped");
                return 1;
            }
            i++;
        }
        else if (args[i].Equals("--chapters", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var ch) || ch < 0)
            {
                Console.WriteLine("--chapters must be a non-negative integer.");
                return 1;
            }
            chaptersRead = ch;
            i++;
        }
        else if (args[i].Equals("--total", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var t) || t <= 0)
            {
                Console.WriteLine("--total must be a positive integer.");
                return 1;
            }
            totalChapters = t;
            i++;
        }
        else if (args[i].Equals("--score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var s) || s < 1 || s > 10)
            {
                Console.WriteLine("--score must be between 1 and 10.");
                return 1;
            }
            score = s;
            i++;
        }
        else if (query is null)
        {
            query = args[i];
        }
    }

    if (string.IsNullOrWhiteSpace(query))
    {
        Console.WriteLine("Usage: koware list update \"<manga title>\" [--status <status>] [--chapters <n>] [--total <n>] [--score <1-10>]");
        return 1;
    }

    var entry = await mangaList.SearchAsync(query, cancellationToken);
    if (entry is null)
    {
        WriteColoredLine($"No manga matching '{query}' found in your list.", ConsoleColor.Yellow);
        return 1;
    }

    var updated = await mangaList.UpdateAsync(entry.MangaTitle, status, chaptersRead, totalChapters, score, cancellationToken: cancellationToken);
    if (updated)
    {
        WriteColoredLine($"Updated '{entry.MangaTitle}'.", ConsoleColor.Green);
        return 0;
    }

    WriteColoredLine("No changes made.", ConsoleColor.Yellow);
    return 0;
}

static async Task<int> HandleMangaListRemoveAsync(string[] args, IMangaListStore mangaList, ILogger logger, CancellationToken cancellationToken)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: koware list remove \"<manga title>\"");
        return 1;
    }

    var query = args[2];
    var entry = await mangaList.SearchAsync(query, cancellationToken);
    if (entry is null)
    {
        WriteColoredLine($"No manga matching '{query}' found in your list.", ConsoleColor.Yellow);
        return 1;
    }

    Console.Write($"Remove '{entry.MangaTitle}' from your list? (y/N): ");
    var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (confirm != "y" && confirm != "yes")
    {
        Console.WriteLine("Cancelled.");
        return 0;
    }

    var removed = await mangaList.RemoveAsync(entry.MangaTitle, cancellationToken);
    if (removed)
    {
        WriteColoredLine($"Removed '{entry.MangaTitle}' from your list.", ConsoleColor.Green);
        return 0;
    }

    WriteColoredLine("Failed to remove manga.", ConsoleColor.Red);
    return 1;
}

static async Task<int> HandleMangaListStatsAsync(IMangaListStore mangaList, bool json, CancellationToken cancellationToken)
{
    var stats = await mangaList.GetStatsAsync(cancellationToken);

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("Manga List Stats");
    Console.ResetColor();
    Console.WriteLine($"  Reading:       {stats.Reading}");
    Console.WriteLine($"  Completed:     {stats.Completed}");
    Console.WriteLine($"  Plan to Read:  {stats.PlanToRead}");
    Console.WriteLine($"  On Hold:       {stats.OnHold}");
    Console.WriteLine($"  Dropped:       {stats.Dropped}");
    Console.WriteLine($"  ─────────────────────");
    Console.WriteLine($"  Total:         {stats.Reading + stats.Completed + stats.PlanToRead + stats.OnHold + stats.Dropped}");
    Console.WriteLine($"  Chapters:      {stats.TotalChaptersRead}");
    return 0;
}

static async Task<int> ShowMangaListAsync(IMangaListStore mangaList, MangaReadStatus? statusFilter, bool json, CancellationToken cancellationToken)
{
    var entries = await mangaList.GetAllAsync(statusFilter, cancellationToken);

    if (entries.Count == 0)
    {
        if (statusFilter.HasValue)
        {
            Console.WriteLine($"No manga with status '{statusFilter.Value.ToDisplayString()}' in your list.");
        }
        else
        {
            Console.WriteLine("Your manga list is empty. Use 'koware list add \"<title>\"' to add manga.");
        }
        return 0;
    }

    if (json)
    {
        var payload = entries.Select(e => new
        {
            title = e.MangaTitle,
            status = e.Status.ToString().ToLowerInvariant(),
            chaptersRead = e.ChaptersRead,
            totalChapters = e.TotalChapters,
            score = e.Score,
            addedAt = e.AddedAt,
            updatedAt = e.UpdatedAt,
            completedAt = e.CompletedAt
        });
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine(statusFilter.HasValue ? $"Manga List ({statusFilter.Value.ToDisplayString()})" : "Manga List");
    Console.ResetColor();
    Console.WriteLine($"{"#",3} {"Status",-14} {"Progress",-10} {"Score",-6} {"Title",-40}");
    Console.WriteLine(new string('─', 80));

    var index = 1;
    foreach (var e in entries)
    {
        var progress = e.TotalChapters.HasValue
            ? $"{e.ChaptersRead}/{e.TotalChapters}"
            : $"{e.ChaptersRead}/?";
        var scoreStr = e.Score.HasValue ? $"{e.Score}/10" : "-";

        Console.Write($"{index,3} ");
        Console.ForegroundColor = e.Status.ToColor();
        Console.Write($"{e.Status.ToDisplayString(),-14}");
        Console.ResetColor();
        Console.WriteLine($" {progress,-10} {scoreStr,-6} {Truncate(e.MangaTitle, 40),-40}");
        index++;
    }

    return 0;
}

/// <summary>
/// Truncate a string to a maximum length, appending "…" if cut.
/// </summary>
/// <param name="value">The string to truncate.</param>
/// <param name="max">Maximum allowed length including the ellipsis.</param>
/// <returns>The original string or a truncated version.</returns>
static string Truncate(string value, int max)
{
    if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
    return value[..(max - 1)] + "…";
}

/// <summary>
/// Print an error message and return exit code 1.
/// </summary>
/// <param name="message">Error message to display.</param>
/// <returns>Always returns 1.</returns>
static int UsageErrorWithReturn(string message)
{
    Console.WriteLine(message);
    return 1;
}

/// <summary>
/// Launch playback from a history entry at a specific episode number.
/// </summary>
/// <param name="entry">The history entry to base the plan on.</param>
/// <param name="orchestrator">Scraping orchestrator.</param>
/// <param name="services">Service provider.</param>
/// <param name="history">Watch history store.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options.</param>
/// <param name="episodeNumber">Episode number to play.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code from playback.</returns>
static async Task<int> LaunchFromHistory(WatchHistoryEntry entry, ScrapeOrchestrator orchestrator, IServiceProvider services, IWatchHistoryStore history, ILogger logger, DefaultCliOptions defaults, int episodeNumber, CancellationToken cancellationToken)
{
    var quality = entry.Quality ?? defaults.Quality;
    var plan = new ScrapePlan(entry.AnimeTitle, episodeNumber, quality);
    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
}

/// <summary>
/// Implement the <c>koware search</c> command: find anime by query.
/// </summary>
/// <param name="orchestrator">Scraping orchestrator (for anime search).</param>
/// <param name="args">CLI arguments; query words and optional --json flag.</param>
/// <param name="services">Service provider for manga catalog.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success, 1 if query missing.</returns>
/// <remarks>
/// Mode-aware: searches anime or manga based on current mode.
/// With --json, outputs structured JSON instead of a formatted list.
/// </remarks>
static async Task<int> HandleSearchAsync(ScrapeOrchestrator orchestrator, string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var jsonOutput = args.Skip(1).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    
    // Parse filters from arguments
    var filters = SearchFilters.Parse(args);
    
    // Extract query (non-filter arguments)
    var filterFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--json", "--genre", "--year", "--status", "--sort", "--score", "--country"
    };
    
    var queryParts = new List<string>();
    for (int i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (filterFlags.Contains(arg))
        {
            i++; // Skip the flag value
            continue;
        }
        if (!arg.StartsWith("--"))
        {
            queryParts.Add(arg);
        }
    }
    
    var query = string.Join(' ', queryParts).Trim();
    
    // Allow empty query when filters are applied (browse mode)
    if (string.IsNullOrWhiteSpace(query) && !filters.HasFilters)
    {
        Koware.Cli.Console.ErrorDisplay.MissingArgument("query", "koware search <query> [--json]");
        return 1;
    }

    var mode = defaults.GetMode();
    
    // Show filter info if any
    if (filters.HasFilters)
    {
        var filterInfo = new List<string>();
        if (filters.Genres?.Count > 0) filterInfo.Add($"genre: {string.Join(", ", filters.Genres)}");
        if (filters.Year.HasValue) filterInfo.Add($"year: {filters.Year}");
        if (filters.Status != ContentStatus.Any) filterInfo.Add($"status: {filters.Status}");
        if (filters.Sort != SearchSort.Default) filterInfo.Add($"sort: {filters.Sort}");
        if (filters.MinScore.HasValue) filterInfo.Add($"min score: {filters.MinScore}");
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Filters: {string.Join(" | ", filterInfo)}");
        Console.ResetColor();
    }

    if (mode == CliMode.Manga)
    {
        // Manga search
        var mangaCatalog = services.GetRequiredService<IMangaCatalog>();
        var mangaMatches = await mangaCatalog.SearchAsync(query, filters, cancellationToken);

        if (jsonOutput)
        {
            var payload = new
            {
                mode = "manga",
                query,
                filters = filters.HasFilters ? new
                {
                    genres = filters.Genres,
                    year = filters.Year,
                    status = filters.Status.ToString(),
                    sort = filters.Sort.ToString()
                } : null,
                count = mangaMatches.Count,
                matches = mangaMatches.Select(m => new
                {
                    id = m.Id.Value,
                    title = m.Title,
                    synopsis = m.Synopsis,
                    detail = m.DetailPage.ToString()
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            RenderMangaSearch(string.IsNullOrWhiteSpace(query) ? "(browse)" : query, mangaMatches);
        }
    }
    else
    {
        // Anime search (default)
        var catalog = services.GetRequiredService<IAnimeCatalog>();
        var matches = await catalog.SearchAsync(query, filters, cancellationToken);
        
        if (jsonOutput)
        {
            var payload = new
            {
                mode = "anime",
                query,
                filters = filters.HasFilters ? new
                {
                    genres = filters.Genres,
                    year = filters.Year,
                    status = filters.Status.ToString(),
                    sort = filters.Sort.ToString()
                } : null,
                count = matches.Count,
                matches = matches.Select(m => new
                {
                    id = m.Id.Value,
                    title = m.Title,
                    detail = m.DetailPage.ToString()
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            RenderSearch(string.IsNullOrWhiteSpace(query) ? "(browse)" : query, matches);
        }
    }

    return 0;
}

/// <summary>
/// Implement the <c>koware recommend</c> command: suggest anime/manga based on user's list and history.
/// </summary>
/// <param name="args">CLI arguments; optional --genre, --limit flags.</param>
/// <param name="services">Service provider for catalog and list stores.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
static async Task<int> HandleRecommendAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var jsonOutput = args.Skip(1).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var limit = 10;
    
    // Parse limit if specified
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var l))
        {
            limit = Math.Clamp(l, 1, 50);
        }
    }
    
    var mode = defaults.GetMode();
    
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"🎯 Generating recommendations based on your {(mode == CliMode.Manga ? "reading" : "watch")} history...");
    Console.ResetColor();
    Console.WriteLine();
    
    if (mode == CliMode.Manga)
    {
        var mangaList = services.GetRequiredService<IMangaListStore>();
        var mangaCatalog = services.GetRequiredService<IMangaCatalog>();
        
        // Get user's list to exclude already tracked manga
        var userList = await mangaList.GetAllAsync(cancellationToken: cancellationToken);
        var trackedIds = userList.Select(e => e.MangaId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackedTitles = userList.Select(e => e.MangaTitle.ToLowerInvariant()).ToHashSet();
        
        // Get popular manga
        var popular = await mangaCatalog.BrowsePopularAsync(new SearchFilters { Sort = SearchSort.Popularity }, cancellationToken);
        
        // Filter out already tracked manga
        var recommendations = popular
            .Where(m => !trackedIds.Contains(m.Id.Value) && !trackedTitles.Contains(m.Title.ToLowerInvariant()))
            .Take(limit)
            .ToList();
        
        if (recommendations.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No recommendations found. Try browsing popular content with:");
            Console.ResetColor();
            Console.WriteLine("  koware search --sort popular");
            return 0;
        }
        
        if (jsonOutput)
        {
            var payload = new
            {
                mode = "manga",
                count = recommendations.Count,
                recommendations = recommendations.Select(m => new
                {
                    id = m.Id.Value,
                    title = m.Title,
                    synopsis = m.Synopsis
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Recommended Manga ({recommendations.Count})");
            Console.ResetColor();
            Console.WriteLine(new string('─', 40));
            
            for (int i = 0; i < recommendations.Count; i++)
            {
                var m = recommendations[i];
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{i + 1,2}. ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(m.Title);
                Console.ResetColor();
                
                if (!string.IsNullOrWhiteSpace(m.Synopsis))
                {
                    var preview = m.Synopsis.Length > 100 ? m.Synopsis[..100] + "..." : m.Synopsis;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"    {preview.Replace("\n", " ")}");
                    Console.ResetColor();
                }
            }
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("To read: koware read \"<title>\"");
            Console.ResetColor();
        }
    }
    else
    {
        var animeList = services.GetRequiredService<IAnimeListStore>();
        var animeCatalog = services.GetRequiredService<IAnimeCatalog>();
        
        // Get user's list to exclude already tracked anime
        var userList = await animeList.GetAllAsync(cancellationToken: cancellationToken);
        var trackedIds = userList.Select(e => e.AnimeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackedTitles = userList.Select(e => e.AnimeTitle.ToLowerInvariant()).ToHashSet();
        
        // Get popular anime
        var popular = await animeCatalog.BrowsePopularAsync(new SearchFilters { Sort = SearchSort.Popularity }, cancellationToken);
        
        // Filter out already tracked anime
        var recommendations = popular
            .Where(a => !trackedIds.Contains(a.Id.Value) && !trackedTitles.Contains(a.Title.ToLowerInvariant()))
            .Take(limit)
            .ToList();
        
        if (recommendations.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No recommendations found. Try browsing popular content with:");
            Console.ResetColor();
            Console.WriteLine("  koware search --sort popular");
            return 0;
        }
        
        if (jsonOutput)
        {
            var payload = new
            {
                mode = "anime",
                count = recommendations.Count,
                recommendations = recommendations.Select(a => new
                {
                    id = a.Id.Value,
                    title = a.Title
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Recommended Anime ({recommendations.Count})");
            Console.ResetColor();
            Console.WriteLine(new string('─', 40));
            
            for (int i = 0; i < recommendations.Count; i++)
            {
                var a = recommendations[i];
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{i + 1,2}. ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(a.Title);
                Console.ResetColor();
            }
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("To watch: koware watch \"<title>\"");
            Console.ResetColor();
        }
    }
    
    return 0;
}

/// <summary>
/// Implement the <c>koware offline</c> command: show downloaded content available offline.
/// </summary>
/// <param name="args">CLI arguments; optional --stats, --cleanup, --json flags.</param>
/// <param name="services">Service provider for download store.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
static async Task<int> HandleOfflineAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var downloadStore = services.GetRequiredService<IDownloadStore>();
    var jsonOutput = args.Skip(1).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var showStats = args.Skip(1).Any(a => a.Equals("--stats", StringComparison.OrdinalIgnoreCase));
    var cleanup = args.Skip(1).Any(a => a.Equals("--cleanup", StringComparison.OrdinalIgnoreCase));
    
    // Cleanup missing files
    if (cleanup)
    {
        var step = ConsoleStep.Start("Cleaning up missing downloads");
        var removed = await downloadStore.CleanupMissingAsync(cancellationToken);
        step.Succeed($"Removed {removed} stale entries");
        return 0;
    }
    
    // Show stats
    if (showStats)
    {
        var stats = await downloadStore.GetStatsAsync(cancellationToken);
        
        if (jsonOutput)
        {
            var payload = new
            {
                totalEpisodes = stats.TotalEpisodes,
                totalChapters = stats.TotalChapters,
                uniqueAnime = stats.UniqueAnime,
                uniqueManga = stats.UniqueManga,
                totalSizeBytes = stats.TotalSizeBytes,
                totalSizeFormatted = FormatFileSize(stats.TotalSizeBytes)
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"{Icons.Download} Download Statistics");
            Console.ResetColor();
            Console.WriteLine(new string('─', 30));
            Console.WriteLine($"  Episodes:   {stats.TotalEpisodes} ({stats.UniqueAnime} anime)");
            Console.WriteLine($"  Chapters:   {stats.TotalChapters} ({stats.UniqueManga} manga)");
            Console.WriteLine($"  Total Size: {FormatFileSize(stats.TotalSizeBytes)}");
        }
        return 0;
    }
    
    // List downloads
    var mode = defaults.GetMode();
    var typeFilter = mode == CliMode.Manga ? DownloadType.Chapter : DownloadType.Episode;
    
    // Interactive mode when no flags
    if (!jsonOutput && !showStats && !cleanup)
    {
        var offlineManager = new Koware.Cli.Console.InteractiveOfflineManager(downloadStore, typeFilter, cancellationToken: cancellationToken);
        return await offlineManager.RunAsync();
    }
    
    var downloads = await downloadStore.GetAllAsync(typeFilter, cancellationToken);
    
    if (downloads.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"No {(mode == CliMode.Manga ? "chapters" : "episodes")} downloaded yet.");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"Download with: koware download \"<title>\" --{(mode == CliMode.Manga ? "chapter" : "episode")} <n>");
        return 0;
    }
    
    // Group by content
    var grouped = downloads
        .GroupBy(d => d.ContentId)
        .Select(g => new
        {
            ContentId = g.Key,
            Title = g.First().ContentTitle,
            Items = g.OrderBy(d => d.Number).ToList(),
            TotalSize = g.Sum(d => d.FileSizeBytes),
            AvailableCount = g.Count(d => d.Exists),
            MissingCount = g.Count(d => !d.Exists)
        })
        .OrderByDescending(g => g.Items.Max(d => d.DownloadedAt))
        .ToList();
    
    if (jsonOutput)
    {
        var payload = new
        {
            mode = mode.ToString().ToLowerInvariant(),
            count = downloads.Count,
            content = grouped.Select(g => new
            {
                id = g.ContentId,
                title = g.Title,
                downloaded = g.Items.Select(d => new
                {
                    number = d.Number,
                    quality = d.Quality,
                    path = d.FilePath,
                    sizeBytes = d.FileSizeBytes,
                    exists = d.Exists,
                    downloadedAt = d.DownloadedAt
                }),
                totalSizeBytes = g.TotalSize
            })
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"{Icons.Download} Downloaded {(mode == CliMode.Manga ? "Manga" : "Anime")} (Available Offline)");
    Console.ResetColor();
    Console.WriteLine(new string('─', 50));
    Console.WriteLine();
    
    foreach (var group in grouped)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  {group.Title}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [{FormatFileSize(group.TotalSize)}]");
        Console.ResetColor();
        
        // Show downloaded numbers
        var numbers = group.Items.Select(d => d.Number).ToList();
        var ranges = FormatNumberRanges(numbers);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"    {Icons.Success} {(mode == CliMode.Manga ? "Ch" : "Ep")}: ");
        Console.ResetColor();
        Console.WriteLine(ranges);
        
        // Show missing count if any
        if (group.MissingCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    {Icons.Warning} {group.MissingCount} file(s) missing - run 'koware offline --cleanup' to remove stale entries");
            Console.ResetColor();
        }
        
        Console.WriteLine();
    }
    
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"Total: {grouped.Count} {(mode == CliMode.Manga ? "manga" : "anime")}, {downloads.Count} {(mode == CliMode.Manga ? "chapters" : "episodes")}");
    Console.WriteLine("View stats: koware offline --stats | Cleanup: koware offline --cleanup");
    Console.ResetColor();
    
    return 0;
}

/// <summary>
/// Format a file size in bytes to a human-readable string.
/// </summary>
static string FormatFileSize(long bytes)
{
    string[] sizes = { "B", "KB", "MB", "GB", "TB" };
    double len = bytes;
    int order = 0;
    while (len >= 1024 && order < sizes.Length - 1)
    {
        order++;
        len /= 1024;
    }
    return $"{len:0.##} {sizes[order]}";
}

/// <summary>
/// Format a list of numbers into compact ranges (e.g., "1-5, 7, 10-12").
/// </summary>
static string FormatNumberRanges(IReadOnlyList<int> numbers)
{
    if (numbers.Count == 0) return "none";
    
    var sorted = numbers.OrderBy(n => n).ToList();
    var ranges = new List<string>();
    var start = sorted[0];
    var end = sorted[0];
    
    for (int i = 1; i < sorted.Count; i++)
    {
        if (sorted[i] == end + 1)
        {
            end = sorted[i];
        }
        else
        {
            ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
            start = end = sorted[i];
        }
    }
    ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
    
    return string.Join(", ", ranges);
}

/// <summary>
/// Implement the <c>koware stream</c> (or <c>plan</c>) command: resolve streams without playing.
/// </summary>
/// <param name="orchestrator">Scraping orchestrator.</param>
/// <param name="args">CLI arguments; query, --episode, --quality, --index, --non-interactive, --json.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// Useful for inspecting available streams before deciding to play or download.
/// </remarks>
static async Task<int> HandlePlanAsync(ScrapeOrchestrator orchestrator, string[] args, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var jsonOutput = args.Skip(1).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var filteredArgs = args.Where((a, idx) => idx == 0 || !a.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();

    ScrapePlan plan;
    try
    {
        plan = ParsePlan(filteredArgs, defaults);
    }
    catch (ArgumentException ex)
    {
        return HandleParseError("stream", ex, logger);
    }

    plan = await MaybeSelectMatchAsync(orchestrator, plan, logger, cancellationToken);

    ScrapeResult result;
    var step = ConsoleStep.Start("Resolving streams");
    try
    {
        result = await orchestrator.ExecuteAsync(plan, cancellationToken);
        step.Succeed("Streams ready");
    }
    catch
    {
        step.Fail("Failed to resolve streams");
        throw;
    }
    if (jsonOutput)
    {
        var payload = new
        {
            query = plan.Query,
            selectedAnime = result.SelectedAnime is null ? null : new
            {
                id = result.SelectedAnime.Id.Value,
                title = result.SelectedAnime.Title
            },
            selectedEpisode = result.SelectedEpisode is null ? null : new
            {
                number = result.SelectedEpisode.Number,
                title = result.SelectedEpisode.Title
            },
            streams = result.Streams?.Select(s => new
            {
                url = s.Url.ToString(),
                quality = s.Quality,
                provider = s.Provider,
                referer = s.Referrer,
                subtitles = s.Subtitles.Select(sub => new
                {
                    label = sub.Label,
                    url = sub.Url.ToString(),
                    language = sub.Language
                })
            }) ?? Enumerable.Empty<object>(),
            matches = result.Matches.Select(m => new { id = m.Id.Value, title = m.Title })
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        RenderPlan(plan, result);
    }

    return 0;
}

/// <summary>
/// Implement the <c>koware watch</c> (or <c>play</c>) command: resolve streams and launch player.
/// </summary>
/// <param name="orchestrator">Scraping orchestrator.</param>
/// <param name="args">CLI arguments; query, --episode, --quality, --index, --non-interactive.</param>
/// <param name="services">Service provider for player options and history.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code from playback.</returns>
static async Task<int> HandlePlayAsync(ScrapeOrchestrator orchestrator, string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    // Block watch command in manga mode
    if (defaults.GetMode() == CliMode.Manga)
    {
        WriteColoredLine("The 'watch' command is not available in manga mode.", ConsoleColor.Yellow);
        WriteColoredLine("Use 'koware read <query>' to read manga, or switch to anime mode with 'koware mode anime'.", ConsoleColor.Gray);
        return 1;
    }

    ScrapePlan plan;
    try
    {
        plan = ParsePlan(args, defaults);
    }
    catch (ArgumentException ex)
    {
        return HandleParseError("watch", ex, logger);
    }

    plan = await MaybeSelectMatchAsync(orchestrator, plan, logger, cancellationToken);
    plan = await MaybeSelectEpisodeAsync(orchestrator, plan, logger, cancellationToken);

    var history = services.GetRequiredService<IWatchHistoryStore>();
    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
}

/// <summary>
/// Implement the <c>koware download</c> command: download episodes or chapters to disk.
/// </summary>
/// <param name="orchestrator">Scraping orchestrator.</param>
/// <param name="args">CLI arguments; query, --episode/--chapter, --episodes/--chapters, --quality, --index, --dir, --non-interactive.</param>
/// <param name="services">Service provider for options.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="defaults">Default CLI options for mode detection.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// Mode-aware: downloads anime episodes or manga chapters based on current mode.
/// Resolves episodes/chapters for a show/manga, selects a range via --episodes/--chapters or --episode/--chapter,
/// and downloads each using HTTP.
/// </remarks>
static async Task<int> HandleDownloadAsync(ScrapeOrchestrator orchestrator, string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var mode = defaults.GetMode();

    if (mode == CliMode.Manga)
    {
        return await HandleMangaDownloadAsync(args, services, logger, cancellationToken);
    }

    // Anime mode (default)
    string? episodesArg = null;
    string? outputDir = null;

    var filteredArgs = new List<string> { args[0] };
    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--episodes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            episodesArg = args[++i];
            continue;
        }

        if (arg.Equals("--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            outputDir = args[++i];
            continue;
        }

        filteredArgs.Add(arg);
    }

    ScrapePlan plan;
    try
    {
        plan = ParsePlan(filteredArgs.ToArray(), defaults);
    }
    catch (ArgumentException ex)
    {
        return HandleParseError("download", ex, logger);
    }

    plan = await MaybeSelectMatchAsync(orchestrator, plan, logger, cancellationToken);

    var step = ConsoleStep.Start("Resolving episodes");
    ScrapeResult initial;
    try
    {
        initial = await orchestrator.ExecuteAsync(plan, cancellationToken);
        step.Succeed("Episodes ready");
    }
    catch
    {
        step.Fail("Failed to resolve episodes");
        throw;
    }

    if (initial.SelectedAnime is null || initial.Episodes is null || initial.Episodes.Count == 0)
    {
        logger.LogWarning("No episodes found for the selected anime.");
        return 1;
    }

    var episodes = initial.Episodes.OrderBy(ep => ep.Number).ToArray();
    var targets = DownloadPlanner.ResolveEpisodeSelection(episodesArg, plan.EpisodeNumber, episodes, logger);
    if (targets.Count == 0)
    {
        logger.LogWarning("No episodes match the requested selection.");
        return 1;
    }

    var targetDir = string.IsNullOrWhiteSpace(outputDir) ? Environment.CurrentDirectory : outputDir;
    Directory.CreateDirectory(targetDir);

    var allAnimeOptions = services.GetService<IOptions<AllAnimeOptions>>()?.Value;
    var defaultReferrer = allAnimeOptions?.Referer;

    using var httpClient = new HttpClient();

    var total = targets.Count;
    var index = 0;
    var failedCount = 0;

    foreach (var episode in targets)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index++;

        // Show progress bar
        var percent = (int)(index * 100.0 / total);
        var filled = (int)(index * 30.0 / total);
        var empty = 30 - filled;
        var bar = new string('█', filled) + new string('░', empty);
        System.Console.Write($"\r  [{bar}] {percent,3}% - Episode {episode.Number,-10}");

        try
        {
            ScrapeResult epResult;
            if (initial.SelectedEpisode is not null && initial.Streams is not null && initial.SelectedEpisode.Number == episode.Number)
            {
                epResult = initial;
            }
            else
            {
                var epPlan = plan with { EpisodeNumber = episode.Number };
                epResult = await orchestrator.ExecuteAsync(epPlan, cancellationToken);
            }

            if (epResult.Streams is null || epResult.Streams.Count == 0)
            {
                failedCount++;
                logger.LogWarning("No streams found for episode {Episode}. Skipping.", episode.Number);
                continue;
            }

            var normalizedStreams = ApplyDefaultReferrer(epResult.Streams, defaultReferrer);
            var stream = PickBestStream(normalizedStreams);
            if (stream is null)
            {
                failedCount++;
                logger.LogWarning("No suitable stream found for episode {Episode}. Skipping.", episode.Number);
                continue;
            }

            var title = epResult.SelectedAnime?.Title ?? initial.SelectedAnime.Title;
            var fileName = DownloadPlanner.BuildDownloadFileName(title, episode, stream.Quality);
            var outputPath = Path.Combine(targetDir, fileName);

            var httpReferrer = stream.Referrer ?? allAnimeOptions?.Referer;
            var httpUserAgent = allAnimeOptions?.UserAgent;

            var isPlaylist = IsPlaylist(stream);
            var ffmpegPath = ResolveExecutablePath("ffmpeg");

            if (isPlaylist && string.IsNullOrWhiteSpace(ffmpegPath))
            {
                failedCount++;
                logger.LogError("ffmpeg is required to download streaming playlist URLs. Episode {Episode} will be skipped.", episode.Number);
                continue;
            }

            if (isPlaylist && !string.IsNullOrWhiteSpace(ffmpegPath))
            {
                await DownloadWithFfmpegAsync(ffmpegPath!, stream, outputPath, httpReferrer, httpUserAgent, logger, cancellationToken);
            }
            else
            {
                await DownloadWithHttpAsync(httpClient, stream, outputPath, httpReferrer, httpUserAgent, logger, cancellationToken);
            }

            // Record download in store
            if (File.Exists(outputPath))
            {
                var fileInfo = new FileInfo(outputPath);
                var downloadStore = services.GetRequiredService<IDownloadStore>();
                await downloadStore.AddAsync(
                    DownloadType.Episode,
                    initial.SelectedAnime.Id.Value,
                    title,
                    episode.Number,
                    stream.Quality,
                    outputPath,
                    fileInfo.Length,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failedCount++;
            logger.LogError(ex, "Failed to download episode {Episode}. Skipping.", episode.Number);
        }
    }

    // Clear progress bar and show completion
    System.Console.Write("\r" + new string(' ', 70) + "\r");
    System.Console.ForegroundColor = ConsoleColor.Green;
    var successCount = total - failedCount;
    System.Console.WriteLine($"  ✔ {successCount} episode(s) downloaded" + (failedCount > 0 ? $" ({failedCount} failed)" : ""));
    System.Console.ResetColor();

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Download complete. Saved episodes to \"{targetDir}\".");
    Console.ResetColor();

    return 0;
}

/// <summary>
/// Implement the <c>koware read</c> command: search manga, fetch chapter pages, and launch the reader.
/// </summary>
/// <param name="args">CLI arguments; query, --chapter, --index, --non-interactive.</param>
/// <param name="services">Service provider for manga catalog and reader options.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Exit code: 0 on success.</returns>
static async Task<int> HandleReadAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    // Block read command in anime mode
    if (defaults.GetMode() == CliMode.Anime)
    {
        WriteColoredLine("The 'read' command is not available in anime mode.", ConsoleColor.Yellow);
        WriteColoredLine("Use 'koware watch <query>' to watch anime, or switch to manga mode with 'koware mode manga'.", ConsoleColor.Gray);
        return 1;
    }

    var queryParts = new List<string>();
    float? chapterNumber = null;
    int? preferredIndex = null;
    var nonInteractive = false;
    int startPage = 1;

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--chapter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                chapterNumber = parsed;
                i++;
                continue;
            }
            logger.LogWarning("Chapter number must be a number.");
            return 1;
        }

        if (arg.Equals("--index", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out var idx) && idx >= 1)
            {
                preferredIndex = idx;
                i++;
                continue;
            }
            logger.LogWarning("--index must be a positive integer.");
            return 1;
        }

        if (arg.Equals("--start-page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out var sp) && sp >= 1)
            {
                startPage = sp;
                i++;
                continue;
            }
            logger.LogWarning("--start-page must be a positive integer.");
            return 1;
        }

        if (arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase))
        {
            nonInteractive = true;
            continue;
        }

        queryParts.Add(arg);
    }

    if (queryParts.Count == 0)
    {
        Koware.Cli.Console.ErrorDisplay.MissingArgument("query", "koware read <query> [--chapter <n>] [--index <n>]");
        return 1;
    }

    var query = string.Join(' ', queryParts).Trim();
    var mangaCatalog = services.GetRequiredService<IMangaCatalog>();

    // Search for manga
    var searchStep = ConsoleStep.Start("Searching manga");
    IReadOnlyCollection<Manga> results;
    try
    {
        results = await mangaCatalog.SearchAsync(query, cancellationToken);
        searchStep.Succeed($"Found {results.Count} result(s)");
    }
    catch (Exception)
    {
        searchStep.Fail("Search failed");
        throw;
    }

    if (results.Count == 0)
    {
        logger.LogWarning("No manga found for query: {Query}", query);
        return 1;
    }

    // Select manga
    Manga selectedManga;
    if (preferredIndex.HasValue)
    {
        var idx = preferredIndex.Value - 1;
        if (idx < 0 || idx >= results.Count)
        {
            logger.LogWarning("Index {Index} is out of range (1-{Count}).", preferredIndex.Value, results.Count);
            return 1;
        }
        selectedManga = results.ElementAt(idx);
    }
    else if (results.Count == 1 || nonInteractive)
    {
        selectedManga = results.First();
    }
    else
    {
        // Interactive selection
        var selectResult = InteractiveSelect.Show(
            results.ToList(),
            m => m.Title,
            new SelectorOptions<Manga>
            {
                Prompt = $"Select manga for \"{query}\"",
                MaxVisibleItems = 10,
                ShowSearch = true,
                SecondaryDisplayFunc = m => m.Synopsis ?? ""
            });

        if (selectResult.Cancelled)
        {
            logger.LogInformation("Selection canceled by user.");
            return 1;
        }

        selectedManga = selectResult.Selected!;
    }

    logger.LogInformation("Selected: {Title}", selectedManga.Title);

    // Fetch chapters
    var chaptersStep = ConsoleStep.Start("Fetching chapters");
    IReadOnlyCollection<Chapter> chapters;
    try
    {
        chapters = await mangaCatalog.GetChaptersAsync(selectedManga, cancellationToken);
        chaptersStep.Succeed($"Found {chapters.Count} chapter(s)");
    }
    catch (Exception)
    {
        chaptersStep.Fail("Failed to fetch chapters");
        throw;
    }

    if (chapters.Count == 0)
    {
        logger.LogWarning("No chapters found for {Title}.", selectedManga.Title);
        return 1;
    }

    // Select chapter
    Chapter selectedChapter;
    if (chapterNumber.HasValue)
    {
        selectedChapter = chapters.FirstOrDefault(c => Math.Abs(c.Number - chapterNumber.Value) < 0.001f)
                          ?? chapters.FirstOrDefault(c => (int)c.Number == (int)chapterNumber.Value)!;
        if (selectedChapter is null)
        {
            logger.LogWarning("Chapter {Chapter} not found. Available: {Min}-{Max}", chapterNumber.Value, chapters.Min(c => c.Number), chapters.Max(c => c.Number));
            return 1;
        }
    }
    else if (nonInteractive)
    {
        selectedChapter = chapters.First();
    }
    else
    {
        // Interactive chapter selection
        var sortedChapters = chapters.OrderBy(c => c.Number).ToList();
        var chapterResult = InteractiveSelect.Show(
            sortedChapters,
            c => $"Chapter {c.Number}" + (string.IsNullOrWhiteSpace(c.Title) ? "" : $" - {c.Title}"),
            new SelectorOptions<Chapter>
            {
                Prompt = $"Select chapter ({sortedChapters.First().Number} - {sortedChapters.Last().Number})",
                MaxVisibleItems = 15,
                ShowSearch = true
            });

        if (chapterResult.Cancelled)
        {
            logger.LogWarning("Selection cancelled.");
            return 1;
        }

        selectedChapter = chapterResult.Selected!;
    }

    logger.LogInformation("Reading: {Title} - Chapter {Chapter}", selectedManga.Title, selectedChapter.Number);

    // Fetch pages
    var pagesStep = ConsoleStep.Start("Fetching pages");
    IReadOnlyCollection<ChapterPage> pages;
    try
    {
        pages = await mangaCatalog.GetPagesAsync(selectedChapter, cancellationToken);
        pagesStep.Succeed($"Loaded {pages.Count} page(s)");
    }
    catch (Exception)
    {
        pagesStep.Fail("Failed to fetch pages");
        throw;
    }

    if (pages.Count == 0)
    {
        logger.LogWarning("No pages found for chapter {Chapter}.", selectedChapter.Number);
        return 1;
    }

    // Launch reader with navigation support
    var readerOptions = services.GetRequiredService<IOptions<ReaderOptions>>().Value;
    var allMangaOptions = services.GetService<IOptions<AllMangaOptions>>()?.Value;
    var displayTitle = $"{selectedManga.Title} - Chapter {selectedChapter.Number}";

    var readResult = await ReadWithNavigationAsync(
        readerOptions,
        selectedManga,
        selectedChapter,
        chapters,
        pages,
        mangaCatalog,
        logger,
        allMangaOptions?.Referer,
        allMangaOptions?.UserAgent,
        displayTitle,
        cancellationToken,
        startPage);

    // Save to reading history with last page position
    try
    {
        var readHistory = services.GetRequiredService<IReadHistoryStore>();
        var entry = new ReadHistoryEntry
        {
            Provider = "allmanga",
            MangaId = selectedManga.Id.Value,
            MangaTitle = selectedManga.Title,
            ChapterNumber = readResult.LastChapter,
            ChapterTitle = chapters.FirstOrDefault(c => Math.Abs(c.Number - readResult.LastChapter) < 0.001f)?.Title ?? selectedChapter.Title,
            LastPage = readResult.LastPage,
            ReadAt = DateTimeOffset.UtcNow
        };
        await readHistory.AddAsync(entry, cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "Failed to update read history.");
    }

    // Update manga tracking list (auto-adds if not present, auto-completes if finished)
    try
    {
        var mangaList = services.GetRequiredService<IMangaListStore>();
        var totalChapters = chapters.Count;
        await mangaList.RecordChapterReadAsync(
            selectedManga.Id.Value,
            selectedManga.Title,
            selectedChapter.Number,
            totalChapters,
            cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "Failed to update manga list tracking.");
    }

    return readResult.ExitCode;
}

/// <summary>
/// Launch the manga reader with the given pages.
/// </summary>
/// <param name="options">Reader options from configuration.</param>
/// <param name="pages">Chapter pages to display.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="httpReferrer">Optional HTTP Referer header.</param>
/// <param name="httpUserAgent">Optional User-Agent header.</param>
/// <param name="displayTitle">Window title for the reader.</param>
/// <param name="startPage">Starting page number for resume (1-indexed).</param>
/// <returns>Exit code from the reader process.</returns>
static int LaunchReader(ReaderOptions options, IReadOnlyCollection<ChapterPage> pages, IReadOnlyCollection<Chapter> chapters, Chapter currentChapter, ILogger logger, string? httpReferrer, string? httpUserAgent, string? displayTitle, string? navResultPath, int startPage = 1)
{
    var readerPath = ResolveReaderExecutable(options);
    if (readerPath is null)
    {
        logger.LogError("No supported reader found. Build Koware.Reader.Win or set Reader:Command in appsettings.json.");
        return 1;
    }

    // Handle macOS browser-based reader
    if (readerPath == "macos-browser")
    {
        return LaunchMacOSBrowserReader(pages, logger, displayTitle);
    }
    
    // Show which reader is being used
    var readerName = Path.GetFileNameWithoutExtension(readerPath);
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"{Icons.Book} ");
    Console.ResetColor();
    Console.Write($"Launching ");
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write(readerName);
    Console.ResetColor();
    Console.Write($" ({pages.Count} pages)");
    if (!string.IsNullOrWhiteSpace(displayTitle))
    {
        Console.Write($" — {displayTitle}");
    }
    Console.WriteLine();

    // Build pages JSON
    var pagesData = pages.Select(p => new { url = p.ImageUrl.ToString(), pageNumber = p.PageNumber }).ToArray();
    var pagesJson = JsonSerializer.Serialize(pagesData);
    var chaptersPayload = chapters.Select(c => new { number = c.Number, title = c.Title, read = c.Number < currentChapter.Number, current = c.Number == currentChapter.Number }).ToArray();
    var chaptersJson = JsonSerializer.Serialize(chaptersPayload);

    var start = new ProcessStartInfo
    {
        FileName = readerPath,
        UseShellExecute = false
    };

    start.ArgumentList.Add(pagesJson);
    start.ArgumentList.Add(string.IsNullOrWhiteSpace(displayTitle) ? "Koware Reader" : displayTitle!);
    start.ArgumentList.Add("--chapters");
    start.ArgumentList.Add(chaptersJson);
    if (!string.IsNullOrWhiteSpace(navResultPath))
    {
        start.ArgumentList.Add("--nav");
        start.ArgumentList.Add(navResultPath!);
    }

    if (!string.IsNullOrWhiteSpace(httpReferrer))
    {
        start.ArgumentList.Add("--referer");
        start.ArgumentList.Add(httpReferrer!);
    }

    if (!string.IsNullOrWhiteSpace(httpUserAgent))
    {
        start.ArgumentList.Add("--user-agent");
        start.ArgumentList.Add(httpUserAgent!);
    }

    if (startPage > 1)
    {
        start.ArgumentList.Add("--start-page");
        start.ArgumentList.Add(startPage.ToString());
    }

    return StartProcessAndWait(logger, start, readerPath);
}

static async Task<ReadResult> ReadWithNavigationAsync(
    ReaderOptions readerOptions,
    Manga selectedManga,
    Chapter selectedChapter,
    IReadOnlyCollection<Chapter> chapters,
    IReadOnlyCollection<ChapterPage> initialPages,
    IMangaCatalog mangaCatalog,
    ILogger logger,
    string? httpReferrer,
    string? httpUserAgent,
    string? displayTitle,
    CancellationToken cancellationToken,
    int startPage = 1)
{
    // create a temp file to capture navigation intent
    var navPath = Path.Combine(Path.GetTempPath(), $"koware-nav-{Guid.NewGuid():N}.txt");
    var orderedChapters = chapters.OrderBy(c => c.Number).ToArray();
    var currentChapter = selectedChapter;
    var pages = initialPages;
    var exitCode = 0;
    var lastNav = new NavigationResult("none", 1, currentChapter.Number);

    try
    {
        var currentStartPage = startPage;
        while (true)
        {
            File.WriteAllText(navPath, "none:1:0");
            exitCode = LaunchReader(readerOptions, pages, chapters, currentChapter, logger, httpReferrer, httpUserAgent, displayTitle, navPath, currentStartPage);
            currentStartPage = 1; // Reset for subsequent chapter navigation

            var nav = ReadNavigation(navPath);
            lastNav = nav;
            
            // Handle direct chapter jump (goto:chapterNumber)
            if (nav.Action.StartsWith("goto:", StringComparison.OrdinalIgnoreCase))
            {
                var targetChapterStr = nav.Action.Substring(5);
                if (float.TryParse(targetChapterStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var targetChapterNum))
                {
                    var targetChapter = orderedChapters.FirstOrDefault(c => Math.Abs(c.Number - targetChapterNum) < 0.001f);
                    if (targetChapter is not null)
                    {
                        currentChapter = targetChapter;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            else if (nav.Action is "next" or "prev")
            {
                var currentIndex = Array.FindIndex(orderedChapters, c => Math.Abs(c.Number - currentChapter.Number) < 0.001f);
                if (currentIndex < 0)
                {
                    break;
                }

                if (nav.Action == "next" && currentIndex + 1 < orderedChapters.Length)
                {
                    currentChapter = orderedChapters[currentIndex + 1];
                }
                else if (nav.Action == "prev" && currentIndex - 1 >= 0)
                {
                    currentChapter = orderedChapters[currentIndex - 1];
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }

            logger.LogInformation("Loading chapter {Chapter} ({Title})", currentChapter.Number, selectedManga.Title);
            pages = await mangaCatalog.GetPagesAsync(currentChapter, cancellationToken);
            displayTitle = $"{selectedManga.Title} - Chapter {currentChapter.Number}";
        }
    }
    finally
    {
        try { File.Delete(navPath); } catch { }
    }

    // Return the final position
    var finalChapter = lastNav.Chapter > 0 ? lastNav.Chapter : currentChapter.Number;
    return new ReadResult(exitCode, finalChapter, lastNav.Page);
}

static NavigationResult ReadNavigation(string path)
{
    try
    {
        var text = File.ReadAllText(path).Trim().ToLowerInvariant();
        // Format: action:page:chapter (e.g., "none:15:1.5")
        var parts = text.Split(':');
        if (parts.Length >= 3 && 
            int.TryParse(parts[1], out var page) && 
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var chapter))
        {
            return new NavigationResult(parts[0], page, chapter);
        }
        // Legacy format: just action
        return new NavigationResult(text, 1, 0);
    }
    catch
    {
        return new NavigationResult("none", 1, 0);
    }
}

/// <summary>
/// Launch a browser-based manga reader on macOS by creating a temporary HTML file.
/// </summary>
static int LaunchMacOSBrowserReader(IReadOnlyCollection<ChapterPage> pages, ILogger logger, string? displayTitle)
{
    var title = string.IsNullOrWhiteSpace(displayTitle) ? "Koware Reader" : displayTitle;
    var htmlContent = GenerateReaderHtml(pages, title!);
    
    // Create temp HTML file
    var tempDir = Path.Combine(Path.GetTempPath(), "koware-reader");
    Directory.CreateDirectory(tempDir);
    var htmlPath = Path.Combine(tempDir, $"reader-{Guid.NewGuid():N}.html");
    File.WriteAllText(htmlPath, htmlContent);
    
    logger.LogInformation("Opening reader in browser: {Path}", htmlPath);
    
    // Open in default browser using macOS 'open' command
    var start = new ProcessStartInfo
    {
        FileName = "open",
        Arguments = $"\"{htmlPath}\"",
        UseShellExecute = false
    };
    
    try
    {
        Process.Start(start);
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"{Icons.Book} Reader opened in browser. Press Enter when done reading...");
        Console.ResetColor();
        Console.ReadLine();
        
        // Cleanup temp file
        try { File.Delete(htmlPath); } catch { }
        
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to open browser reader");
        return 1;
    }
}

/// <summary>
/// Generate an HTML page for reading manga in the browser.
/// </summary>
static string GenerateReaderHtml(IReadOnlyCollection<ChapterPage> pages, string title)
{
    var escapedTitle = System.Net.WebUtility.HtmlEncode(title);
    var imagesHtml = string.Join("\n", pages.OrderBy(p => p.PageNumber).Select(p => 
        $"        <img src=\"{p.ImageUrl}\" alt=\"Page {p.PageNumber}\" loading=\"lazy\" />"));
    
    return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{escapedTitle}</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            background: #1a1a2e;
            color: #eee;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            min-height: 100vh;
        }}
        header {{
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            background: rgba(26, 26, 46, 0.95);
            backdrop-filter: blur(10px);
            padding: 12px 20px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            z-index: 100;
            border-bottom: 1px solid #333;
        }}
        h1 {{ font-size: 1.1rem; font-weight: 500; }}
        .page-info {{ color: #888; font-size: 0.9rem; }}
        .container {{
            max-width: 1000px;
            margin: 0 auto;
            padding: 70px 10px 20px;
        }}
        .page {{
            margin-bottom: 4px;
            display: flex;
            justify-content: center;
        }}
        img {{
            max-width: 100%;
            height: auto;
            display: block;
        }}
        .controls {{
            position: fixed;
            bottom: 20px;
            right: 20px;
            display: flex;
            gap: 8px;
        }}
        button {{
            background: #38bdf8;
            color: #000;
            border: none;
            padding: 10px 16px;
            border-radius: 8px;
            cursor: pointer;
            font-size: 14px;
            font-weight: 500;
        }}
        button:hover {{ background: #7dd3fc; }}
    </style>
</head>
<body>
    <header>
        <h1>{escapedTitle}</h1>
        <span class=""page-info"">{pages.Count} pages</span>
    </header>
    <div class=""container"">
{imagesHtml}
    </div>
    <div class=""controls"">
        <button onclick=""window.scrollTo({{top: 0, behavior: 'smooth'}})"">↑ Top</button>
    </div>
</body>
</html>";
}

/// <summary>
/// Resolve the reader executable path from options.
/// </summary>
/// <param name="options">Reader options.</param>
/// <returns>Full path to reader executable, or null if not found.</returns>
static string? ResolveReaderExecutable(ReaderOptions options)
{
    var command = options.Command;
    var isDefaultReader = string.IsNullOrWhiteSpace(command) || 
        command.Equals("Koware.Reader.Win.exe", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("Koware.Reader.Win", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("Koware.Reader.exe", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("Koware.Reader", StringComparison.OrdinalIgnoreCase);
    
    if (OperatingSystem.IsMacOS())
    {
        // On macOS: try bundled Avalonia reader from Koware.app bundle
        var macReaderCandidates = new[]
        {
            "/Applications/Koware.app/Contents/Resources/reader/Koware.Reader",
            "/usr/local/bin/koware/reader/Koware.Reader",
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "reader", "Koware.Reader"),
            Path.Combine(AppContext.BaseDirectory, "reader", "Koware.Reader"),
        };
        
        foreach (var candidate in macReaderCandidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
        
        // Fall back to browser-based reader if no bundled reader
        if (isDefaultReader)
        {
            return "macos-browser";
        }
    }
    
    if (string.IsNullOrWhiteSpace(command))
    {
        command = "Koware.Reader.Win.exe";
    }

    // Check if it's an absolute path
    if (Path.IsPathRooted(command) && File.Exists(command))
    {
        return command;
    }

    // Check in app directory
    var appDir = AppContext.BaseDirectory;
    var candidates = new[]
    {
        Path.Combine(appDir, command),
        Path.Combine(appDir, "Koware.Reader.Win.exe"),
        Path.Combine(appDir, "Koware.Reader.Win", "Koware.Reader.Win.exe"),
        Path.Combine(appDir, "Koware.Reader.exe"),
        Path.Combine(appDir, "Koware.Reader", "Koware.Reader.exe"),
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    // Check PATH
    var resolved = ResolveExecutablePath(command.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(resolved))
    {
        return resolved;
    }

    return null;
}

/// <summary>
/// Handle download command in manga mode: download chapter pages to disk.
/// </summary>
static async Task<int> HandleMangaDownloadAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    var queryParts = new List<string>();
    string? chaptersArg = null;
    float? singleChapter = null;
    int? preferredIndex = null;
    var nonInteractive = false;
    string? outputDir = null;

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if ((arg.Equals("--chapter", StringComparison.OrdinalIgnoreCase) || arg.Equals("--chapters", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
        {
            var value = args[i + 1];
            if (value.Contains('-') || value.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                chaptersArg = value;
            }
            else if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                singleChapter = parsed;
            }
            else
            {
                chaptersArg = value;
            }
            i++;
            continue;
        }

        if (arg.Equals("--index", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out var idx) && idx >= 1)
            {
                preferredIndex = idx;
                i++;
                continue;
            }
            logger.LogWarning("--index must be a positive integer.");
            return 1;
        }

        if (arg.Equals("--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            outputDir = args[++i];
            continue;
        }

        if (arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase))
        {
            nonInteractive = true;
            continue;
        }

        queryParts.Add(arg);
    }

    if (queryParts.Count == 0)
    {
        logger.LogWarning("download command is missing a search query.");
        WriteColoredLine("Usage: koware download <query> [--chapter <n|n-m|all>] [--dir <path>] [--index <n>] [--non-interactive]", ConsoleColor.Cyan);
        return 1;
    }

    var query = string.Join(' ', queryParts).Trim();
    var mangaCatalog = services.GetRequiredService<IMangaCatalog>();

    // Search for manga
    var searchStep = ConsoleStep.Start("Searching manga");
    var results = await mangaCatalog.SearchAsync(query, cancellationToken);
    searchStep.Succeed($"Found {results.Count} result(s)");

    if (results.Count == 0)
    {
        logger.LogWarning("No manga found for query: {Query}", query);
        return 1;
    }

    // Select manga
    Manga selectedManga;
    if (preferredIndex.HasValue)
    {
        var idx = preferredIndex.Value - 1;
        if (idx < 0 || idx >= results.Count)
        {
            logger.LogWarning("Index {Index} is out of range (1-{Count}).", preferredIndex.Value, results.Count);
            return 1;
        }
        selectedManga = results.ElementAt(idx);
    }
    else if (results.Count == 1 || nonInteractive)
    {
        selectedManga = results.First();
    }
    else
    {
        // Interactive selection
        var selectResult = InteractiveSelect.Show(
            results.ToList(),
            m => m.Title,
            new SelectorOptions<Manga>
            {
                Prompt = $"Select manga to download",
                MaxVisibleItems = 10,
                ShowSearch = true,
                SecondaryDisplayFunc = m => m.Synopsis ?? ""
            });

        if (selectResult.Cancelled)
        {
            logger.LogWarning("Selection cancelled.");
            return 1;
        }

        selectedManga = selectResult.Selected!;
    }

    logger.LogInformation("Selected: {Title}", selectedManga.Title);

    // Fetch chapters
    var chaptersStep = ConsoleStep.Start("Fetching chapters");
    var chapters = await mangaCatalog.GetChaptersAsync(selectedManga, cancellationToken);
    chaptersStep.Succeed($"Found {chapters.Count} chapter(s)");

    if (chapters.Count == 0)
    {
        logger.LogWarning("No chapters found for {Title}.", selectedManga.Title);
        return 1;
    }

    // Determine which chapters to download
    var orderedChapters = chapters.OrderBy(c => c.Number).ToList();
    var targetChapters = new List<Chapter>();

    if (singleChapter.HasValue)
    {
        var ch = orderedChapters.FirstOrDefault(c => Math.Abs(c.Number - singleChapter.Value) < 0.001f)
                 ?? orderedChapters.FirstOrDefault(c => (int)c.Number == (int)singleChapter.Value);
        if (ch is null)
        {
            logger.LogWarning("Chapter {Chapter} not found.", singleChapter.Value);
            return 1;
        }
        targetChapters.Add(ch);
    }
    else if (!string.IsNullOrWhiteSpace(chaptersArg))
    {
        if (chaptersArg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targetChapters.AddRange(orderedChapters);
        }
        else if (chaptersArg.Contains('-'))
        {
            var parts = chaptersArg.Split('-');
            if (parts.Length == 2 &&
                float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var from) &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var to))
            {
                targetChapters.AddRange(orderedChapters.Where(c => c.Number >= from && c.Number <= to));
            }
        }
    }
    else
    {
        // Interactive selection
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Available chapters: {orderedChapters.First().Number} - {orderedChapters.Last().Number} ({chapters.Count} total)");
        Console.ResetColor();
        Console.Write("Enter chapter(s) to download (e.g., 1, 1-10, all): ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            logger.LogInformation("Download cancelled.");
            return 0;
        }

        if (input.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targetChapters.AddRange(orderedChapters);
        }
        else if (input.Contains('-'))
        {
            var parts = input.Split('-');
            if (parts.Length == 2 &&
                float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var from) &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var to))
            {
                targetChapters.AddRange(orderedChapters.Where(c => c.Number >= from && c.Number <= to));
            }
        }
        else if (float.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num))
        {
            var ch = orderedChapters.FirstOrDefault(c => Math.Abs(c.Number - num) < 0.001f)
                     ?? orderedChapters.FirstOrDefault(c => (int)c.Number == (int)num);
            if (ch is not null)
            {
                targetChapters.Add(ch);
            }
        }
    }

    if (targetChapters.Count == 0)
    {
        logger.LogWarning("No chapters match the requested selection.");
        return 1;
    }

    // Prepare output directory
    var sanitizedTitle = SanitizeFileName(selectedManga.Title);
    var targetDir = string.IsNullOrWhiteSpace(outputDir)
        ? Path.Combine(Environment.CurrentDirectory, sanitizedTitle)
        : Path.Combine(outputDir, sanitizedTitle);
    Directory.CreateDirectory(targetDir);

    var allMangaOptions = services.GetService<IOptions<AllMangaOptions>>()?.Value;
    using var httpClient = new HttpClient();
    if (!string.IsNullOrWhiteSpace(allMangaOptions?.UserAgent))
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(allMangaOptions.UserAgent);
    }
    if (!string.IsNullOrWhiteSpace(allMangaOptions?.Referer))
    {
        httpClient.DefaultRequestHeaders.Referrer = new Uri(allMangaOptions.Referer);
    }

    logger.LogInformation("Downloading {Count} chapter(s) to {Dir}", targetChapters.Count, targetDir);

    var total = targetChapters.Count;
    var index = 0;
    var failedCount = 0;

    foreach (var chapter in targetChapters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index++;

        // Show progress bar
        var percent = (int)(index * 100.0 / total);
        var filled = (int)(index * 30.0 / total);
        var empty = 30 - filled;
        var bar = new string('█', filled) + new string('░', empty);
        var chapterLabel = chapter.Number % 1 == 0 ? $"{(int)chapter.Number}" : $"{chapter.Number:0.#}";
        System.Console.Write($"\r  [{bar}] {percent,3}% - Chapter {chapterLabel,-10}");

        var chapterDir = Path.Combine(targetDir, $"Chapter_{chapter.Number:000}");
        Directory.CreateDirectory(chapterDir);

        try
        {
            var pages = await mangaCatalog.GetPagesAsync(chapter, cancellationToken);
            var pageIndex = 0;
            foreach (var page in pages.OrderBy(p => p.PageNumber))
            {
                pageIndex++;
                var ext = GetImageExtension(page.ImageUrl.ToString());
                var fileName = $"{page.PageNumber:000}{ext}";
                var filePath = Path.Combine(chapterDir, fileName);

                if (File.Exists(filePath))
                {
                    continue; // Skip if already downloaded
                }

                var imageBytes = await httpClient.GetByteArrayAsync(page.ImageUrl, cancellationToken);
                await File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken);
            }

            // Record download in store
            var chapterDirInfo = new DirectoryInfo(chapterDir);
            var chapterSize = chapterDirInfo.EnumerateFiles().Sum(f => f.Length);
            var downloadStore = services.GetRequiredService<IDownloadStore>();
            await downloadStore.AddAsync(
                DownloadType.Chapter,
                selectedManga.Id.Value,
                selectedManga.Title,
                (int)chapter.Number,
                null,
                chapterDir,
                chapterSize,
                cancellationToken);
        }
        catch (Exception ex)
        {
            failedCount++;
            logger.LogWarning("Failed to download chapter {Chapter}: {Error}", chapter.Number, ex.Message);
        }
    }

    // Clear progress bar and show completion
    System.Console.Write("\r" + new string(' ', 70) + "\r");
    System.Console.ForegroundColor = ConsoleColor.Green;
    var successCount = total - failedCount;
    System.Console.WriteLine($"  ✔ {successCount} chapter(s) downloaded" + (failedCount > 0 ? $" ({failedCount} failed)" : ""));
    System.Console.ResetColor();

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Download complete. Saved to \"{targetDir}\".");
    Console.ResetColor();

    return 0;
}

/// <summary>
/// Get the image file extension from a URL.
/// </summary>
static string GetImageExtension(string url)
{
    var uri = new Uri(url);
    var path = uri.AbsolutePath;
    var ext = Path.GetExtension(path);
    if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5)
    {
        return ".jpg"; // Default to jpg
    }
    return ext;
}

/// <summary>
/// Sanitize a string for use as a file/directory name.
/// </summary>
static string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
    return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized.Trim();
}

/// <summary>
/// Normalize parse errors into a friendly warning and usage hint for a given command.
/// </summary>
/// <param name="command">The command name that failed parsing.</param>
/// <param name="ex">The parsing exception.</param>
/// <param name="logger">Logger instance.</param>
/// <returns>Always returns 1.</returns>
static int HandleParseError(string command, ArgumentException ex, ILogger logger)
{
    var canonical = command.Equals("play", StringComparison.OrdinalIgnoreCase) ? "watch" : command;
    var missingQuery = ex.Message.Contains("Query is required", StringComparison.OrdinalIgnoreCase);

    if (missingQuery)
    {
        logger.LogWarning("{Command} command is missing a search query.", canonical);
    }
    else
    {
        logger.LogWarning("Invalid arguments for {Command}: {Error}", canonical, ex.Message);
    }

    PrintFriendlyCommandHint(canonical);
    return 1;
}

/// <summary>
/// Print a short, user-friendly hint for incomplete or invalid command invocations.
/// </summary>
/// <param name="command">The command name to show hints for.</param>
/// <remarks>
/// Shows usage and example for watch, stream, search, and provider commands.
/// </remarks>
static void PrintFriendlyCommandHint(string command)
{
    switch (command.ToLowerInvariant())
    {
        case "watch":
            WriteColoredLine("Command looks incomplete. Add a search query to watch something.", ConsoleColor.Yellow);
            WriteColoredLine("Usage:   koware watch <query> [--episode <n>] [--quality <label>] [--index <match>] [--non-interactive]", ConsoleColor.Cyan);
            WriteColoredLine("Example: koware watch \"one piece\" --episode 1 --quality 1080p", ConsoleColor.Green);
            break;
        case "stream":
        case "plan":
            WriteColoredLine("Command looks incomplete. Add a search query to plan/stream.", ConsoleColor.Yellow);
            WriteColoredLine("Usage:   koware stream <query> [--episode <n>] [--quality <label>] [--index <match>] [--non-interactive] [--json]", ConsoleColor.Cyan);
            WriteColoredLine("Example: koware stream \"bleach\" --episode 1 --non-interactive", ConsoleColor.Green);
            break;
        case "search":
            WriteColoredLine("Command looks incomplete. Add a search query.", ConsoleColor.Yellow);
            WriteColoredLine("Usage:   koware search <query> [--json]", ConsoleColor.Cyan);
            WriteColoredLine("Example: koware search \"fullmetal alchemist\"", ConsoleColor.Green);
            break;
        case "provider":
            Console.WriteLine("Usage: koware provider [--enable <name> | --disable <name>]");
            Console.WriteLine("Shows provider status or toggles a provider on/off.");
            break;
        default:
            PrintUsage();
            break;
    }

    WriteColoredLine($"Tip: run 'koware help {command.ToLowerInvariant()}' for more details.", ConsoleColor.DarkGray);
}

/// <summary>
/// If no preferred match index is set, interactively (or heuristically) choose one from search results.
/// </summary>
/// <param name="orchestrator">Scraping orchestrator.</param>
/// <param name="plan">Current scrape plan.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A plan with PreferredMatchIndex set.</returns>
/// <remarks>
/// If NonInteractive is true, defaults to the first match.
/// Otherwise, prompts the user to select from the search results.
/// </remarks>
static async Task<ScrapePlan> MaybeSelectMatchAsync(ScrapeOrchestrator orchestrator, ScrapePlan plan, ILogger logger, CancellationToken cancellationToken)
{
    if (plan.PreferredMatchIndex.HasValue)
    {
        return plan;
    }

    if (plan.NonInteractive)
    {
        return plan with { PreferredMatchIndex = 1 };
    }

    var matches = await orchestrator.SearchAsync(plan.Query, cancellationToken);
    if (matches.Count == 0)
    {
        RenderSearch(plan.Query, matches);
        return plan;
    }

    if (matches.Count == 1)
    {
        return plan with { PreferredMatchIndex = 1 };
    }

    // Use interactive selector for multiple matches
    var result = InteractiveSelect.Show(
        matches.ToList(),
        a => a.Title,
        new SelectorOptions<Anime>
        {
            Prompt = $"Select anime for \"{plan.Query}\"",
            MaxVisibleItems = 10,
            ShowSearch = true,
            SecondaryDisplayFunc = a => a.Synopsis ?? ""
        });

    if (result.Cancelled)
    {
        logger.LogInformation("Selection canceled by user.");
        throw new OperationCanceledException("Selection canceled by user.");
    }

    return plan with { PreferredMatchIndex = result.SelectedIndex + 1 };
}

/// <summary>
/// If no episode number is set, interactively select one from available episodes.
/// </summary>
/// <param name="orchestrator">Scraping orchestrator.</param>
/// <param name="plan">Current scrape plan.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A plan with EpisodeNumber set.</returns>
/// <remarks>
/// If NonInteractive is true or an episode is already specified, returns the plan unchanged.
/// Otherwise, fetches episodes and prompts the user to select one.
/// </remarks>
static async Task<ScrapePlan> MaybeSelectEpisodeAsync(ScrapeOrchestrator orchestrator, ScrapePlan plan, ILogger logger, CancellationToken cancellationToken)
{
    // If episode already specified or non-interactive mode, skip selection
    if (plan.EpisodeNumber.HasValue || plan.NonInteractive)
    {
        return plan;
    }

    // Need to first get the selected anime to fetch its episodes
    if (!plan.PreferredMatchIndex.HasValue)
    {
        return plan;
    }

    var matches = await orchestrator.SearchAsync(plan.Query, cancellationToken);
    if (matches.Count == 0)
    {
        return plan;
    }

    var animeIndex = Math.Min(plan.PreferredMatchIndex.Value, matches.Count) - 1;
    var selectedAnime = matches.ElementAt(animeIndex);

    var episodes = await orchestrator.GetEpisodesAsync(selectedAnime, cancellationToken);
    if (episodes.Count == 0)
    {
        return plan;
    }

    // If only one episode, auto-select it
    if (episodes.Count == 1)
    {
        return plan with { EpisodeNumber = episodes.First().Number };
    }

    // Use interactive selector for multiple episodes (with DisableQuickJump for numeric input)
    var sortedEpisodes = episodes.OrderBy(e => e.Number).ToList();
    var result = InteractiveSelect.Show(
        sortedEpisodes,
        ep => string.IsNullOrWhiteSpace(ep.Title)
            ? $"Episode {ep.Number}"
            : $"Episode {ep.Number}: {ep.Title}",
        new SelectorOptions<Episode>
        {
            Prompt = $"Select episode for \"{selectedAnime.Title}\"",
            MaxVisibleItems = 15,
            ShowSearch = true,
            ShowPreview = false,
            DisableQuickJump = true // Allow typing episode numbers like "22"
        });

    if (result.Cancelled)
    {
        logger.LogInformation("Episode selection canceled by user.");
        throw new OperationCanceledException("Episode selection canceled by user.");
    }

    return plan with { EpisodeNumber = result.Selected?.Number };
}

/// <summary>
/// Pick the "best" stream from a list, preferring playlists/HLS and avoiding bad hosts.
/// </summary>
/// <param name="streams">Collection of available streams.</param>
/// <returns>The highest-scored stream, or null if none available.</returns>
/// <remarks>
/// Prefers .m3u8 HLS streams, then other playlists, then direct files.
/// Avoids hosts known to be unreliable (see <see cref="IsBadHost"/>).
/// </remarks>
static StreamLink? PickBestStream(IReadOnlyCollection<StreamLink> streams)
{
    var pool = streams.Where(s => !IsBadHost(s)).ToArray();
    if (pool.Length == 0)
    {
        pool = streams.ToArray();
    }

    var m3u8Preferred = pool
        .Where(s => s.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(ScoreStream)
        .ToArray();
    if (m3u8Preferred.Length > 0)
    {
        return m3u8Preferred.First();
    }

    var playlists = pool.Where(IsPlaylist).ToArray();
    if (playlists.Length > 0)
    {
        return playlists.OrderByDescending(ScoreStream).FirstOrDefault();
    }

    var candidates = pool.Where(s => !IsJsonStream(s)).ToArray();
    var best = candidates
        .OrderByDescending(ScoreStream)
        .FirstOrDefault();

    return best ?? pool.OrderByDescending(ScoreStream).FirstOrDefault();
}

/// <summary>
/// Filter and order streams based on what the chosen player can handle.
/// </summary>
/// <param name="streams">All available streams.</param>
/// <param name="playerName">Name of the resolved player (e.g., "vlc", "mpv").</param>
/// <param name="logger">Logger instance.</param>
/// <returns>Filtered and sorted stream collection.</returns>
/// <remarks>
/// Removes soft-sub-only streams if the player doesn't support external subtitles.
/// Orders by host priority and quality descending.
/// </remarks>
static IReadOnlyCollection<StreamLink> FilterStreamsForPlayer(IReadOnlyCollection<StreamLink> streams, string playerName, ILogger logger)
{
    var supportsSoftSubs = SupportsSoftSubtitles(playerName);
    var cautious = IsCautiousPlayer(playerName);

    var filtered = supportsSoftSubs
        ? streams
        : streams.Where(s => !s.RequiresSoftSubSupport).ToArray();

    if (!supportsSoftSubs && filtered.Count == 0)
    {
        logger.LogWarning("Player {Player} may not support external subtitles; using soft-sub streams anyway.", playerName);
        filtered = streams;
    }

    if (cautious)
    {
        filtered = filtered.Where(s => !(IsPlaylist(s) && s.RequiresSoftSubSupport)).ToArray();
        if (filtered.Count == 0)
        {
            filtered = streams;
        }
    }

    return filtered
        .OrderByDescending(s => ScoreStreamForPlayer(s, cautious))
        .ToArray();
}

/// <summary>
/// Return whether a player is assumed to support separate subtitle tracks.
/// </summary>
/// <param name="playerName">Player executable name.</param>
/// <returns>True if external subtitles are likely supported.</returns>
static bool SupportsSoftSubtitles(string playerName) =>
    !playerName.Contains("vlc", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// Players like VLC/Android struggle with some soft-sub HLS streams; treat them cautiously.
/// </summary>
static bool IsCautiousPlayer(string playerName) =>
    playerName.Contains("vlc", StringComparison.OrdinalIgnoreCase) ||
    playerName.Contains("android", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// Score a stream based on quality, protocol, host, and provider (higher is better).
/// </summary>
/// <param name="stream">The stream to score.</param>
/// <returns>An integer score; compare against other streams to pick the best.</returns>
/// <remarks>
/// Prefers HLS/DASH, HTTPS, known-good hosts (akamai), and high-quality labels.
/// Penalizes bad hosts, JSON descriptors, and segment URLs.
/// </remarks>
static int ScoreStream(StreamLink stream)
{
    var score = 0;
    var url = stream.Url.ToString();
    var host = stream.Url.Host;
    var provider = stream.Provider ?? string.Empty;
    var quality = stream.Quality ?? string.Empty;

    if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || quality.Contains("hls", StringComparison.OrdinalIgnoreCase))
    {
        score += 200;
    }

    if (quality.Contains("dash", StringComparison.OrdinalIgnoreCase) || url.Contains("dash", StringComparison.OrdinalIgnoreCase))
    {
        score += 150;
    }

    if (TryParseQualityNumber(quality, out var q))
    {
        score += q / 10;
    }

    score += stream.HostPriority * 5;

    if (stream.Url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        score += 20;
    }

    // Provider-aware tweaks similar to ani-cli
    if (provider.Contains("hianime", StringComparison.OrdinalIgnoreCase)
        || provider.Contains("wixmp", StringComparison.OrdinalIgnoreCase))
    {
        score += 80;
    }

    if (IsBadHost(stream))
    {
        score -= 500;
    }

    if (host.Contains("akamaized", StringComparison.OrdinalIgnoreCase) || host.Contains("akamai", StringComparison.OrdinalIgnoreCase))
    {
        score += 50;
    }

    if (IsJsonStream(stream))
    {
        score -= 300;
    }

    if (IsSegment(stream))
    {
        score -= 80;
    }

    return score;
}

/// <summary>
/// Adjust stream score for player-specific constraints (e.g., VLC/Android prefer direct files).
/// </summary>
static int ScoreStreamForPlayer(StreamLink stream, bool cautiousPlayer)
{
    var score = ScoreStream(stream);
    if (cautiousPlayer)
    {
        if (!IsPlaylist(stream) && !stream.RequiresSoftSubSupport)
        {
            score += 150;
        }

        if (IsPlaylist(stream))
        {
            score -= 50;
        }
    }

    return score;
}

/// <summary>
/// Identify hosts known to be unreliable or undesirable for playback/download.
/// </summary>
/// <param name="stream">The stream to check.</param>
/// <returns>True if the host is on the blocklist.</returns>
static bool IsBadHost(StreamLink stream)
{
    var host = stream.Url.Host;
    return host.Contains("haildrop", StringComparison.OrdinalIgnoreCase)
           || host.Contains("sharepoint", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Extract a numeric quality value (e.g., 1080 from "1080p"), if present.
/// </summary>
/// <param name="quality">Quality label like "1080p", "720p", "auto".</param>
/// <param name="value">Parsed integer value.</param>
/// <returns>True if a number was extracted.</returns>
static bool TryParseQualityNumber(string? quality, out int value)
{
    value = 0;
    if (string.IsNullOrWhiteSpace(quality))
    {
        return false;
    }

    var digits = new string(quality.Where(char.IsDigit).ToArray());
    if (digits.Length == 0)
    {
        return false;
    }

    return int.TryParse(digits, out value);
}

/// <summary>
/// Heuristic: is this URL a playlist/manifest (HLS/DASH) instead of a single file.
/// </summary>
/// <param name="stream">The stream to check.</param>
/// <returns>True if the URL looks like a playlist.</returns>
static bool IsPlaylist(StreamLink stream)
{
    var path = stream.Url.AbsolutePath;
    return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
           || path.Contains("manifest", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Heuristic: does this URL look like a media segment (chunk) rather than a full stream.
/// </summary>
/// <param name="stream">The stream to check.</param>
/// <returns>True if the URL looks like a segment.</returns>
static bool IsSegment(StreamLink stream)
{
    var path = stream.Url.AbsolutePath;
    return path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Heuristic: does this URL point to a JSON descriptor instead of media.
/// </summary>
/// <param name="stream">The stream to check.</param>
/// <returns>True if the URL ends with .json.</returns>
static bool IsJsonStream(StreamLink stream)
{
    var path = stream.Url.AbsolutePath;
    return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Ensure every stream has a usable referrer, falling back to a default when missing.
/// </summary>
static IReadOnlyCollection<StreamLink> ApplyDefaultReferrer(IReadOnlyCollection<StreamLink> streams, string? defaultReferrer)
{
    if (string.IsNullOrWhiteSpace(defaultReferrer))
    {
        return streams;
    }

    return streams
        .Select(s => string.IsNullOrWhiteSpace(s.Referrer) ? s with { Referrer = defaultReferrer } : s)
        .ToArray();
}

/// <summary>
/// Resolve the best watch history entry for a continue command with optional query.
/// Prefers exact title matches, then fuzzy matches, then falls back to the most recent entry.
/// </summary>
static async Task<WatchHistoryEntry?> ResolveWatchHistoryAsync(IWatchHistoryStore history, string? query, ILogger logger, CancellationToken cancellationToken)
{
    WatchHistoryEntry? entry = null;

    if (!string.IsNullOrWhiteSpace(query))
    {
        entry = await history.GetLastForAnimeAsync(query, cancellationToken);
        entry ??= await history.SearchLastAsync(query, cancellationToken);
    }

    entry ??= await history.GetLastAsync(cancellationToken);

    if (entry is not null && !string.IsNullOrWhiteSpace(query) && !entry.AnimeTitle.Equals(query, StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation("No exact history match for '{Query}'. Using most recent entry '{Title}'.", query, entry.AnimeTitle);
    }

    return entry;
}

/// <summary>
/// Resolve the best read history entry for a continue command with optional query.
/// Prefers exact title matches, then fuzzy matches, then falls back to the most recent entry.
/// </summary>
static async Task<ReadHistoryEntry?> ResolveReadHistoryAsync(IReadHistoryStore history, string? query, ILogger logger, CancellationToken cancellationToken)
{
    ReadHistoryEntry? entry = null;

    if (!string.IsNullOrWhiteSpace(query))
    {
        entry = await history.GetLastForMangaAsync(query, cancellationToken);
        entry ??= await history.SearchLastAsync(query, cancellationToken);
    }

    entry ??= await history.GetLastAsync(cancellationToken);

    if (entry is not null && !string.IsNullOrWhiteSpace(query) && !entry.MangaTitle.Equals(query, StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation("No exact history match for '{Query}'. Using most recent entry '{Title}'.", query, entry.MangaTitle);
    }

    return entry;
}

/// <summary>
/// Build a human-friendly player window title from anime, episode, and quality.
/// </summary>
/// <param name="result">Scrape result with selected anime/episode.</param>
/// <param name="stream">The stream being played.</param>
/// <returns>A title string like "One Piece — Episode 1000 — 1080p".</returns>
static string BuildPlayerTitle(ScrapeResult result, StreamLink stream)
{
    var parts = new List<string>();
    if (result.SelectedAnime is not null)
    {
        parts.Add(result.SelectedAnime.Title);
    }

    if (result.SelectedEpisode is not null)
    {
        var episodeTitle = string.IsNullOrWhiteSpace(result.SelectedEpisode.Title)
            ? $"Episode {result.SelectedEpisode.Number}"
            : $"Episode {result.SelectedEpisode.Number}: {result.SelectedEpisode.Title}";
        parts.Add(episodeTitle);
    }

    if (!string.IsNullOrWhiteSpace(stream.Quality))
    {
        parts.Add(stream.Quality!);
    }

    return parts.Count == 0 ? stream.Url.ToString() : string.Join(" — ", parts);
}

/// <summary>
/// Download a single stream via HttpClient to the given file path.
/// </summary>
/// <param name="httpClient">Reusable HTTP client.</param>
/// <param name="stream">The stream to download.</param>
/// <param name="outputPath">Destination file path.</param>
/// <param name="httpReferrer">Optional HTTP Referer header.</param>
/// <param name="httpUserAgent">Optional User-Agent header.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="cancellationToken">Cancellation token.</param>
static async Task DownloadWithHttpAsync(HttpClient httpClient, StreamLink stream, string outputPath, string? httpReferrer, string? httpUserAgent, ILogger logger, CancellationToken cancellationToken)
{
    var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);

    if (!string.IsNullOrWhiteSpace(httpReferrer) && Uri.TryCreate(httpReferrer, UriKind.Absolute, out var refUri))
    {
        request.Headers.Referrer = refUri;
    }

    if (!string.IsNullOrWhiteSpace(httpUserAgent))
    {
        request.Headers.TryAddWithoutValidation("User-Agent", httpUserAgent);
    }

    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    response.EnsureSuccessStatusCode();

    var totalBytes = response.Content.Headers.ContentLength;

    await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
    await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

    var buffer = new byte[81920];
    long readTotal = 0;
    int read;

    // Create progress bar if we know the total size
    using var progressBar = totalBytes.HasValue && totalBytes.Value > 0
        ? new Koware.Cli.Console.ConsoleProgressBar("Downloading", totalBytes.Value)
        : null;

    var lastReportTime = DateTime.UtcNow;

    while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
    {
        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        readTotal += read;

        // Report progress at most every 100ms to avoid console flicker
        var now = DateTime.UtcNow;
        if (progressBar is not null && (now - lastReportTime).TotalMilliseconds >= 100)
        {
            progressBar.Report(readTotal);
            lastReportTime = now;
        }
    }

    // Complete the progress bar
    progressBar?.Complete("Download complete");
}

/// <summary>
/// Build an HTTP headers blob suitable for ffmpeg's -headers argument.
/// </summary>
/// <param name="httpReferrer">Optional Referer header.</param>
/// <param name="httpUserAgent">Optional User-Agent header.</param>
/// <returns>A CRLF-delimited header string, or empty if none provided.</returns>
static string BuildFfmpegHeaders(string? httpReferrer, string? httpUserAgent)
{
    var parts = new List<string>();

    if (!string.IsNullOrWhiteSpace(httpReferrer))
    {
        parts.Add($"Referer: {httpReferrer}");
    }

    if (!string.IsNullOrWhiteSpace(httpUserAgent))
    {
        parts.Add($"User-Agent: {httpUserAgent}");
    }

    if (parts.Count == 0)
    {
        return string.Empty;
    }

    return string.Join("\r\n", parts) + "\r\n";
}

/// <summary>
/// Download a playlist stream using ffmpeg, copying streams directly to a file.
/// </summary>
/// <param name="ffmpegPath">Absolute path to ffmpeg executable.</param>
/// <param name="stream">The stream to download.</param>
/// <param name="outputPath">Destination file path.</param>
/// <param name="httpReferrer">Optional HTTP Referer header.</param>
/// <param name="httpUserAgent">Optional User-Agent header.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <remarks>
/// Uses "-c copy" to remux without re-encoding. Kills ffmpeg on cancellation.
/// </remarks>
static async Task DownloadWithFfmpegAsync(string ffmpegPath, StreamLink stream, string outputPath, string? httpReferrer, string? httpUserAgent, ILogger logger, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    var start = new ProcessStartInfo
    {
        FileName = ffmpegPath,
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };

    start.ArgumentList.Add("-y");
    start.ArgumentList.Add("-loglevel");
    start.ArgumentList.Add("error");

    var headers = BuildFfmpegHeaders(httpReferrer, httpUserAgent);
    if (!string.IsNullOrWhiteSpace(headers))
    {
        start.ArgumentList.Add("-headers");
        start.ArgumentList.Add(headers);
    }

    start.ArgumentList.Add("-i");
    start.ArgumentList.Add(stream.Url.ToString());
    start.ArgumentList.Add("-c");
    start.ArgumentList.Add("copy");
    start.ArgumentList.Add("-bsf:a");
    start.ArgumentList.Add("aac_adtstoasc");
    start.ArgumentList.Add(outputPath);

    using var process = new Process { StartInfo = start };

    try
    {
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        _ = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();

        await Task.Run(() => process.WaitForExit(), cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}.");
        }
    }
    catch
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        throw;
    }
}

/// <summary>
/// Try to resolve an executable path for a given command name, probing local bins and PATH.
/// </summary>
/// <param name="command">Command name like "ffmpeg", "vlc", or a full path.</param>
/// <returns>Absolute path to the executable, or null if not found.</returns>
/// <remarks>
/// Searches: explicit path, local bin directories, well-known install paths, then PATH.
/// </remarks>
static string? ResolveExecutablePath(string command)
{
    if (string.IsNullOrWhiteSpace(command))
    {
        return null;
    }

    var trimmed = command.Trim('"');
    if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
    {
        return trimmed;
    }

    var isNativePlayer = trimmed.StartsWith("Koware.Player.Win", StringComparison.OrdinalIgnoreCase);

    if (isNativePlayer)
    {
        var localBinRoots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Koware.Player.Win", "bin"),
            Path.Combine(Environment.CurrentDirectory, "Koware.Player.Win", "bin")
        };

        foreach (var root in localBinRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var candidatePath = Directory
                .EnumerateFiles(root, "Koware.Player.Win.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (candidatePath is not null)
            {
                return Path.GetFullPath(candidatePath);
            }
        }
    }

    var candidates = Path.HasExtension(trimmed)
        ? new[] { trimmed }
        : new[] { trimmed, $"{trimmed}.exe" };

    var probeRoots = new[]
    {
        AppContext.BaseDirectory,
        Environment.CurrentDirectory
    };

    foreach (var root in probeRoots)
    {
        foreach (var candidate in candidates)
        {
            var full = Path.Combine(root, candidate);
            if (File.Exists(full))
            {
                return Path.GetFullPath(full);
            }
        }
    }

    var wellKnown = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
    };

    foreach (var path in wellKnown)
    {
        if (File.Exists(path) && trimmed.Equals("vlc", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
    }

    var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var dir in paths)
    {
        foreach (var candidate in candidates)
        {
            var fullPath = Path.Combine(dir, candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
    }

    return null;
}

/// <summary>
/// Pick a concrete player executable (Koware.Player.Win, vlc, mpv, etc.) based on options.
/// </summary>
/// <param name="options">Player options from config (may specify a custom command).</param>
/// <returns>A <see cref="PlayerResolution"/> with the resolved path, name, and tried candidates.</returns>
/// <remarks>
/// Tries the configured command first, then falls back to Koware.Player.Win, vlc, mpv.
/// </remarks>
static PlayerResolution ResolvePlayerExecutable(PlayerOptions options)
{
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(options.Command))
    {
        candidates.Add(options.Command);
    }
    
    if (OperatingSystem.IsMacOS())
    {
        // macOS: prefer IINA/mpv (native, works best), bundled Avalonia player requires LibVLC which has ARM64 issues
        candidates.AddRange(new[] { 
            "iina", 
            "mpv", 
            "/Applications/Koware.app/Contents/Resources/player/Koware.Player",
            "/usr/local/bin/koware/player/Koware.Player",
            "vlc" 
        });
    }
    else
    {
        // Windows: prefer bundled WPF player, then Avalonia, then VLC, then mpv
        candidates.AddRange(new[] { "Koware.Player.Win", "Koware.Player.Win.exe", "Koware.Player", "Koware.Player.exe", "vlc", "mpv" });
    }
    
    candidates = candidates
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var resolved = candidates
        .Select(c => new { Command = c, Path = ResolveExecutablePath(c) })
        .FirstOrDefault(x => x.Path is not null);

    var playerName = resolved?.Path is null
        ? (candidates.FirstOrDefault() ?? "unknown")
        : Path.GetFileNameWithoutExtension(resolved.Path);

    return new PlayerResolution(resolved?.Path, playerName, candidates);
}

/// <summary>
/// Launch the resolved media player with appropriate arguments and HTTP headers/subtitles.
/// </summary>
/// <param name="options">Player options from config.</param>
/// <param name="stream">The stream to play.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="httpReferrer">Optional HTTP Referer header.</param>
/// <param name="httpUserAgent">Optional User-Agent header.</param>
/// <param name="displayTitle">Window title for the player.</param>
/// <param name="resolution">Pre-resolved player; if null, will be resolved.</param>
/// <returns>Exit code from the player process.</returns>
/// <remarks>
/// Handles Koware.Player.Win, vlc, and mpv with appropriate argument styles.
/// </remarks>
static int LaunchPlayer(PlayerOptions options, StreamLink stream, ILogger logger, string? httpReferrer, string? httpUserAgent, string? displayTitle, PlayerResolution? resolution = null)
{
    resolution ??= ResolvePlayerExecutable(options);

    if (resolution.Path is null)
    {
        var hint = OperatingSystem.IsMacOS() 
            ? "Install IINA (brew install --cask iina) or mpv (brew install mpv), or set Player:Command in config."
            : "Build Koware.Player.Win or set Player:Command in appsettings.json.";
        logger.LogError("No supported player found (tried {Candidates}). {Hint}", string.Join(", ", resolution.Candidates), hint);
        return 1;
    }

    var playerPath = resolution.Path;
    var playerName = resolution.Name;
    var subtitle = stream.Subtitles.FirstOrDefault();
    
    // Show which player is being used
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"{Icons.Play} ");
    Console.ResetColor();
    Console.Write($"Launching ");
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write(playerName);
    Console.ResetColor();
    if (!string.IsNullOrWhiteSpace(displayTitle))
    {
        Console.Write($" — {displayTitle}");
    }
    Console.WriteLine();

    // Handle bundled Koware players (Windows and cross-platform Avalonia)
    if (string.Equals(playerName, "Koware.Player.Win", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(playerName, "Koware.Player", StringComparison.OrdinalIgnoreCase))
    {
        var start = new ProcessStartInfo
        {
            FileName = playerPath,
            UseShellExecute = false
        };

        start.ArgumentList.Add(stream.Url.ToString());
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(displayTitle) ? stream.Url.ToString() : displayTitle!);

        if (!string.IsNullOrWhiteSpace(httpReferrer))
        {
            start.ArgumentList.Add("--referer");
            start.ArgumentList.Add(httpReferrer!);
        }

        if (!string.IsNullOrWhiteSpace(httpUserAgent))
        {
            start.ArgumentList.Add("--user-agent");
            start.ArgumentList.Add(httpUserAgent!);
        }

        if (subtitle is not null)
        {
            start.ArgumentList.Add("--subtitle");
            start.ArgumentList.Add(subtitle.Url.ToString());
            if (!string.IsNullOrWhiteSpace(subtitle.Label))
            {
                start.ArgumentList.Add("--subtitle-label");
                start.ArgumentList.Add(subtitle.Label);
            }
        }

        return StartProcessAndWait(logger, start, playerPath);
    }

    var defaultArgs = string.Empty;
    var isMpvLike = string.Equals(playerName, "mpv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(playerName, "iina", StringComparison.OrdinalIgnoreCase);
    
    if (string.Equals(playerName, "vlc", StringComparison.OrdinalIgnoreCase))
    {
        defaultArgs = "--play-and-exit --quiet";
    }
    else if (isMpvLike)
    {
        defaultArgs = "--no-terminal --force-window=yes";
    }

    var argsPrefix = string.IsNullOrWhiteSpace(options.Args) ? defaultArgs : options.Args;

    var headerArgs = new List<string>();
    if (!string.IsNullOrWhiteSpace(httpReferrer))
    {
        if (string.Equals(playerName, "vlc", StringComparison.OrdinalIgnoreCase))
        {
            headerArgs.Add($"--http-referrer=\"{httpReferrer}\"");
        }
        else if (isMpvLike)
        {
            headerArgs.Add($"--referrer=\"{httpReferrer}\"");
        }
    }

    if (!string.IsNullOrWhiteSpace(httpUserAgent))
    {
        if (string.Equals(playerName, "vlc", StringComparison.OrdinalIgnoreCase))
        {
            headerArgs.Add($"--http-user-agent=\"{httpUserAgent}\"");
        }
        else if (isMpvLike)
        {
            headerArgs.Add($"--user-agent=\"{httpUserAgent}\"");
        }
    }

    var argParts = new List<string>();
    if (!string.IsNullOrWhiteSpace(argsPrefix))
    {
        argParts.Add(argsPrefix);
    }
    argParts.AddRange(headerArgs);

    var arguments = argParts.Count == 0
        ? $"\"{stream.Url}\""
        : $"{string.Join(" ", argParts)} \"{stream.Url}\"";

    var startInfo = new ProcessStartInfo
    {
        FileName = playerPath,
        Arguments = arguments,
        UseShellExecute = false
    };

    return StartProcessAndWait(logger, startInfo, playerPath, arguments);
}

/// <summary>
/// Interactive loop that lets the user retry playback with different quality labels.
/// </summary>
/// <param name="streams">Available streams to choose from.</param>
/// <param name="options">Player options.</param>
/// <param name="player">Resolved player.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="httpReferrer">Optional HTTP Referer header.</param>
/// <param name="httpUserAgent">Optional User-Agent header.</param>
/// <param name="displayTitle">Window title for the player.</param>
/// <param name="lastExitCode">Exit code from the previous playback.</param>
/// <returns>Final exit code after all replays.</returns>
/// <remarks>
/// Prompts the user to type a quality label; Enter exits the loop.
/// </remarks>
static int ReplayWithDifferentQuality(
    IReadOnlyCollection<StreamLink> streams,
    PlayerOptions options,
    PlayerResolution player,
    ILogger logger,
    string? httpReferrer,
    string? httpUserAgent,
    string? displayTitle,
    int lastExitCode)
{
    var ordered = streams.ToArray();
    if (ordered.Length <= 1)
    {
        return lastExitCode;
    }

    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("Available qualities:");
        Console.WriteLine(string.Join(", ", ordered.Select(s => s.Quality).Distinct()));
        Console.Write("Press Enter to exit, or type a quality to replay: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return lastExitCode;
        }

        var next = ordered.FirstOrDefault(s => string.Equals(s.Quality, input, StringComparison.OrdinalIgnoreCase))
                   ?? ordered.FirstOrDefault(s => s.Quality.Contains(input, StringComparison.OrdinalIgnoreCase));

        if (next is null)
        {
            Console.WriteLine("Quality not found. Try one of the listed labels.");
            continue;
        }

        logger.LogInformation("Replaying with quality {Quality} ({Provider})", next.Quality, next.Provider);
        lastExitCode = LaunchPlayer(options, next, logger, next.Referrer ?? httpReferrer, httpUserAgent, displayTitle, player);
    }
}

/// <summary>
/// Start a process with the given start info, wait for exit, and log basic diagnostics.
/// </summary>
/// <param name="logger">Logger instance.</param>
/// <param name="start">Process start info.</param>
/// <param name="command">Command name for logging.</param>
/// <param name="arguments">Optional arguments string for logging.</param>
/// <returns>Process exit code, or 1 on failure.</returns>
static int StartProcessAndWait(ILogger logger, ProcessStartInfo start, string command, string? arguments = null)
{
    var formattedArgs = start.ArgumentList.Count > 0
        ? string.Join(" ", start.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
        : arguments ?? string.Empty;

    try
    {
        logger.LogDebug("Launching player: {Player} {Args}", command, formattedArgs);

        using var proc = Process.Start(start);
        if (proc is null)
        {
            logger.LogError("Failed to start player process.");
            return 1;
        }

        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            logger.LogWarning("Player exited with code {ExitCode}.", proc.ExitCode);
        }

        return proc.ExitCode;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unable to launch player {Command}", command);
        return 1;
    }
}

/// <summary>
/// Parse CLI arguments for search/stream/watch/download into a ScrapePlan with defaults applied.
/// </summary>
/// <param name="args">CLI arguments; first element is the command name.</param>
/// <param name="defaults">Default CLI options for quality and match index.</param>
/// <returns>A populated <see cref="ScrapePlan"/>.</returns>
/// <exception cref="ArgumentException">Thrown if query is missing or episode is invalid.</exception>
/// <remarks>
/// Supports --episode, --quality, --index, --non-interactive flags.
/// Positional episode number (last arg if numeric) is also supported.
/// </remarks>
static ScrapePlan ParsePlan(string[] args, DefaultCliOptions defaults)
{
    var queryParts = new List<string>();
    int? episodeNumber = null;
    string? preferredQuality = null;
    int? preferredIndex = null;
    var nonInteractive = false;

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--episode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out var parsedEpisode))
            {
                if (parsedEpisode <= 0)
                {
                    throw new ArgumentException("Episode number must be greater than zero", nameof(args));
                }

                episodeNumber = parsedEpisode;
                i++;
                continue;
            }

            throw new ArgumentException("Episode number must be an integer", nameof(args));
        }

        if (arg.Equals("--quality", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            preferredQuality = args[i + 1];
            i++;
            continue;
        }

        if (arg.Equals("--index", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (!int.TryParse(args[i + 1], out var parsedIndex) || parsedIndex < 1)
            {
                throw new ArgumentException("--index must be a positive integer.", nameof(args));
            }

            preferredIndex = parsedIndex;
            i++;
            continue;
        }

        if (arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase))
        {
            nonInteractive = true;
            continue;
        }

        queryParts.Add(arg);
    }

    if (queryParts.Count == 0)
    {
        throw new ArgumentException("Query is required", nameof(args));
    }

    if (episodeNumber is null && queryParts.Count > 1 && int.TryParse(queryParts[^1], out var positionalEpisode))
    {
        if (positionalEpisode <= 0)
        {
            throw new ArgumentException("Episode number must be greater than zero", nameof(args));
        }

        episodeNumber = positionalEpisode;
        queryParts.RemoveAt(queryParts.Count - 1);
    }

    var query = string.Join(' ', queryParts).Trim();
    if (string.IsNullOrWhiteSpace(query))
    {
        throw new ArgumentException("Query is required", nameof(args));
    }

    if (string.IsNullOrWhiteSpace(preferredQuality))
    {
        preferredQuality = defaults.Quality;
    }

    if (!preferredIndex.HasValue && defaults.PreferredMatchIndex.HasValue && defaults.PreferredMatchIndex.Value > 0)
    {
        preferredIndex = defaults.PreferredMatchIndex;
    }

    return new ScrapePlan(query, episodeNumber, preferredQuality, preferredIndex, nonInteractive);
}

/// <summary>
/// Pretty-print the results of a search query with colored indices and detail URLs.
/// </summary>
/// <param name="query">The search query string.</param>
/// <param name="matches">Collection of matching anime results.</param>
static void RenderSearch(string query, IReadOnlyCollection<Anime> matches)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"{Icons.Search} ");
    Console.ResetColor();
    Console.Write($"Search results for ");
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write($"\"{query}\"");
    Console.ResetColor();
    Console.WriteLine($" ({matches.Count} found)");
    Console.WriteLine(new string('─', Math.Min(60, Console.WindowWidth - 2)));
    
    if (matches.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  No results found. Try a different query.");
        Console.ResetColor();
        return;
    }

    var index = 1;
    foreach (var anime in matches)
    {
        var color = TextColorer.ForMatchIndex(index - 1, matches.Count);
        Console.ForegroundColor = color;
        Console.Write($"  [{index,2}] ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(anime.Title);
        Console.ResetColor();
        index++;
    }
    
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  Use: koware watch \"{query}\" --index <n>");
    Console.ResetColor();
}

/// <summary>
/// Pretty-print the results of a manga search query with colored indices and detail URLs.
/// </summary>
/// <param name="query">The search query string.</param>
/// <param name="matches">Collection of matching manga results.</param>
static void RenderMangaSearch(string query, IReadOnlyCollection<Manga> matches)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.Write("📚 ");
    Console.ResetColor();
    Console.Write($"Manga results for ");
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write($"\"{query}\"");
    Console.ResetColor();
    Console.WriteLine($" ({matches.Count} found)");
    Console.WriteLine(new string('─', Math.Min(60, Console.WindowWidth - 2)));
    
    if (matches.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  No results found. Try a different query.");
        Console.ResetColor();
        return;
    }

    var index = 1;
    foreach (var manga in matches)
    {
        var color = TextColorer.ForMatchIndex(index - 1, matches.Count);
        Console.ForegroundColor = color;
        Console.Write($"  [{index,2}] ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(manga.Title);
        Console.ResetColor();
        if (!string.IsNullOrWhiteSpace(manga.Synopsis))
        {
            var synopsis = manga.Synopsis.Length > 50 ? manga.Synopsis[..47] + "..." : manga.Synopsis;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($" — {synopsis}");
            Console.ResetColor();
        }
        Console.WriteLine();
        index++;
    }
    
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  Use: koware read \"{query}\" --index <n>");
    Console.ResetColor();
}

/// <summary>
/// Summarize the current scrape plan: selected anime, episodes, and top streams.
/// </summary>
/// <param name="plan">The current scrape plan.</param>
/// <param name="result">The result from executing the plan.</param>
static void RenderPlan(ScrapePlan plan, ScrapeResult result)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Query: {plan.Query}");
    Console.ResetColor();
    Console.WriteLine($"Matches: {result.Matches.Count}");

    if (result.SelectedAnime is null)
    {
        Console.WriteLine("No anime selected yet. Use 'search' to refine your query.");
        return;
    }

    Console.WriteLine($"Selected: {result.SelectedAnime.Title}");

    if (result.Episodes is not null)
    {
        Console.WriteLine("Episodes:");
        foreach (var episode in result.Episodes.Take(5))
        {
            Console.WriteLine($"  Ep {episode.Number}: {episode.Title}");
        }

        if (result.Episodes.Count > 5)
        {
            Console.WriteLine($"  ...and {result.Episodes.Count - 5} more");
        }
    }

    if (result.SelectedEpisode is not null)
    {
        Console.WriteLine($"Selected Episode: {result.SelectedEpisode.Number} ({result.SelectedEpisode.Title})");
    }

    if (result.Streams is not null)
    {
        Console.WriteLine("Streams:");
        var ordered = result.Streams.OrderByDescending(ScoreStream).ToArray();
        var toShow = ordered.Take(5).ToArray();
        foreach (var stream in toShow)
        {
            var subtitleLabel = stream.Subtitles.Count == 0
                ? "no subs"
                : string.Join(", ", stream.Subtitles.Select(s =>
                    string.IsNullOrWhiteSpace(s.Language)
                        ? s.Label
                        : $"{s.Label} ({s.Language})"));
            var sourceLabel = string.IsNullOrWhiteSpace(stream.SourceTag) ? stream.Provider : stream.SourceTag;
            Console.WriteLine($"  [{sourceLabel}] {stream.Quality} -> {stream.Url} (subs: {subtitleLabel})");
        }

        if (ordered.Length > toShow.Length)
        {
            Console.WriteLine($"  ...and {ordered.Length - toShow.Length} more");
        }
    }
}

/// <summary>
/// Print a minimal banner with version and copyright when no arguments are provided.
/// </summary>
static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Koware CLI {GetVersionLabel()}");
    Console.ResetColor();
    Console.WriteLine("Copyright © Ilgaz Mehmetoglu. All rights reserved.");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("Type 'koware help' to see available commands.");
    Console.ResetColor();
}

/// <summary>
/// Print the high-level CLI usage and a one-line description for each command.
/// </summary>
static void PrintUsage()
{
    WriteHeader($"Koware CLI {GetVersionLabel()}");
    Console.WriteLine("Usage:");
    WriteCommand("search <query> [--genre <g>] [--year <y>] [--status <s>] [--sort <o>]", "Find anime/manga with filters.", ConsoleColor.Cyan);
    WriteCommand("stream <query> [--episode <n>] [--quality <label>] [--index <n>] [--non-interactive]", "Show plan + streams, no player.", ConsoleColor.Cyan);
    WriteCommand("watch <query> [--episode <n>] [--quality <label>] [--index <n>] [--non-interactive]", "Pick a stream and play (alias: play).", ConsoleColor.Green);
    WriteCommand("play <query> [--episode <n>] [--quality <label>] [--index <n>] [--non-interactive]", "Same as watch.", ConsoleColor.Green);
    WriteCommand("download <query>", "Download episodes or full shows to disk.", ConsoleColor.Green);
    WriteCommand("read <query> [--chapter <n>]", "Read manga chapters in the reader.", ConsoleColor.Green);
    WriteCommand("last [--play] [--json]", "Show or replay your most recent watch.", ConsoleColor.Yellow);
    WriteCommand("continue [<anime>] [--from <episode>] [--quality <label>]", "Resume from history (auto next episode).", ConsoleColor.Yellow);
    WriteCommand("history [options]", "Browse/search history; play entries or show stats.", ConsoleColor.Yellow);
    WriteCommand("list [subcommand]", "Track anime: add, update status, mark complete.", ConsoleColor.Yellow);
    WriteCommand("recommend [--limit <n>]", "Get personalized recommendations (alias: rec).", ConsoleColor.Yellow);
    WriteCommand("offline [--stats] [--cleanup]", "Show downloaded content available offline.", ConsoleColor.Yellow);
    WriteCommand("help [command]", "Show this guide or a command-specific help page.", ConsoleColor.Magenta);
    WriteCommand("config [show|set|get|path]", "View or edit appsettings.user.json (player/reader/defaults).", ConsoleColor.Magenta);
    WriteCommand("mode [anime|manga]", "Show or switch between anime and manga modes.", ConsoleColor.Magenta);
    WriteCommand("doctor", "Full health check: config, providers, player, external tools.", ConsoleColor.Magenta);
    WriteCommand("provider [options]", "List/enable/disable providers.", ConsoleColor.Magenta);
    WriteCommand("update", "Download and launch the latest Koware installer.", ConsoleColor.Magenta);
}

/// <summary>
/// Get help text for a command as a list of lines for display in the FZF selector.
/// </summary>
static List<string> GetHelpLines(string command, CliMode mode)
{
    var lines = new List<string>();
    
    switch (command.ToLowerInvariant())
    {
        case "search":
            lines.Add("search - Find anime or manga with optional filters");
            lines.Add("");
            lines.Add("Usage: koware search <query> [filters]");
            lines.Add("Mode : searches anime or manga based on current mode");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --json             Output results as JSON");
            lines.Add("");
            lines.Add("Filters:");
            lines.Add("  --genre <name>     Filter by genre (Action, Romance, Fantasy, etc.)");
            lines.Add("  --year <year>      Filter by release year (e.g., 2024)");
            lines.Add("  --status <status>  Filter by status: ongoing, completed, upcoming");
            lines.Add("  --sort <order>     Sort by: popular, score, recent, title");
            lines.Add("  --score <min>      Minimum score filter");
            lines.Add("  --country <code>   Country: JP (Japan), KR (Korea), CN (China)");
            lines.Add("");
            lines.Add("Browse mode:");
            lines.Add("  Use filters without a query to browse (e.g., koware search --sort popular)");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware search \"demon slayer\"");
            lines.Add("  koware search --genre action --status ongoing");
            lines.Add("  koware search \"romance\" --year 2024 --sort popular");
            lines.Add("  koware search --sort popular --json");
            break;
            
        case "recommend":
            lines.Add("recommend - Get personalized recommendations based on your history");
            lines.Add("");
            lines.Add("Usage: koware recommend [--limit <n>] [--json]");
            lines.Add("Alias: rec");
            lines.Add("Mode : recommends anime or manga based on current mode");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --limit <n>  Number of recommendations (default: 10, max: 50)");
            lines.Add("  --json       Output as JSON");
            lines.Add("");
            lines.Add("Behavior:");
            lines.Add("  - Finds popular content you haven't watched/read yet");
            lines.Add("  - Excludes titles already in your tracking list");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware recommend");
            lines.Add("  koware recommend --limit 20");
            break;
            
        case "stream":
            lines.Add("stream - Plan stream selection and print the resolved streams");
            lines.Add("Alias: plan");
            lines.Add("");
            lines.Add("Usage: koware stream <query> [options]");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --episode <n>       Episode number");
            lines.Add("  --quality <label>   Quality (e.g., 1080p, 720p, auto)");
            lines.Add("  --index <match>     Match index from search results (1-based)");
            lines.Add("  --non-interactive   Skip interactive prompts");
            lines.Add("  --json              Output stream URLs as JSON");
            lines.Add("");
            lines.Add("Output includes:");
            lines.Add("  - Stream URLs with quality labels");
            lines.Add("  - Provider information");
            lines.Add("  - Subtitle tracks (if available)");
            lines.Add("");
            lines.Add("Notes:");
            lines.Add("  Does not launch a player. Useful for inspecting available streams");
            lines.Add("  or piping URLs to external tools.");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware stream \"bleach\" --episode 1");
            lines.Add("  koware stream \"one piece\" --episode 1010 --json");
            break;
            
        case "watch":
            lines.Add("watch - Pick a stream and launch the configured player");
            lines.Add("Alias: play");
            lines.Add("");
            lines.Add("Usage: koware watch <query> [options]");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --episode <n>       Episode number to play");
            lines.Add("  --quality <label>   Preferred quality (1080p, 720p, 480p, auto)");
            lines.Add("  --index <match>     Match index from search results (1-based)");
            lines.Add("  --non-interactive   Skip interactive prompts");
            lines.Add("");
            lines.Add("Player:");
            lines.Add("  Uses the configured player (default: Koware.Player.Win)");
            lines.Add("  Configure with: koware config set Player:Command \"vlc\"");
            lines.Add("");
            lines.Add("Behavior:");
            lines.Add("  - Searches for anime matching query");
            lines.Add("  - Resolves available streams");
            lines.Add("  - Picks best quality matching preference");
            lines.Add("  - Launches player with stream URL");
            lines.Add("  - Records entry in watch history");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware watch \"one piece\" --episode 1010 --quality 1080p");
            lines.Add("  koware play \"demon slayer\" --episode 1");
            lines.Add("  koware watch \"bleach\" --index 1 --episode 1 --non-interactive");
            break;
            
        case "download":
            lines.Add("download - Download episodes or chapters to files on disk");
            lines.Add("");
            lines.Add("Usage (anime mode):");
            lines.Add("  koware download <query> [options]");
            lines.Add("");
            lines.Add("Anime options:");
            lines.Add("  --episode <n>       Single episode number");
            lines.Add("  --episodes <range>  Episode range: 1-12, 5-10, or 'all'");
            lines.Add("  --quality <label>   Preferred quality (1080p, 720p, etc.)");
            lines.Add("  --index <match>     Match index from search results");
            lines.Add("  --dir <path>        Output directory (default: ./downloads)");
            lines.Add("  --non-interactive   Skip interactive prompts");
            lines.Add("");
            lines.Add("Usage (manga mode):");
            lines.Add("  koware download <query> [options]");
            lines.Add("");
            lines.Add("Manga options:");
            lines.Add("  --chapter <n|range> Chapter number or range: 1, 1-10, or 'all'");
            lines.Add("  --index <match>     Match index from search results");
            lines.Add("  --dir <path>        Output directory");
            lines.Add("  --non-interactive   Skip interactive prompts");
            lines.Add("");
            lines.Add("Requirements:");
            lines.Add("  Anime downloads require ffmpeg for HLS streams");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware download \"one piece\" --episodes 1-12 --quality 1080p");
            lines.Add("  koware download \"bleach\" --episode 1 --dir ~/Videos/Anime");
            lines.Add("  koware download \"chainsaw man\" --chapter 1-10 (manga mode)");
            lines.Add("");
            lines.Add("Downloads are tracked. View with 'koware offline'.");
            break;
            
        case "read":
            lines.Add("read - Search for manga and read chapters in the Koware reader");
            lines.Add("Note: Only available in manga mode");
            lines.Add("");
            lines.Add("Usage: koware read <query> [options]");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --chapter <n>       Chapter number (supports decimals: 10.5)");
            lines.Add("  --index <match>     Match index from search results (1-based)");
            lines.Add("  --start-page <n>    Start from specific page number");
            lines.Add("  --non-interactive   Skip interactive prompts");
            lines.Add("");
            lines.Add("Reader:");
            lines.Add("  Uses Koware.Reader.Win with features like:");
            lines.Add("  - Scroll/paged reading modes");
            lines.Add("  - Keyboard navigation (arrow keys, Page Up/Down)");
            lines.Add("  - Fit modes (width, height, original)");
            lines.Add("  - Zen mode for distraction-free reading (Z key)");
            lines.Add("  - Chapter navigation");
            lines.Add("");
            lines.Add("Behavior:");
            lines.Add("  1. Searches manga catalog");
            lines.Add("  2. Fetches chapter page URLs");
            lines.Add("  3. Launches reader with pages");
            lines.Add("  4. Records entry in read history");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware read \"one piece\" --chapter 1");
            lines.Add("  koware read \"chainsaw man\" --index 1 --chapter 10");
            lines.Add("  koware read \"jujutsu kaisen\" --chapter 236 --start-page 5");
            break;
            
        case "last":
            lines.Add("last - Show the most recent watched/read entry");
            lines.Add("");
            lines.Add("Usage: koware last [options]");
            lines.Add("Mode : shows last watched anime or last read manga");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --play   Resume playback of the last entry (anime mode)");
            lines.Add("  --json   Output entry details as JSON");
            lines.Add("");
            lines.Add("Output shows:");
            lines.Add("  - Title of the anime/manga");
            lines.Add("  - Episode/chapter number");
            lines.Add("  - Quality (for anime)");
            lines.Add("  - Timestamp of when it was watched/read");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware last");
            lines.Add("  koware last --play");
            lines.Add("  koware last --json");
            break;
            
        case "continue":
            lines.Add("continue - Resume from history and play/read the next episode/chapter");
            lines.Add("");
            lines.Add("Usage (anime):");
            lines.Add("  koware continue [<name>] [--from <episode>] [--quality <label>]");
            lines.Add("");
            lines.Add("Usage (manga):");
            lines.Add("  koware continue [<name>] [--from <chapter>]");
            lines.Add("");
            lines.Add("Mode : continues anime or manga based on current mode");
            lines.Add("");
            lines.Add("Behavior:");
            lines.Add("  - No name: resumes the most recent history entry");
            lines.Add("  - With name: fuzzy-matches history by title");
            lines.Add("  - Automatically advances to the NEXT episode/chapter");
            lines.Add("  - --from overrides the starting episode/chapter");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware continue                        Resume last watched");
            lines.Add("  koware continue \"one piece\"            Continue One Piece");
            lines.Add("  koware continue \"bleach\" --from 100    Start from ep 100");
            lines.Add("  koware continue --quality 720p         Use specific quality");
            break;
            
        case "history":
            lines.Add("history - Browse and filter watch/read history");
            lines.Add("");
            lines.Add("Usage: koware history [search <query>] [options]");
            lines.Add("Mode : shows watch history (anime) or read history (manga)");
            lines.Add("");
            lines.Add("Subcommands:");
            lines.Add("  search <query>      Filter history by title");
            lines.Add("");
            lines.Add("Filter options:");
            lines.Add("  --limit <n>         Limit number of results (default: 20)");
            lines.Add("  --after <ISO>       Show entries after date (2024-01-01)");
            lines.Add("  --before <ISO>      Show entries before date");
            lines.Add("  --from <ep>         From episode/chapter number");
            lines.Add("  --to <ep>           To episode/chapter number");
            lines.Add("");
            lines.Add("Output options:");
            lines.Add("  --json              Output as JSON");
            lines.Add("  --stats             Show aggregated counts per title");
            lines.Add("");
            lines.Add("Action options:");
            lines.Add("  --play <n>          Play the nth item from results (anime)");
            lines.Add("  --next              Play next episode of first match");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware history");
            lines.Add("  koware history search \"one piece\"");
            lines.Add("  koware history --limit 50 --json");
            lines.Add("  koware history --stats");
            lines.Add("  koware history --play 1");
            break;
            
        case "list":
            var statusValues = mode == CliMode.Manga ? "reading, completed, plan, hold, dropped" : "watching, completed, plan, hold, dropped";
            var itemType = mode == CliMode.Manga ? "manga" : "anime";
            var countType = mode == CliMode.Manga ? "chapters" : "episodes";
            var defaultStatus = mode == CliMode.Manga ? "plan-to-read" : "plan-to-watch";
            lines.Add($"list - Track your {itemType} watch/read status (like MAL/AniList)");
            lines.Add("");
            lines.Add("Subcommands:");
            lines.Add("  koware list                        Show all entries in your list");
            lines.Add("  koware list add \"<title>\"          Add title (searches if needed)");
            lines.Add("  koware list update \"<title>\"       Update an existing entry");
            lines.Add("  koware list remove \"<title>\"       Remove from list");
            lines.Add("  koware list stats                  Show counts by status");
            lines.Add("");
            lines.Add("Filter options:");
            lines.Add("  --status <status>  Filter by status");
            lines.Add("  --json             Output as JSON");
            lines.Add("");
            lines.Add($"Status values: {statusValues}");
            lines.Add("");
            lines.Add("Update options:");
            lines.Add("  --status <status>  Change status");
            lines.Add($"  --{countType} <n>     Set total {countType} count");
            lines.Add("  --score <1-10>     Rate the title (1-10)");
            lines.Add("  --notes \"...\"      Add personal notes");
            lines.Add("");
            lines.Add("Auto-tracking:");
            lines.Add($"  - Watching/reading auto-adds as '{defaultStatus}'");
            lines.Add("  - Status transitions automatically as you progress");
            lines.Add("  - Completing last episode/chapter marks as 'completed'");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware list");
            lines.Add("  koware list --status watching");
            lines.Add("  koware list add \"demon slayer\" --status watching");
            lines.Add("  koware list update \"one piece\" --score 10 --notes \"Masterpiece\"");
            lines.Add("  koware list stats --json");
            break;
            
        case "offline":
            lines.Add("offline - View downloaded content available for offline viewing");
            lines.Add("Alias: downloads");
            lines.Add("");
            lines.Add("Usage: koware offline [options]");
            lines.Add("Mode : shows episodes or chapters based on current mode");
            lines.Add("");
            lines.Add("Options:");
            lines.Add("  --stats    Show download statistics");
            lines.Add("             (total size, episode/chapter counts per title)");
            lines.Add("  --cleanup  Remove stale database entries for deleted files");
            lines.Add("  --json     Output as JSON for scripting");
            lines.Add("");
            lines.Add("Output shows:");
            lines.Add("  - List of anime/manga with downloaded content");
            lines.Add("  - Episode/chapter numbers formatted as ranges (1-5, 7, 10-12)");
            lines.Add("  - Total file size per title");
            lines.Add("  - Warnings for missing files");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware offline                 List downloaded anime");
            lines.Add("  koware mode manga && koware offline   List manga");
            lines.Add("  koware offline --stats         Show size statistics");
            lines.Add("  koware offline --cleanup       Clean up deleted files");
            lines.Add("  koware offline --json          Export for backup/scripting");
            break;
            
        case "config":
            lines.Add("config - View or update appsettings.user.json");
            lines.Add("");
            lines.Add("Subcommands:");
            lines.Add("  koware config                    Show config summary");
            lines.Add("  koware config show [--json]      Show all config values");
            lines.Add("  koware config path               Print config file path");
            lines.Add("  koware config get <path>         Read a specific value");
            lines.Add("  koware config set <path> <value> Write a value");
            lines.Add("  koware config unset <path>       Remove a value");
            lines.Add("");
            lines.Add("Legacy shortcuts (still supported):");
            lines.Add("  --quality <label>  Set default quality preference");
            lines.Add("  --index <n>        Set default match index");
            lines.Add("  --player <cmd>     Set player command");
            lines.Add("  --args <args>      Set player arguments");
            lines.Add("  --show             Show current config");
            lines.Add("");
            lines.Add("Config paths (use with get/set/unset):");
            lines.Add("  Player:Command     Player executable (vlc, mpv, etc.)");
            lines.Add("  Player:Arguments   Arguments to pass to player");
            lines.Add("  Reader:Command     Reader executable");
            lines.Add("  Defaults:Quality   Default quality preference");
            lines.Add("  Defaults:Index     Default search result index");
            lines.Add("  Defaults:Mode      Default mode (anime/manga)");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware config set Player:Command \"vlc\"");
            lines.Add("  koware config set Defaults:Quality \"1080p\"");
            lines.Add("  koware config get Player:Command");
            lines.Add("  koware config --quality 1080p --player mpv");
            break;
            
        case "mode":
            lines.Add("mode - Switch between anime and manga modes");
            lines.Add("");
            lines.Add("Usage: koware mode [anime|manga]");
            lines.Add("");
            lines.Add("Arguments:");
            lines.Add("  (none)    Show current mode");
            lines.Add("  anime     Switch to anime mode (default)");
            lines.Add("  manga     Switch to manga mode");
            lines.Add("");
            lines.Add("Mode is persisted in config file.");
            lines.Add("");
            lines.Add("Commands affected by mode:");
            lines.Add("  search    Searches anime or manga catalogs");
            lines.Add("  download  Downloads episodes or chapters");
            lines.Add("  history   Shows watch or read history");
            lines.Add("  last      Shows last watched or last read");
            lines.Add("  continue  Continues anime or manga");
            lines.Add("  list      Shows anime or manga tracking list");
            lines.Add("  offline   Shows downloaded anime or manga");
            lines.Add("  recommend Recommends anime or manga");
            lines.Add("");
            lines.Add("Mode-specific commands:");
            lines.Add("  watch/play   Only works in anime mode");
            lines.Add("  read         Only works in manga mode");
            lines.Add("");
            lines.Add("Examples:");
            lines.Add("  koware mode          Show current mode");
            lines.Add("  koware mode manga    Switch to manga");
            lines.Add("  koware mode anime    Switch back to anime");
            break;
            
        case "provider":
            lines.Add("provider - Manage content providers");
            lines.Add("");
            lines.Add("Usage: koware provider <subcommand> [options]");
            lines.Add("");
            lines.Add("Subcommands:");
            lines.Add("  list                  Show all providers and their status");
            lines.Add("  autoconfig <url>      Analyze website and generate provider config");
            lines.Add("  autoconfig [name]     Fetch config from koware-providers repo");
            lines.Add("  add [name]            Interactive wizard to configure a provider");
            lines.Add("  edit                  Open config file in default editor");
            lines.Add("  init                  Create a template configuration file");
            lines.Add("  test [name]           Test provider connectivity (DNS + HTTP)");
            lines.Add("  --enable <name>       Enable a provider");
            lines.Add("  --disable <name>      Disable a provider");
            lines.Add("");
            lines.Add("Autoconfig from URL (intelligent analysis):");
            lines.Add("  koware provider autoconfig https://example.com");
            lines.Add("  koware provider autoconfig mangadex.org --name \"MangaDex\"");
            lines.Add("");
            lines.Add("  Options:");
            lines.Add("    --name <name>       Custom provider name");
            lines.Add("    --type <type>       Force type: anime, manga, or both");
            lines.Add("    --skip-validation   Skip live testing phase");
            lines.Add("    --dry-run           Analyze without saving");
            lines.Add("");
            lines.Add("Autoconfig from remote manifest:");
            lines.Add("  koware provider autoconfig             Auto-configure all");
            lines.Add("  koware provider autoconfig allanime    Configure specific");
            lines.Add("  koware provider autoconfig --list      List available");
            lines.Add("");
            lines.Add("Other examples:");
            lines.Add("  koware provider list");
            lines.Add("  koware provider test");
            lines.Add("  koware provider --disable allanime");
            break;
            
        case "doctor":
            lines.Add("doctor - Run a full health check (config, providers, tools)");
            lines.Add("");
            lines.Add("Usage: koware doctor");
            lines.Add("");
            lines.Add("Checks performed:");
            lines.Add("");
            lines.Add("System:");
            lines.Add("  - CLI version and installation path");
            lines.Add("  - Config directory and file existence");
            lines.Add("");
            lines.Add("Providers:");
            lines.Add("  - AllAnime configuration status");
            lines.Add("  - AllManga configuration status");
            lines.Add("  - DNS resolution for provider domains");
            lines.Add("  - HTTP connectivity test");
            lines.Add("");
            lines.Add("Toolchain:");
            lines.Add("  - Player binary (Koware.Player.Win or configured)");
            lines.Add("  - Reader binary (Koware.Reader.Win or configured)");
            lines.Add("  - ffmpeg (required for HLS downloads)");
            lines.Add("  - yt-dlp (optional, for some sources)");
            lines.Add("  - aria2c (optional, for faster downloads)");
            lines.Add("");
            lines.Add("Output:");
            lines.Add("  Shows OK/FAIL status for each check with details.");
            lines.Add("  Tool versions are displayed when available.");
            lines.Add("");
            lines.Add("Run this first if you encounter issues.");
            break;
            
        case "update":
            lines.Add("update - Download and run the latest Koware installer");
            lines.Add("Note: Windows only");
            lines.Add("");
            lines.Add("Usage: koware update");
            lines.Add("");
            lines.Add("Process:");
            lines.Add("  1. Checks GitHub Releases for latest version");
            lines.Add("  2. Compares with current installed version");
            lines.Add("  3. Downloads installer to temp directory");
            lines.Add("  4. Launches the installer GUI");
            lines.Add("  5. Follow installer prompts to complete update");
            lines.Add("");
            lines.Add("Notes:");
            lines.Add("  - Requires internet connection");
            lines.Add("  - Downloads from github.com/S1mplector/Koware/releases");
            lines.Add("  - Installer preserves your config and history");
            lines.Add("  - macOS: Updates handled via DMG re-download");
            break;
            
        default:
            lines.Add($"Unknown command: {command}");
            break;
    }
    
    // Add navigation hint at the end
    lines.Add("");
    lines.Add("Press Esc to go back");
    
    return lines;
}

/// <summary>
/// Implement <c>koware help</c> and <c>koware help &lt;command&gt;</c>.
/// </summary>
/// <param name="args">CLI arguments; second element is the help topic.</param>
/// <param name="mode">Current CLI mode (anime or manga) for mode-sensitive help.</param>
/// <returns>Exit code: 0 on success, 1 if topic unknown.</returns>
/// <remarks>
/// Delegates to topic-specific help sections for each command.
/// </remarks>
static int HandleHelp(string[] args, CliMode mode)
{
    if (args.Length == 1)
    {
        // Show interactive command selector with back navigation
        var commands = new (string Name, string Description)[]
        {
            ("search", "Find anime or manga with optional filters"),
            ("recommend", "Get personalized recommendations based on your history"),
            ("stream", "Plan stream selection and print the resolved streams"),
            ("watch", "Pick a stream and launch the configured player"),
            ("download", "Download episodes or chapters to files on disk"),
            ("read", "Search for manga and read chapters in the Koware reader"),
            ("last", "Show the most recent watched/read entry"),
            ("continue", "Resume from history and play/read the next episode/chapter"),
            ("history", "Browse and filter watch/read history"),
            ("list", "Track your anime/manga watch/read status"),
            ("offline", "View downloaded content available for offline viewing"),
            ("config", "View or update appsettings.user.json"),
            ("mode", "Switch between anime and manga modes"),
            ("provider", "Manage providers: autoconfig from URL, list, test, enable/disable"),
            ("doctor", "Run a full health check (config, providers, tools)"),
            ("update", "Download and run the latest Koware installer")
        };

        while (true)
        {
            var selector = new InteractiveSelector<(string Name, string Description)>(
                commands,
                cmd => cmd.Name,
                new SelectorOptions<(string Name, string Description)>
                {
                    Prompt = "Help",
                    PreviewFunc = cmd => cmd.Description,
                    ShowPreview = true,
                    MaxVisibleItems = 12,
                    EmptyMessage = "No commands found"
                });

            var result = selector.Run();
            if (result.Cancelled)
                return 0;

            // Show detailed help for selected command in FZF menu
            var helpLines = GetHelpLines(result.Selected.Name, mode);
            var detailSelector = new InteractiveSelector<string>(
                helpLines,
                line => line,
                new SelectorOptions<string>
                {
                    Prompt = $"Help: {result.Selected.Name}",
                    ShowPreview = false,
                    ShowSearch = false,
                    MaxVisibleItems = Math.Min(helpLines.Count, 18),
                    DisableQuickJump = true
                });

            var detailResult = detailSelector.Run();
            // Whether cancelled (Esc) or selected (Enter), go back to main menu
            // Loop continues until user presses Esc on the main menu
        }
    }

    var topic = args[1].ToLowerInvariant();
    switch (topic)
    {
        case "search":
            PrintTopicHeader("search", "Find anime or manga with optional filters.");
            Console.WriteLine("Usage: koware search <query> [filters]");
            Console.WriteLine("Mode : searches anime or manga based on current mode (use 'koware mode' to switch).");
            Console.WriteLine();
            WriteColoredLine("Filters:", ConsoleColor.Yellow);
            WriteListOption("--genre <name>", "Filter by genre (Action, Romance, Fantasy, etc.)");
            WriteListOption("--year <year>", "Filter by release year (e.g., 2024)");
            WriteListOption("--status <status>", "Filter by status: ongoing, completed, upcoming");
            WriteListOption("--sort <order>", "Sort by: popular, score, recent, title");
            WriteListOption("--country <code>", "Country: JP (Japan), KR (Korea), CN (China)");
            Console.WriteLine();
            WriteColoredLine("Examples:", ConsoleColor.Yellow);
            Console.WriteLine("  koware search \"demon slayer\"");
            Console.WriteLine("  koware search --genre action --status ongoing");
            Console.WriteLine("  koware search \"romance\" --year 2024 --sort popular");
            Console.WriteLine("  koware search --sort popular  (browse mode with empty query)");
            break;
        case "recommend":
        case "rec":
            PrintTopicHeader("recommend", "Get personalized recommendations based on your history.");
            Console.WriteLine("Usage: koware recommend [--limit <n>] [--json]");
            Console.WriteLine("Mode : recommends anime or manga based on current mode.");
            Console.WriteLine();
            WriteColoredLine("Options:", ConsoleColor.Yellow);
            WriteListOption("--limit <n>", "Number of recommendations (default: 10, max: 50)");
            WriteListOption("--json", "Output as JSON");
            Console.WriteLine();
            Console.WriteLine("Behavior:");
            Console.WriteLine("  • Finds popular content you haven't watched/read yet");
            Console.WriteLine("  • Excludes titles already in your tracking list");
            Console.WriteLine();
            WriteColoredLine("Examples:", ConsoleColor.Yellow);
            Console.WriteLine("  koware recommend");
            Console.WriteLine("  koware recommend --limit 20");
            Console.WriteLine("  koware rec --json");
            break;
        case "stream":
        case "plan":
            PrintTopicHeader("stream", "Plan stream selection and print the resolved streams.");
            Console.WriteLine("Usage: koware stream <query> [--episode <n>] [--quality <label>] [--index <match>] [--non-interactive]");
            Console.WriteLine("Notes: does not launch a player; useful for inspecting streams.");
            break;
        case "watch":
        case "play":
            PrintTopicHeader("watch", "Pick a stream and launch the configured player.");
            Console.WriteLine("Usage: koware watch <query> [--episode <n>] [--quality <label>] [--index <match>] [--non-interactive]");
            Console.WriteLine("Alias: 'play' is the same as 'watch'.");
            Console.WriteLine("Example: koware watch \"one piece\" --episode 1010 --quality 1080p");
            break;
        case "download":
            PrintTopicHeader("download", "Download episodes or chapters to files on disk.");
            Console.WriteLine("Usage (anime): koware download <query> [--episode <n> | --episodes <n-m|all>] [--quality <label>] [--index <match>] [--dir <path>]");
            Console.WriteLine("Usage (manga): koware download <query> [--chapter <n|n-m|all>] [--index <match>] [--dir <path>]");
            Console.WriteLine("Mode : downloads episodes or chapters based on current mode.");
            Console.WriteLine("Examples:");
            Console.WriteLine("  koware download \"one piece\" --episodes 1-12 --quality 1080p");
            Console.WriteLine("  koware download \"chainsaw man\" --chapter 1-10  (manga mode)");
            Console.WriteLine();
            Console.WriteLine("Downloads are tracked. View with 'koware offline'.");
            break;
        case "offline":
        case "downloads":
            PrintTopicHeader("offline", "View downloaded content available for offline viewing.");
            Console.WriteLine("Usage: koware offline [--stats] [--cleanup] [--json]");
            Console.WriteLine("Mode : shows episodes or chapters based on current mode.");
            Console.WriteLine();
            WriteColoredLine("Options:", ConsoleColor.Yellow);
            WriteListOption("--stats", "Show download statistics (total episodes, chapters, size)");
            WriteListOption("--cleanup", "Remove stale entries for deleted files");
            WriteListOption("--json", "Output as JSON");
            Console.WriteLine();
            WriteColoredLine("What it shows:", ConsoleColor.Yellow);
            Console.WriteLine("  • List of anime/manga with downloaded content");
            Console.WriteLine("  • Episode/chapter numbers available offline");
            Console.WriteLine("  • File sizes and missing file warnings");
            Console.WriteLine();
            WriteColoredLine("Examples:", ConsoleColor.Yellow);
            Console.WriteLine("  koware offline              List downloaded anime");
            Console.WriteLine("  koware mode manga && koware offline   List downloaded manga");
            Console.WriteLine("  koware offline --stats      Show total download statistics");
            Console.WriteLine("  koware offline --cleanup    Remove entries for deleted files");
            break;
        case "read":
            PrintTopicHeader("read", "Search for manga and read chapters in the Koware reader.");
            Console.WriteLine("Usage: koware read <query> [--chapter <n>] [--index <match>] [--non-interactive]");
            Console.WriteLine("Examples:");
            Console.WriteLine("  koware read \"one piece\" --chapter 1");
            Console.WriteLine("  koware read \"chainsaw man\" --index 1 --chapter 10");
            Console.WriteLine("Behavior: searches allmanga.to, fetches chapter pages, and launches the reader.");
            break;
        case "last":
            PrintTopicHeader("last", "Show the most recent watched/read entry.");
            Console.WriteLine("Usage: koware last [--play] [--json]");
            Console.WriteLine("Mode : shows last watched anime or last read manga based on current mode.");
            Console.WriteLine("Flags: --play launches the last stream (anime mode); --json prints structured data.");
            break;
        case "continue":
            PrintTopicHeader("continue", "Resume from history and play/read the next episode/chapter.");
            Console.WriteLine("Usage (anime): koware continue [<anime name>] [--from <episode>] [--quality <label>]");
            Console.WriteLine("Usage (manga): koware continue [<manga name>] [--from <chapter>]");
            Console.WriteLine("Mode : continues anime or manga based on current mode.");
            Console.WriteLine("Behavior:");
            Console.WriteLine("  • No name: resumes the most recent entry and advances to the next episode/chapter.");
            Console.WriteLine("  • With name: fuzzy-matches history by title and resumes that show/manga.");
            Console.WriteLine("  • --from overrides the episode number; --quality overrides quality (else defaults/history).");
            break;
        case "history":
            PrintTopicHeader("history", "Browse and filter watch/read history.");
            Console.WriteLine("Usage: koware history [search <query>] [--anime/--manga <query>] [--limit <n>] [--after <ISO>] [--before <ISO>] [--from <ep>] [--to <ep>] [--json] [--stats]");
            Console.WriteLine("Mode : shows watch history (anime) or read history (manga) based on current mode.");
            Console.WriteLine("Notes:");
            Console.WriteLine("  • search <query> or --anime/--manga <query> filters titles.");
            Console.WriteLine("  • --play <n> plays the nth item (anime mode); --next plays next episode.");
            Console.WriteLine("  • --stats shows counts per anime/manga.");
            break;
        case "config":
            PrintTopicHeader("config", "View or update appsettings.user.json.");
            Console.WriteLine("Usage:");
            Console.WriteLine("  koware config                         Show summary");
            Console.WriteLine("  koware config show [--json]           Show config (raw JSON with --json)");
            Console.WriteLine("  koware config path                    Print config file path");
            Console.WriteLine("  koware config get <path> [--json]     Read a value (e.g., Player:Command)");
            Console.WriteLine("  koware config set <path> <value>      Write a value (creates sections)");
            Console.WriteLine("  koware config unset <path>            Remove a value");
            Console.WriteLine("Shortcuts:");
            Console.WriteLine("  koware config --quality 1080p --index 1");
            Console.WriteLine("  koware config --player vlc --args \"--play-and-exit\"");
            Console.WriteLine("Examples:");
            Console.WriteLine("  koware config set Player:Command \"vlc\"");
            Console.WriteLine("  koware config set Reader:Command \"./reader/Koware.Reader\"");
            Console.WriteLine("  koware config show --json");
            Console.WriteLine("  koware config path");
            break;
        case "provider":
            PrintTopicHeader("provider", "Manage content providers.");
            Console.WriteLine("Usage: koware provider <subcommand> [options]");
            Console.WriteLine();
            Console.WriteLine("Quick start:");
            Console.WriteLine("  koware provider autoconfig <url>   Analyze a website and generate config");
            Console.WriteLine("  koware provider autoconfig         Auto-configure from remote manifest");
            Console.WriteLine();
            Console.WriteLine("Run 'koware provider help' for full options.");
            break;
        case "doctor":
            PrintTopicHeader("doctor", "Run a full health check (config, providers, tools).");
            Console.WriteLine("Usage: koware doctor");
            Console.WriteLine("Behavior:");
            Console.WriteLine("  • Verifies CLI version/path and config file location.");
            Console.WriteLine("  • Checks anime/manga provider reachability (DNS + HTTP).");
            Console.WriteLine("  • Confirms player/reader binaries are discoverable.");
            Console.WriteLine("  • Checks external tools: ffmpeg, yt-dlp, aria2c (with versions when available).");
            break;
        case "mode":
            PrintTopicHeader("mode", "Switch between anime and manga modes.");
            Console.WriteLine("Usage: koware mode [anime|manga]");
            Console.WriteLine("Behavior:");
            Console.WriteLine("  • No argument: shows current mode.");
            Console.WriteLine("  • 'anime': switches to anime mode (default).");
            Console.WriteLine("  • 'manga': switches to manga mode.");
            Console.WriteLine();
            Console.WriteLine("Mode affects these commands:");
            Console.WriteLine("  • search  → searches anime or manga");
            Console.WriteLine("  • history → shows watch or read history");
            Console.WriteLine("  • last    → shows last watched or last read");
            Console.WriteLine("  • continue → continues anime or manga");
            break;
        case "update":
            PrintTopicHeader("update", "Download and run the latest Koware installer from GitHub Releases.");
            Console.WriteLine("Usage: koware update");
            Console.WriteLine("Behavior: downloads the latest Windows installer and launches it. Follow the GUI to complete the update.");
            break;
        case "list":
            if (mode == CliMode.Manga)
            {
                PrintTopicHeader("list", "Track your manga read status.");
                Console.WriteLine();
                WriteColoredLine("Commands:", ConsoleColor.Yellow);
                WriteListCommand("koware list", "Show all manga in your list");
                WriteListCommand("koware list --status <status>", "Filter by status");
                WriteListCommand("koware list add \"<title>\"", "Add to plan-to-read (default)");
                WriteListCommand("koware list update \"<title>\" --status <status>", "Change status");
                WriteListCommand("koware list remove \"<title>\"", "Remove from list");
                WriteListCommand("koware list stats", "Show counts by status");
                Console.WriteLine();
                WriteColoredLine("Options:", ConsoleColor.Yellow);
                WriteListOption("--status <status>", "reading, completed, plan, hold, dropped");
                WriteListOption("--chapters <n>", "Set total chapter count");
                WriteListOption("--score <1-10>", "Rate the manga");
                WriteListOption("--notes \"...\"", "Add personal notes");
                Console.WriteLine();
                WriteColoredLine("Examples:", ConsoleColor.Yellow);
                WriteListExample("koware list add \"One Piece\"");
                WriteListExample("koware list add \"Chainsaw Man\" --status reading --chapters 150");
                WriteListExample("koware list update \"One Piece\" --status reading --score 10");
                WriteListExample("koware list --status reading");
                Console.WriteLine();
                WriteColoredLine("Auto-tracking:", ConsoleColor.Yellow);
                Console.WriteLine("  When you read a chapter, it's automatically added as 'Reading'.");
                Console.WriteLine("  'Plan to Read' entries transition to 'Reading' when you start.");
                Console.WriteLine("  When you finish the last chapter, it auto-completes.");
            }
            else
            {
                PrintTopicHeader("list", "Track your anime watch status.");
                Console.WriteLine();
                WriteColoredLine("Commands:", ConsoleColor.Yellow);
                WriteListCommand("koware list", "Show all anime in your list");
                WriteListCommand("koware list --status <status>", "Filter by status");
                WriteListCommand("koware list add \"<title>\"", "Add to plan-to-watch (default)");
                WriteListCommand("koware list update \"<title>\" --status <status>", "Change status");
                WriteListCommand("koware list remove \"<title>\"", "Remove from list");
                WriteListCommand("koware list stats", "Show counts by status");
                Console.WriteLine();
                WriteColoredLine("Options:", ConsoleColor.Yellow);
                WriteListOption("--status <status>", "watching, completed, plan, hold, dropped");
                WriteListOption("--episodes <n>", "Set total episode count");
                WriteListOption("--score <1-10>", "Rate the anime");
                WriteListOption("--notes \"...\"", "Add personal notes");
                Console.WriteLine();
                WriteColoredLine("Examples:", ConsoleColor.Yellow);
                WriteListExample("koware list add \"Clannad\"");
                WriteListExample("koware list add \"Demon Slayer\" --status watching --episodes 26");
                WriteListExample("koware list update \"Clannad\" --status completed --score 10");
                WriteListExample("koware list --status watching");
                Console.WriteLine();
                WriteColoredLine("Auto-tracking:", ConsoleColor.Yellow);
                Console.WriteLine("  When you watch an episode, it's automatically added as 'Watching'.");
                Console.WriteLine("  'Plan to Watch' entries transition to 'Watching' when you start.");
                Console.WriteLine("  When you finish the last episode, it auto-completes.");
            }
            break;
        default:
            PrintUsage();
            Console.WriteLine();
            Console.WriteLine($"Unknown help topic '{topic}'. Try one of: search, recommend, offline, stream, watch, play, download, last, continue, history, list, config, provider, doctor, update.");
            return 1;
    }

    return 0;
}

/// <summary>
/// Helper for help topics: prints a cyan "Help: &lt;name&gt;" header with a description.
/// </summary>
/// <param name="name">Command name.</param>
/// <param name="description">Short description of the command.</param>
static void PrintTopicHeader(string name, string description)
{
    WriteHeader($"Help: {name}");
    Console.WriteLine(description);
}

/// <summary>
/// Write a cyan header line, preserving and restoring the previous console color.
/// </summary>
/// <param name="text">Header text to display.</param>
static void WriteHeader(string text)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(text);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Write a single line in the specified color, then restore the previous color.
/// </summary>
/// <param name="text">Text to display.</param>
/// <param name="color">Foreground color.</param>
static void WriteColoredLine(string text, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Helper for PrintUsage: render a command signature and description with consistent formatting.
/// </summary>
/// <param name="signature">Command syntax (e.g., "search &lt;query&gt;").</param>
/// <param name="description">Short description of the command.</param>
/// <param name="color">Foreground color for the signature.</param>
static void WriteCommand(string signature, string description, ConsoleColor color = ConsoleColor.Green)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write($"  {signature,-14}");
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write(" - ");
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine(description);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Helper for list help: write a command with syntax highlighting.
/// </summary>
static void WriteListCommand(string syntax, string description)
{
    var prev = Console.ForegroundColor;
    Console.Write("  ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"{syntax,-45}");
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine(description);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Helper for list help: write an option with its values.
/// </summary>
static void WriteListOption(string option, string values)
{
    var prev = Console.ForegroundColor;
    Console.Write("  ");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write($"{option,-20}");
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine(values);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Helper for list help: write an example command in magenta.
/// </summary>
static void WriteListExample(string example)
{
    var prev = Console.ForegroundColor;
    Console.Write("  ");
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine(example);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Helper for koware last: write a labeled field with colored value.
/// </summary>
static void WriteLastField(string label, string value, ConsoleColor valueColor)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"  {label,-10}");
    Console.ForegroundColor = valueColor;
    Console.WriteLine(value);
    Console.ForegroundColor = prev;
}

/// <summary>
/// Read the entry assembly version and return a short label like "v0.4.0".
/// </summary>
/// <returns>Version string or empty if unavailable.</returns>
static string GetVersionLabel()
{
    var version = Assembly.GetEntryAssembly()?.GetName().Version;
    if (version is null)
    {
        return string.Empty;
    }

    var parts = version.ToString().Split('.');
    var trimmed = parts.Length >= 3 ? string.Join('.', parts.Take(3)) : version.ToString();
    return $"v{trimmed}";
}

static Version? TryParseVersionCore(string? label)
{
    if (string.IsNullOrWhiteSpace(label))
    {
        return null;
    }

    var text = label.Trim();

    if (text.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
    {
        text = text.Substring(2);
    }
    else if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
    {
        text = text.Substring(1);
    }

    var separatorIndex = text.IndexOfAny(new[] { '-', '+', ' ' });
    if (separatorIndex >= 0)
    {
        text = text.Substring(0, separatorIndex);
    }

    return Version.TryParse(text, out var parsed) ? parsed : null;
}

/// <summary>
/// Implement the <c>koware version</c> command.
/// </summary>
/// <returns>Exit code: 0.</returns>
static int HandleVersion()
{
    var version = GetVersionLabel();
    Console.WriteLine(string.IsNullOrWhiteSpace(version) ? "Koware CLI (unknown version)" : $"Koware CLI {version}");
    return 0;
}

/// <summary>
/// Implement the <c>koware mode</c> command: show or switch between anime and manga modes.
/// </summary>
/// <param name="args">CLI arguments; optional "anime" or "manga" to switch.</param>
/// <param name="logger">Logger instance.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// With no arguments, shows current mode.
/// With "anime" or "manga", switches mode and saves to appsettings.user.json.
/// </remarks>
static Task<int> HandleModeAsync(string[] args, ILogger logger)
{
    var configPath = GetUserConfigFilePath();
    var root = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();
    var defaults = root["Defaults"] as JsonObject ?? new JsonObject();
    var currentMode = defaults["Mode"]?.ToString() ?? "anime";

    string newMode;
    
    if (args.Length == 1)
    {
        // Show interactive mode selector
        var modes = new[]
        {
            ("anime", "📺 Anime Mode", "Search, watch, and track anime series"),
            ("manga", "📖 Manga Mode", "Search, read, and track manga chapters")
        };
        
        var selector = new Koware.Cli.Console.InteractiveSelector<(string Id, string Name, string Description)>(
            modes,
            m => m.Name,
            new Koware.Cli.Console.SelectorOptions<(string Id, string Name, string Description)>
            {
                Prompt = $"Select Mode (current: {currentMode})",
                MaxVisibleItems = 5,
                ShowSearch = false,
                ShowPreview = true,
                PreviewFunc = m => m.Description
            });
        
        var result = selector.Run();
        
        if (result.Cancelled)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Mode selection cancelled.");
            Console.ResetColor();
            return Task.FromResult(0);
        }
        
        newMode = result.Selected!.Id;
    }
    else
    {
        newMode = args[1].ToLowerInvariant();
        if (newMode != "anime" && newMode != "manga")
        {
            logger.LogWarning("Invalid mode '{Mode}'. Use 'anime' or 'manga'.", args[1]);
            return Task.FromResult(1);
        }
    }

    // Check if already in this mode
    if (newMode == currentMode.ToLowerInvariant())
    {
        Console.WriteLine($"Already in {newMode} mode.");
        return Task.FromResult(0);
    }

    // Save the new mode
    defaults["Mode"] = newMode;
    root["Defaults"] = defaults;

    var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(configPath, json);

    Console.ForegroundColor = newMode == "manga" ? ConsoleColor.Magenta : ConsoleColor.Green;
    Console.WriteLine($"Switched to {newMode.ToUpperInvariant()} mode.");
    Console.ResetColor();

    return Task.FromResult(0);
}

/// <summary>
/// Implement the <c>koware config</c> command.
/// </summary>
/// <param name="args">CLI arguments.</param>
/// <returns>Exit code: 0 on success.</returns>
/// <remarks>
/// Reads/writes appsettings.user.json.
/// Supports verbs for ease of use:
/// - <c>show</c> (default): print summary; with --json prints raw config.
/// - <c>path</c>: print config file location.
/// - <c>get &lt;path&gt;</c>: read a value such as Player:Command.
/// - <c>set &lt;path&gt; &lt;value&gt;</c>: update a value, creating sections as needed.
/// - <c>unset &lt;path&gt;</c>: remove a value.
/// Legacy flag-style shortcuts (--quality/--index/--player/--args/--show) remain supported.
/// </remarks>
static int HandleConfig(string[] args)
{
    var configPath = GetUserConfigFilePath();
    var root = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();

    if (args.Length == 1)
    {
        PrintConfigSummary(root, configPath, rawJson: false);
        return 0;
    }

    var verb = args[1].ToLowerInvariant();
    if (verb is "show" or "--show" or "--json")
    {
        var extraArgs = args.Skip(2)
            .Where(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (extraArgs.Count == 0)
        {
            var raw = verb == "--json" || args.Skip(2).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            PrintConfigSummary(root, configPath, raw);
            return 0;
        }
    }

    switch (verb)
    {
        case "path":
            Console.WriteLine($"Config file: {configPath}");
            return 0;
        case "get":
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("Usage: koware config get <path> [--json]");
                    return 1;
                }

                var targetPath = args[2];
                var raw = args.Skip(3).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));

                if (!TryGetConfigValue(root, targetPath, out var value))
                {
                    Console.WriteLine($"Setting '{targetPath}' not found.");
                    return 1;
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                if (raw || value is not JsonValue)
                {
                    Console.WriteLine(value?.ToJsonString(options) ?? "null");
                }
                else if (value is JsonValue val && val.TryGetValue<string>(out var textValue))
                {
                    Console.WriteLine(textValue ?? "null");
                }
                else
                {
                    Console.WriteLine(value?.ToJsonString(options) ?? "null");
                }

                return 0;
            }
        case "set":
            {
                if (args.Length < 4)
                {
                    Console.WriteLine("Usage: koware config set <path> <value>");
                    return 1;
                }

                var targetPath = args[2];
                var value = string.Join(' ', args.Skip(3));

                if (!TrySetConfigValue(root, targetPath, value, out var error))
                {
                    Console.WriteLine(error ?? $"Could not set '{targetPath}'.");
                    return 1;
                }

                SaveUserConfig(root, configPath);
                Console.WriteLine($"{Icons.Success} Set {targetPath} = {value}");
                Console.WriteLine($"   Saved to {configPath}");
                return 0;
            }
        case "unset":
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("Usage: koware config unset <path>");
                    return 1;
                }

                var targetPath = args[2];
                if (!TryUnsetConfigValue(root, targetPath))
                {
                    Console.WriteLine($"Setting '{targetPath}' not found.");
                    return 1;
                }

                SaveUserConfig(root, configPath);
                Console.WriteLine($"{Icons.Success} Removed {targetPath}");
                Console.WriteLine($"   Saved to {configPath}");
                return 0;
            }
    }

    return HandleConfigLegacyOptions(args, root, configPath);
}

/// <summary>
/// Print a short usage line for the <c>koware config</c> command.
/// </summary>
static void PrintConfigUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  koware config                         Show config summary");
    Console.WriteLine("  koware config show [--json]           Show config (raw JSON with --json)");
    Console.WriteLine("  koware config path                    Print config file path");
    Console.WriteLine("  koware config get <path> [--json]     Read a value (e.g., Player:Command)");
    Console.WriteLine("  koware config set <path> <value>      Write a value (creates sections)");
    Console.WriteLine("  koware config unset <path>            Remove a value");
    Console.WriteLine("Shortcuts:");
    Console.WriteLine("  koware config --quality 1080p --index 1");
    Console.WriteLine("  koware config --player vlc --args \"--play-and-exit\"");
}

/// <summary>
/// Legacy flag-based configuration handler (kept for backwards compatibility).
/// </summary>
static int HandleConfigLegacyOptions(string[] args, JsonObject root, string configPath)
{
    var player = root["Player"] as JsonObject ?? new JsonObject();
    var reader = root["Reader"] as JsonObject ?? new JsonObject();
    var defaults = root["Defaults"] as JsonObject ?? new JsonObject();

    var showOnly = false;
    var changed = false;

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg.ToLowerInvariant())
        {
            case "--quality":
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("Missing value for --quality.");
                    return 1;
                }
                defaults["Quality"] = args[++i];
                changed = true;
                break;
            case "--index":
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var idx) || idx < 1)
                {
                    Console.WriteLine("Value for --index must be a positive integer.");
                    return 1;
                }
                defaults["PreferredMatchIndex"] = idx;
                i++;
                changed = true;
                break;
            case "--player":
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("Missing value for --player.");
                    return 1;
                }
                player["Command"] = args[++i];
                changed = true;
                break;
            case "--args":
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("Missing value for --args.");
                    return 1;
                }
                player["Args"] = args[++i];
                changed = true;
                break;
            case "--show":
                showOnly = true;
                break;
            default:
                Console.WriteLine($"Unknown option '{arg}'.");
                PrintConfigUsage();
                return 1;
        }
    }

    root["Player"] = player;
    root["Reader"] = reader;
    root["Defaults"] = defaults;

    if (changed)
    {
        SaveUserConfig(root, configPath);
        Console.WriteLine($"Saved preferences to {configPath}");
    }

    if (showOnly || !changed)
    {
        PrintConfigSummary(root, configPath, rawJson: false);
    }

    return 0;
}

/// <summary>
/// Pretty-print a summary of the user configuration or dump the raw JSON.
/// </summary>
static void PrintConfigSummary(JsonObject root, string configPath, bool rawJson)
{
    var options = new JsonSerializerOptions { WriteIndented = true };

    if (rawJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(root, options));
        return;
    }

    var defaults = root["Defaults"] as JsonObject ?? new JsonObject();
    var player = root["Player"] as JsonObject ?? new JsonObject();
    var reader = root["Reader"] as JsonObject ?? new JsonObject();

    Console.WriteLine($"Config file: {configPath}");
    Console.WriteLine();

    Console.WriteLine("Defaults");
    Console.WriteLine($"  Mode    : {defaults["Mode"]?.ToString() ?? "anime"}");
    Console.WriteLine($"  Quality : {defaults["Quality"]?.ToString() ?? "(not set)"}");
    Console.WriteLine($"  Index   : {defaults["PreferredMatchIndex"]?.ToString() ?? "(not set)"}");
    Console.WriteLine();

    Console.WriteLine("Player");
    var playerCommand = player["Command"]?.ToString();
    var playerArgs = player["Args"]?.ToString();
    Console.WriteLine($"  Command : {(string.IsNullOrWhiteSpace(playerCommand) ? "(default)" : playerCommand)}");
    Console.WriteLine($"  Args    : {(string.IsNullOrWhiteSpace(playerArgs) ? "(none)" : playerArgs)}");
    Console.WriteLine();

    Console.WriteLine("Reader");
    var readerCommand = reader["Command"]?.ToString();
    var readerArgs = reader["Args"]?.ToString();
    Console.WriteLine($"  Command : {(string.IsNullOrWhiteSpace(readerCommand) ? "(default)" : readerCommand)}");
    Console.WriteLine($"  Args    : {(string.IsNullOrWhiteSpace(readerArgs) ? "(none)" : readerArgs)}");
    Console.WriteLine();

    Console.WriteLine("Tips:");
    Console.WriteLine("  koware config set Player:Command \"vlc\"");
    Console.WriteLine("  koware config set Player:Args \"--play-and-exit\"");
    Console.WriteLine("  koware config set Reader:Command \"./reader/Koware.Reader\"");
    Console.WriteLine("  koware config set Defaults:Quality \"1080p\"");
}

/// <summary>
/// Try to read the file version from a PE/executable if available.
/// </summary>
static string? TryGetFileVersion(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        return null;
    }

    try
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.IsNullOrWhiteSpace(info.ProductVersion))
        {
            return info.ProductVersion;
        }

        return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// Try to execute a command with <c>--version</c> (or custom args) and return the first output line.
/// </summary>
static string? TryGetCommandVersion(string path, string args = "--version")
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    try
    {
        var start = new ProcessStartInfo
        {
            FileName = path,
            Arguments = string.IsNullOrWhiteSpace(args) ? string.Empty : args,
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

        var firstLine = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return firstLine;
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// Try to fetch a config value using a colon-separated path (case-insensitive).
/// </summary>
static bool TryGetConfigValue(JsonObject root, string path, out JsonNode? value)
{
    value = root;
    foreach (var segment in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
    {
        if (value is not JsonObject obj)
        {
            value = null;
            return false;
        }

        var key = FindExistingKey(obj, segment);
        if (key is null || !obj.TryGetPropertyValue(key, out value))
        {
            value = null;
            return false;
        }
    }

    return true;
}

/// <summary>
/// Set a config value by path, creating intermediate objects as needed.
/// </summary>
static bool TrySetConfigValue(JsonObject root, string path, string rawValue, out string? error)
{
    error = null;
    var segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
    {
        error = "Setting path cannot be empty.";
        return false;
    }

    JsonObject current = root;
    for (var i = 0; i < segments.Length - 1; i++)
    {
        var key = FindExistingKey(current, segments[i]) ?? segments[i];
        if (!current.TryGetPropertyValue(key, out var next) || next is not JsonObject nextObj)
        {
            nextObj = new JsonObject();
            current[key] = nextObj;
        }
        current = nextObj;
    }

    var finalKey = FindExistingKey(current, segments[^1]) ?? segments[^1];
    current[finalKey] = CreateJsonValue(rawValue);
    return true;
}

/// <summary>
/// Remove a config value by path.
/// </summary>
static bool TryUnsetConfigValue(JsonObject root, string path)
{
    var segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
    {
        return false;
    }

    JsonObject? current = root;
    for (var i = 0; i < segments.Length - 1; i++)
    {
        if (current is null)
        {
            return false;
        }

        var key = FindExistingKey(current, segments[i]);
        if (key is null || !current.TryGetPropertyValue(key, out var next) || next is not JsonObject nextObj)
        {
            return false;
        }

        current = nextObj;
    }

    if (current is null)
    {
        return false;
    }

    var finalKey = FindExistingKey(current, segments[^1]);
    return finalKey is not null && current.Remove(finalKey);
}

/// <summary>
/// Find an existing property name using case-insensitive comparison.
/// </summary>
static string? FindExistingKey(JsonObject obj, string name)
{
    foreach (var kvp in obj)
    {
        if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return kvp.Key;
        }
    }

    return null;
}

/// <summary>
/// Convert a raw string into a JsonNode with simple type inference.
/// </summary>
static JsonNode? CreateJsonValue(string raw)
{
    if (bool.TryParse(raw, out var boolValue))
    {
        return JsonValue.Create(boolValue);
    }

    if (int.TryParse(raw, out var intValue))
    {
        return JsonValue.Create(intValue);
    }

    if (double.TryParse(raw, out var doubleValue))
    {
        return JsonValue.Create(doubleValue);
    }

    if (raw.Equals("null", StringComparison.OrdinalIgnoreCase))
    {
        return JsonValue.Create<string?>(null);
    }

    return JsonValue.Create(raw);
}

/// <summary>
/// Persist the user configuration to disk, ensuring the directory exists.
/// </summary>
static void SaveUserConfig(JsonObject root, string configPath)
{
    var configDir = Path.GetDirectoryName(configPath);
    if (!string.IsNullOrWhiteSpace(configDir) && !Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir);
    }

    File.WriteAllText(configPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// Handle the theme command - list and preview available themes.
/// </summary>
static int HandleTheme(string[] args)
{
    var configPath = GetUserConfigFilePath();

    if (args.Length < 2)
    {
        // List available themes
        Console.ForegroundColor = Theme.Primary;
        Console.WriteLine("Available Themes:");
        Console.ResetColor();
        Console.WriteLine();

        foreach (var name in ThemePresets.GetNames())
        {
            var theme = ThemePresets.Get(name);
            var current = name.Equals(Theme.Current == ThemePresets.Get(name) ? name : "", StringComparison.OrdinalIgnoreCase);
            
            Console.Write("  ");
            if (current)
            {
                Console.ForegroundColor = Theme.Success;
                Console.Write("● ");
            }
            else
            {
                Console.Write("  ");
            }
            
            Console.ForegroundColor = theme.Primary;
            Console.Write($"{name,-12}");
            Console.ForegroundColor = theme.Secondary;
            Console.Write(" ■");
            Console.ForegroundColor = theme.Accent;
            Console.Write("■");
            Console.ForegroundColor = theme.Success;
            Console.Write("■");
            Console.ForegroundColor = theme.Warning;
            Console.Write("■");
            Console.ForegroundColor = theme.Error;
            Console.Write("■");
            Console.ResetColor();
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.ForegroundColor = Theme.Muted;
        Console.WriteLine("Usage: koware theme <name>  - Set and save theme");
        Console.WriteLine("       koware theme --preview <name>  - Preview theme");
        Console.ResetColor();
        return 0;
    }

    var arg = args[1].ToLowerInvariant();

    if (arg == "--preview" && args.Length > 2)
    {
        var previewName = args[2];
        if (!ThemePresets.GetNames().Any(n => n.Equals(previewName, StringComparison.OrdinalIgnoreCase)))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unknown theme: {previewName}");
            Console.ResetColor();
            return 1;
        }

        // Preview the theme
        Theme.SetPreset(previewName);
        Console.ForegroundColor = Theme.Primary;
        Console.WriteLine($"Preview: {previewName}");
        Console.ForegroundColor = Theme.Muted;
        Console.WriteLine(new string('─', 40));
        Console.ResetColor();
        
        Console.ForegroundColor = Theme.Text;
        Console.Write("  Text  ");
        Console.ForegroundColor = Theme.Primary;
        Console.Write("Primary  ");
        Console.ForegroundColor = Theme.Secondary;
        Console.Write("Secondary  ");
        Console.ForegroundColor = Theme.Accent;
        Console.WriteLine("Accent");
        
        Console.ForegroundColor = Theme.Success;
        Console.Write($"  {Icons.Success} Success  ");
        Console.ForegroundColor = Theme.Warning;
        Console.Write($"{Icons.Warning} Warning  ");
        Console.ForegroundColor = Theme.Error;
        Console.WriteLine($"{Icons.Error} Error");
        Console.ResetColor();
        
        Console.ForegroundColor = Theme.Muted;
        Console.WriteLine("\n(Preview only - run without --preview to save)");
        Console.ResetColor();
        return 0;
    }

    // Set theme
    var themeName = args[1];
    if (!ThemePresets.GetNames().Any(n => n.Equals(themeName, StringComparison.OrdinalIgnoreCase)))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown theme: {themeName}");
        Console.WriteLine("Available: " + string.Join(", ", ThemePresets.GetNames()));
        Console.ResetColor();
        return 1;
    }

    // Save to config
    var root = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();

    root["Theme"] = new JsonObject { ["Preset"] = themeName };
    var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(configPath, json);

    Theme.SetPreset(themeName);
    Console.ForegroundColor = Theme.Success;
    Console.WriteLine($"Theme set to '{themeName}'");
    Console.ResetColor();
    return 0;
}

/// <summary>
/// Handle the stats command - display detailed viewing/reading statistics.
/// </summary>
static async Task<int> HandleStatsAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var mode = defaults.GetMode();
    var isJson = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));

    Console.ForegroundColor = Theme.Primary;
    Console.WriteLine("📊 Statistics Dashboard");
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('═', 50));
    Console.ResetColor();
    Console.WriteLine();

    // Watch history stats
    var watchHistory = services.GetRequiredService<IWatchHistoryStore>();
    var watchStats = await watchHistory.GetStatsAsync(null, cancellationToken);
    var totalWatchEntries = watchStats.Sum(s => s.Count);
    var uniqueAnime = watchStats.Count;
    var estimatedHoursWatched = totalWatchEntries * 24 / 60.0; // ~24 min per episode

    Console.ForegroundColor = Theme.Secondary;
    Console.WriteLine("📺 Anime Watching");
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('─', 30));
    Console.ResetColor();
    Console.WriteLine($"  Episodes watched: {totalWatchEntries:N0}");
    Console.WriteLine($"  Unique anime:     {uniqueAnime:N0}");
    Console.WriteLine($"  Est. hours:       {estimatedHoursWatched:N1}h");
    Console.WriteLine();

    // Top 5 most watched
    if (watchStats.Count > 0)
    {
        Console.ForegroundColor = Theme.Accent;
        Console.WriteLine("  Most Watched:");
        Console.ResetColor();
        foreach (var stat in watchStats.Take(5))
        {
            Console.ForegroundColor = Theme.Success;
            Console.Write($"    {stat.Count,3} ep ");
            Console.ForegroundColor = Theme.Text;
            Console.WriteLine(stat.AnimeTitle.Length > 35 ? stat.AnimeTitle[..32] + "..." : stat.AnimeTitle);
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    // Read history stats
    var readHistory = services.GetRequiredService<IReadHistoryStore>();
    var readStats = await readHistory.GetStatsAsync(null, cancellationToken);
    var totalReadEntries = readStats.Sum(s => s.Count);
    var uniqueManga = readStats.Count;
    var estimatedChaptersRead = totalReadEntries;

    Console.ForegroundColor = Theme.Secondary;
    Console.WriteLine($"{Icons.Book} Manga Reading");
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('─', 30));
    Console.ResetColor();
    Console.WriteLine($"  Chapters read:    {totalReadEntries:N0}");
    Console.WriteLine($"  Unique manga:     {uniqueManga:N0}");
    Console.WriteLine();

    // Top 5 most read
    if (readStats.Count > 0)
    {
        Console.ForegroundColor = Theme.Accent;
        Console.WriteLine("  Most Read:");
        Console.ResetColor();
        foreach (var stat in readStats.Take(5))
        {
            Console.ForegroundColor = Theme.Success;
            Console.Write($"    {stat.Count,3} ch ");
            Console.ForegroundColor = Theme.Text;
            Console.WriteLine(stat.MangaTitle.Length > 35 ? stat.MangaTitle[..32] + "..." : stat.MangaTitle);
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    // Download stats
    var downloadStore = services.GetRequiredService<IDownloadStore>();
    var dlStats = await downloadStore.GetStatsAsync(cancellationToken);

    Console.ForegroundColor = Theme.Secondary;
    Console.WriteLine($"{Icons.Download} Downloads");
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('─', 30));
    Console.ResetColor();
    Console.WriteLine($"  Episodes:         {dlStats.TotalEpisodes:N0} ({dlStats.UniqueAnime} anime)");
    Console.WriteLine($"  Chapters:         {dlStats.TotalChapters:N0} ({dlStats.UniqueManga} manga)");
    Console.WriteLine($"  Total size:       {FormatFileSize(dlStats.TotalSizeBytes)}");
    Console.WriteLine();

    // Anime list stats
    var animeList = services.GetRequiredService<IAnimeListStore>();
    var animeListAll = await animeList.GetAllAsync(null, cancellationToken);
    var animeByStatus = animeListAll.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count());

    Console.ForegroundColor = Theme.Secondary;
    Console.WriteLine("📋 Anime List");
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('─', 30));
    Console.ResetColor();
    Console.WriteLine($"  Total:            {animeListAll.Count}");
    if (animeByStatus.TryGetValue(AnimeWatchStatus.Watching, out var watching))
        Console.WriteLine($"  Currently watching: {watching}");
    if (animeByStatus.TryGetValue(AnimeWatchStatus.Completed, out var completed))
        Console.WriteLine($"  Completed:        {completed}");
    if (animeByStatus.TryGetValue(AnimeWatchStatus.PlanToWatch, out var ptw))
        Console.WriteLine($"  Plan to watch:    {ptw}");
    if (animeByStatus.TryGetValue(AnimeWatchStatus.OnHold, out var onHold))
        Console.WriteLine($"  On hold:          {onHold}");
    if (animeByStatus.TryGetValue(AnimeWatchStatus.Dropped, out var dropped))
        Console.WriteLine($"  Dropped:          {dropped}");
    Console.WriteLine();

    // Manga list stats
    var mangaList = services.GetRequiredService<IMangaListStore>();
    var mangaListAll = await mangaList.GetAllAsync(null, cancellationToken);
    var mangaByStatus = mangaListAll.GroupBy(m => m.Status).ToDictionary(g => g.Key, g => g.Count());

    Console.ForegroundColor = Theme.Secondary;
    Console.WriteLine("📚 Manga List");
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('─', 30));
    Console.ResetColor();
    Console.WriteLine($"  Total:            {mangaListAll.Count}");
    if (mangaByStatus.TryGetValue(MangaReadStatus.Reading, out var reading))
        Console.WriteLine($"  Currently reading: {reading}");
    if (mangaByStatus.TryGetValue(MangaReadStatus.Completed, out var mCompleted))
        Console.WriteLine($"  Completed:        {mCompleted}");
    if (mangaByStatus.TryGetValue(MangaReadStatus.PlanToRead, out var ptr))
        Console.WriteLine($"  Plan to read:     {ptr}");
    Console.WriteLine();

    // Summary
    Console.ForegroundColor = Theme.Muted;
    Console.WriteLine(new string('═', 50));
    Console.ForegroundColor = Theme.Primary;
    Console.WriteLine($"Total content: {uniqueAnime + uniqueManga} titles | {totalWatchEntries + totalReadEntries} entries");
    Console.ResetColor();

    return 0;
}

// ===== Record Types =====

/// <summary>
/// Navigation result from reader including current page.
/// </summary>
record NavigationResult(string Action, int Page, float Chapter);

/// <summary>
/// Result from reading with navigation - includes exit code and last position.
/// </summary>
record ReadResult(int ExitCode, float LastChapter, int LastPage);
