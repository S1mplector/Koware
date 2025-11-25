// Author: Ilgaz Mehmetoğlu
// Summary: Entry point and command routing for the Koware CLI, including playback orchestration and configuration handling.
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
using Koware.Infrastructure.DependencyInjection;
using Koware.Infrastructure.Configuration;
using Koware.Cli.Configuration;
using Koware.Cli.History;
using Koware.Cli.Console;
using Koware.Cli.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using var host = BuildHost(args);
var exitCode = await RunAsync(host, args);
return exitCode;

static IHost BuildHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: true);
    builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.user.json"), optional: true, reloadOnChange: true);
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection("Player"));
    builder.Services.Configure<DefaultCliOptions>(builder.Configuration.GetSection("Defaults"));
    builder.Services.AddSingleton<IWatchHistoryStore, SqliteWatchHistoryStore>();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("koware", LogLevel.Information);
    builder.Logging.AddFilter("Koware", LogLevel.Information);
    builder.Logging.AddFilter("Koware.Infrastructure.Scraping.GogoAnimeCatalog", LogLevel.Error);

    return builder.Build();
}

static async Task<int> RunAsync(IHost host, string[] args)
{
    using var scope = host.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("koware.cli");
    var defaults = services.GetService<IOptions<DefaultCliOptions>>()?.Value ?? new DefaultCliOptions();
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        logger.LogInformation("Cancellation requested. Stopping...");
        e.Cancel = true;
        cts.Cancel();
    };

    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();
    var command = args[0].ToLowerInvariant();

    try
    {
        switch (command)
        {
            case "search":
                return await HandleSearchAsync(orchestrator, args, logger, cts.Token);
            case "plan":
            case "stream":
                return await HandlePlanAsync(orchestrator, args, logger, defaults, cts.Token);
            case "watch":
            case "play":
                return await HandlePlayAsync(orchestrator, args, services, logger, defaults, cts.Token);
            case "last":
                return await HandleLastAsync(args, services, logger, cts.Token);
            case "continue":
                return await HandleContinueAsync(args, services, logger, defaults, cts.Token);
            case "history":
                return await HandleHistoryAsync(args, services, logger, defaults, cts.Token);
            case "config":
                return HandleConfig(args);
            case "doctor":
                return await HandleDoctorAsync(services, logger, cts.Token);
            case "provider":
                return await HandleProviderAsync(args, services);
            case "help":
            case "--help":
            case "-h":
                return HandleHelp(args);
            default:
                logger.LogWarning("Unknown command: {Command}", command);
                PrintUsage();
                return 1;
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Operation canceled by user.");
        return 2;
    }
    catch (HttpRequestException ex)
    {
        logger.LogError("Network error while reaching the anime provider: {Message}", ex.Message);
        LogNetworkHint(ex);
        return 1;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception during command execution.");
        return 1;
    }
}

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
    var filteredStreams = FilterStreamsForPlayer(result.Streams, playerResolution.Name, logger);

    var stream = PickBestStream(filteredStreams);
    if (stream is null)
    {
        logger.LogWarning("No playable streams found.");
        return 1;
    }
    logger.LogDebug("Selected stream {Quality} from host {Host}", stream.Quality ?? "unknown", stream.Url.Host);

    var allAnimeOptions = services.GetService<IOptions<AllAnimeOptions>>()?.Value;
    var displayTitle = BuildPlayerTitle(result, stream);
    var httpReferrer = !string.IsNullOrWhiteSpace(stream.Referrer)
        ? stream.Referrer
        : allAnimeOptions?.Referer;
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
    }

    return exitCode;
}

static async Task<int> HandleLastAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    var history = services.GetRequiredService<IWatchHistoryStore>();
    var entry = await history.GetLastAsync(cancellationToken);
    if (entry is null)
    {
        logger.LogWarning("No watch history found.");
        return 1;
    }

    var play = args.Any(a => string.Equals(a, "--play", StringComparison.OrdinalIgnoreCase));
    var json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

    if (!play)
    {
        if (json)
        {
            var jsonText = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            Console.WriteLine(jsonText);
        }
        else
        {
            Console.WriteLine("Last watched:");
            Console.WriteLine($"  Anime   : {entry.AnimeTitle} ({entry.AnimeId})");
            Console.WriteLine($"  Episode : {entry.EpisodeNumber}{(string.IsNullOrWhiteSpace(entry.EpisodeTitle) ? string.Empty : $" - {entry.EpisodeTitle}")}");
            Console.WriteLine($"  Provider: {entry.Provider}");
            if (!string.IsNullOrWhiteSpace(entry.Quality))
            {
                Console.WriteLine($"  Quality : {entry.Quality}");
            }
            Console.WriteLine($"  Watched : {entry.WatchedAt:u}");
        }

            return 0;
    }

    return 0;
}

static void LogNetworkHint(HttpRequestException ex)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine("Network issue: could not reach the anime provider.");
    Console.ResetColor();
    var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    Console.WriteLine($"Details: {msg}");
    Console.WriteLine("Tips:");
    Console.WriteLine("  - Check your internet connection and VPN/proxy settings.");
    Console.WriteLine("  - Ensure api.allanime.day resolves (DNS) and isn't blocked by a firewall.");
    Console.WriteLine("  - Retry in a minute; the host may be temporarily down.");
}

static async Task<int> HandleDoctorAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    var allAnime = services.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
    var gogo = services.GetRequiredService<IOptions<GogoAnimeOptions>>().Value;
    var results = new List<(string name, ProviderCheckResult result)>();
    foreach (var (name, target) in new[] { ("allanime", allAnime.ApiBase), ("gogoanime", $"{gogo.ApiBase}/anime/gogoanime") })
    {
        var step = ConsoleStep.Start($"Checking {name}");
        try
        {
            var diagnostics = new ProviderDiagnostics(new HttpClient());
            var options = name == "allanime"
                ? new AllAnimeOptions { ApiBase = allAnime.ApiBase, Referer = allAnime.Referer, UserAgent = allAnime.UserAgent }
                : new AllAnimeOptions { ApiBase = gogo.ApiBase, Referer = gogo.SiteBase, UserAgent = gogo.UserAgent };

            var result = await diagnostics.CheckAsync(options, cancellationToken);
            results.Add((name, result));
            step.Succeed("Check complete");
        }
        catch (Exception ex)
        {
            step.Fail("Check failed");
            logger.LogError(ex, "{Provider} doctor check failed.", name);
            results.Add((name, new ProviderCheckResult { Target = target, HttpError = ex.Message }));
        }
    }

    Console.WriteLine();
    foreach (var (name, result) in results)
    {
        Console.WriteLine($"{name} ({result.Target})");
        WriteStatus("DNS", result.DnsResolved, result.DnsError);
        var detail = result.HttpError ?? (result.HttpStatus.HasValue ? $"HTTP {result.HttpStatus}" : "No response");
        WriteStatus("HTTP", result.HttpSuccess, detail);
        Console.WriteLine();
    }

    var allOk = results.All(r => r.result.Success);
    if (!allOk)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("One or more providers are unreachable. Check your connection, DNS, or try again later.");
        Console.ResetColor();
        return 1;
    }

    Console.WriteLine("Player / toolchain:");
    var playerOptions = services.GetRequiredService<IOptions<PlayerOptions>>().Value;
    var playerResolution = ResolvePlayerExecutable(playerOptions);
    WriteStatus("Player", playerResolution.Path is not null, playerResolution.Path ?? $"Not found (tried: {string.Join(", ", playerResolution.Candidates)})");

    foreach (var tool in new[] { "ffmpeg", "yt-dlp", "aria2c" })
    {
        var resolved = ResolveExecutablePath(tool);
        WriteStatus(tool, !string.IsNullOrWhiteSpace(resolved), resolved ?? "Not found");
    }
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("All providers reachable.");
    Console.ResetColor();
    return 0;
}

static void WriteStatus(string label, bool success, string? detail = null)
{
    Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
    var status = success ? "OK" : "FAIL";
    Console.Write($"{label,-5}: {status}");
    Console.ResetColor();
    if (!string.IsNullOrWhiteSpace(detail))
    {
        Console.Write($" - {detail}");
    }
    Console.WriteLine();
}

static async Task<int> HandleProviderAsync(string[] args, IServiceProvider services)
{
    var toggles = services.GetRequiredService<IOptions<ProviderToggleOptions>>().Value;
    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.user.json");
    var json = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();
    var providersNode = json["Providers"] as JsonObject ?? new JsonObject();
    var disabledNode = providersNode["DisabledProviders"] as JsonArray ?? new JsonArray();

    if (args.Length == 1)
    {
        var known = new[]
        {
            ("allanime", "Primary (AllAnime)"),
            ("gogoanime", "Fallback (GogoAnime)")
        };
        foreach (var (name, label) in known)
        {
            var enabled = toggles.IsEnabled(name);
            Console.ForegroundColor = enabled ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"{name,-10}");
            Console.ResetColor();
            Console.WriteLine($" - {label}");
        }
        return 0;
    }

    if (args.Length >= 3 && (args[1].Equals("--enable", StringComparison.OrdinalIgnoreCase) || args[1].Equals("--disable", StringComparison.OrdinalIgnoreCase)))
    {
        var target = args[2].ToLowerInvariant();
        var enable = args[1].Equals("--enable", StringComparison.OrdinalIgnoreCase);
        var set = new HashSet<string>(disabledNode.OfType<JsonNode?>().Select(n => n?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
        if (enable)
        {
            set.Remove(target);
        }
        else
        {
            set.Add(target);
        }

        var newArray = new JsonArray(set.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        providersNode["DisabledProviders"] = newArray;
        json["Providers"] = providersNode;
        File.WriteAllText(configPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));

        Console.ForegroundColor = enable ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"{(enable ? "Enabled" : "Disabled")} provider '{target}'.");
        Console.ResetColor();
        Console.WriteLine("Restart your session for changes to take effect.");
        return 0;
    }

    Console.WriteLine("Usage: koware provider [--enable <name> | --disable <name>]");
    return 1;
}

static async Task<int> HandleContinueAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
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
    WatchHistoryEntry? entry;

    if (string.IsNullOrWhiteSpace(animeQuery))
    {
        entry = await history.GetLastAsync(cancellationToken);
    }
    else
    {
        entry = await history.SearchLastAsync(animeQuery, cancellationToken)
                ?? await history.GetLastForAnimeAsync(animeQuery, cancellationToken);
    }

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

static async Task<int> HandleHistoryAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
    var history = services.GetRequiredService<IWatchHistoryStore>();
    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();

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
        }
        idx++;
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
        Console.WriteLine($"{index,3} {Truncate(e.AnimeTitle,30),-30} {e.EpisodeNumber,4} {e.Quality ?? "?",-8} {e.WatchedAt:u,-20} {e.EpisodeTitle ?? string.Empty}");
        index++;
    }
}

static string Truncate(string value, int max)
{
    if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
    return value[..(max - 1)] + "…";
}

static int UsageErrorWithReturn(string message)
{
    Console.WriteLine(message);
    return 1;
}

static async Task<int> LaunchFromHistory(WatchHistoryEntry entry, ScrapeOrchestrator orchestrator, IServiceProvider services, IWatchHistoryStore history, ILogger logger, DefaultCliOptions defaults, int episodeNumber, CancellationToken cancellationToken)
{
    var quality = entry.Quality ?? defaults.Quality;
    var plan = new ScrapePlan(entry.AnimeTitle, episodeNumber, quality);
    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
}

static async Task<int> HandleSearchAsync(ScrapeOrchestrator orchestrator, string[] args, ILogger logger, CancellationToken cancellationToken)
{
    var jsonOutput = args.Skip(1).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var filteredArgs = args.Where((a, idx) => idx == 0 || !a.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();

    var query = string.Join(' ', filteredArgs.Skip(1)).Trim();
    if (string.IsNullOrWhiteSpace(query))
    {
        logger.LogWarning("search command is missing a query.");
        PrintFriendlyCommandHint("search");
        return 1;
    }

    var matches = await orchestrator.SearchAsync(query, cancellationToken);
    if (jsonOutput)
    {
        var payload = new
        {
            query,
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
        RenderSearch(query, matches);
    }

    return 0;
}

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

static async Task<int> HandlePlayAsync(ScrapeOrchestrator orchestrator, string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
{
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

    var history = services.GetRequiredService<IWatchHistoryStore>();
    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
}

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

    RenderSearch(plan.Query, matches);
    Console.Write($"Select anime [1-{matches.Count}] (Enter for 1): ");
    var input = Console.ReadLine();
    if (int.TryParse(input, out var choice) && choice >= 1 && choice <= matches.Count)
    {
        return plan with { PreferredMatchIndex = choice };
    }

    if (!string.IsNullOrWhiteSpace(input))
    {
        logger.LogWarning("Invalid selection '{Input}'. Defaulting to the first match.", input);
    }

    return plan with { PreferredMatchIndex = 1 };
}

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

static IReadOnlyCollection<StreamLink> FilterStreamsForPlayer(IReadOnlyCollection<StreamLink> streams, string playerName, ILogger logger)
{
    var supportsSoftSubs = SupportsSoftSubtitles(playerName);
    var filtered = supportsSoftSubs
        ? streams
        : streams.Where(s => !s.RequiresSoftSubSupport).ToArray();

    if (!supportsSoftSubs && filtered.Count == 0)
    {
        logger.LogWarning("Player {Player} may not support external subtitles; using soft-sub streams anyway.", playerName);
        filtered = streams;
    }

    return filtered
        .OrderByDescending(s => s.HostPriority)
        .ThenByDescending(s => ParseQualityScore(s.Quality))
        .ToArray();
}

static bool SupportsSoftSubtitles(string playerName) =>
    !playerName.Contains("vlc", StringComparison.OrdinalIgnoreCase);

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

static bool IsBadHost(StreamLink stream)
{
    var host = stream.Url.Host;
    return host.Contains("haildrop", StringComparison.OrdinalIgnoreCase)
           || host.Contains("sharepoint", StringComparison.OrdinalIgnoreCase);
}

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

static int ParseQualityScore(string? quality) =>
    TryParseQualityNumber(quality, out var q) ? q : 0;

static bool IsPlaylist(StreamLink stream)
{
    var path = stream.Url.AbsolutePath;
    return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
           || path.Contains("manifest", StringComparison.OrdinalIgnoreCase);
}

static bool IsSegment(StreamLink stream)
{
    var path = stream.Url.AbsolutePath;
    return path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
}

static bool IsJsonStream(StreamLink stream)
{
    var path = stream.Url.AbsolutePath;
    return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

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

static PlayerResolution ResolvePlayerExecutable(PlayerOptions options)
{
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(options.Command))
    {
        candidates.Add(options.Command);
    }
    candidates.AddRange(new[] { "Koware.Player.Win", "Koware.Player.Win.exe", "vlc", "mpv" });
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

static int LaunchPlayer(PlayerOptions options, StreamLink stream, ILogger logger, string? httpReferrer, string? httpUserAgent, string? displayTitle, PlayerResolution? resolution = null)
{
    resolution ??= ResolvePlayerExecutable(options);

    if (resolution.Path is null)
    {
        logger.LogError("No supported player found (tried {Candidates}). Build Koware.Player.Win or set Player:Command in appsettings.json.", string.Join(", ", resolution.Candidates));
        return 1;
    }

    var playerPath = resolution.Path;
    var playerName = resolution.Name;
    var subtitle = stream.Subtitles.FirstOrDefault();

    if (string.Equals(playerName, "Koware.Player.Win", StringComparison.OrdinalIgnoreCase))
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
    if (string.Equals(playerName, "vlc", StringComparison.OrdinalIgnoreCase))
    {
        defaultArgs = "--play-and-exit --quiet";
    }
    else if (string.Equals(playerName, "mpv", StringComparison.OrdinalIgnoreCase))
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
        else if (string.Equals(playerName, "mpv", StringComparison.OrdinalIgnoreCase))
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
        else if (string.Equals(playerName, "mpv", StringComparison.OrdinalIgnoreCase))
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

static void RenderSearch(string query, IReadOnlyCollection<Anime> matches)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Matches for \"{query}\":");
    Console.ResetColor();
    if (matches.Count == 0)
    {
        Console.WriteLine("  No results yet. Try a different query.");
        return;
    }

    var index = 1;
    foreach (var anime in matches)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  [{index}] ");
        Console.ResetColor();
        Console.WriteLine($"{anime.Title} -> {anime.DetailPage}");
        index++;
    }
}

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
            Console.WriteLine($"  {stream.Quality} -> {stream.Url}");
        }

        if (ordered.Length > toShow.Length)
        {
            Console.WriteLine($"  ...and {ordered.Length - toShow.Length} more");
        }
    }
}

static void PrintUsage()
{
    WriteHeader($"Koware CLI {GetVersionLabel()}");
    Console.WriteLine("Usage:");
    WriteCommand("search <query>", "Find anime and list matches.", ConsoleColor.Cyan);
    WriteCommand("stream <query> [--episode <n>] [--quality <label>] [--index <n>] [--non-interactive]", "Show plan + streams, no player.", ConsoleColor.Cyan);
    WriteCommand("watch <query> [--episode <n>] [--quality <label>] [--index <n>] [--non-interactive]", "Pick a stream and play (alias: play).", ConsoleColor.Green);
    WriteCommand("play <query> [--episode <n>] [--quality <label>] [--index <n>] [--non-interactive]", "Same as watch.", ConsoleColor.Green);
    WriteCommand("last [--play] [--json]", "Show or replay your most recent watch.", ConsoleColor.Yellow);
    WriteCommand("continue [<anime>] [--from <episode>] [--quality <label>]", "Resume from history (auto next episode).", ConsoleColor.Yellow);
    WriteCommand("history [options]", "Browse/search history; play entries or show stats.", ConsoleColor.Yellow);
    WriteCommand("help [command]", "Show this guide or a command-specific help page.", ConsoleColor.Magenta);
    WriteCommand("config [options]", "Persist defaults (quality/index/player) to appsettings.user.json.", ConsoleColor.Magenta);
    WriteCommand("doctor", "Check provider connectivity (DNS/HTTP).", ConsoleColor.Magenta);
    WriteCommand("provider [options]", "List/enable/disable providers.", ConsoleColor.Magenta);
}

static int HandleHelp(string[] args)
{
    if (args.Length == 1)
    {
        PrintUsage();
        Console.WriteLine();
        Console.WriteLine("For detailed help: koware help <command>");
        Console.WriteLine("Commands: search, stream, watch, play, last, continue, config, doctor");
        return 0;
    }

    var topic = args[1].ToLowerInvariant();
    switch (topic)
    {
        case "search":
            PrintTopicHeader("search", "Find anime and show a numbered list of matches.");
            Console.WriteLine("Usage: koware search <query>");
            Console.WriteLine("Tips : use quotes for multi-word queries (e.g., \"demon slayer\").");
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
        case "last":
            PrintTopicHeader("last", "Show or replay the most recent watched entry.");
            Console.WriteLine("Usage: koware last [--play] [--json]");
            Console.WriteLine("Flags: --play launches the last stream; --json prints structured data.");
            break;
        case "continue":
            PrintTopicHeader("continue", "Resume from history (fuzzy match by title) and play the next episode.");
            Console.WriteLine("Usage: koware continue [<anime name>] [--from <episode>] [--quality <label>]");
            Console.WriteLine("Behavior:");
            Console.WriteLine("  • No name: resumes the most recent entry and advances to the next episode.");
            Console.WriteLine("  • With name: fuzzy-matches history by title (case-insensitive contains) and resumes that show.");
            Console.WriteLine("  • --from overrides the episode number; --quality overrides quality (else defaults/history).");
            break;
        case "history":
            PrintTopicHeader("history", "Browse and filter watch history (and replay entries).");
            Console.WriteLine("Usage: koware history [search <query>] [--anime <query>] [--limit <n>] [--after <ISO>] [--before <ISO>] [--from <ep>] [--to <ep>] [--json] [--stats] [--play <n>] [--next]");
            Console.WriteLine("Notes:");
            Console.WriteLine("  • search <query> or --anime <query> filters titles (case-insensitive contains).");
            Console.WriteLine("  • --play <n> plays the nth item in the shown list; --next plays next episode of the first match.");
            Console.WriteLine("  • --stats shows counts per anime instead of entries.");
            break;
        case "config":
            PrintTopicHeader("config", "Persist preferred defaults to appsettings.user.json.");
            Console.WriteLine("Usage: koware config [--quality <label>] [--index <n>] [--player <exe>] [--args <string>] [--show]");
            Console.WriteLine("Examples:");
            Console.WriteLine("  koware config --quality 1080p --index 1");
            Console.WriteLine("  koware config --player vlc --args \"--play-and-exit\"");
            Console.WriteLine("  koware config --show");
            break;
        case "provider":
            PrintTopicHeader("provider", "List or toggle providers.");
            Console.WriteLine("Usage: koware provider [--enable <name> | --disable <name>]");
            Console.WriteLine("Behavior: lists providers; with flags, updates enablement.");
            break;
        case "doctor":
            PrintTopicHeader("doctor", "Check connectivity to the anime provider.");
            Console.WriteLine("Usage: koware doctor");
            Console.WriteLine("Behavior: pings api host, reports DNS + HTTP reachability.");
            break;
        default:
            PrintUsage();
            Console.WriteLine();
            Console.WriteLine($"Unknown help topic '{topic}'. Try one of: search, stream, watch, play, last, continue, config, provider, doctor.");
            return 1;
    }

    return 0;
}

static void PrintTopicHeader(string name, string description)
{
    WriteHeader($"Help: {name}");
    Console.WriteLine(description);
}

static void WriteHeader(string text)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(text);
    Console.ForegroundColor = prev;
}

static void WriteColoredLine(string text, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = prev;
}

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

static int HandleConfig(string[] args)
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.user.json");
    var root = File.Exists(configPath)
        ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
        : new JsonObject();

    var player = root["Player"] as JsonObject ?? new JsonObject();
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
    root["Defaults"] = defaults;

    if (changed)
    {
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
        Console.WriteLine($"Saved preferences to {configPath}");
    }

    if (showOnly || !changed)
    {
        var summary = new
        {
            Player = new
            {
                Command = player["Command"]?.ToString() ?? "(default)",
                Args = player["Args"]?.ToString()
            },
            Defaults = new
            {
                Quality = defaults["Quality"]?.ToString(),
                PreferredMatchIndex = defaults["PreferredMatchIndex"]?.ToString()
            }
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    return 0;
}

static void PrintConfigUsage()
{
    Console.WriteLine("Usage: koware config [--quality <label>] [--index <n>] [--player <exe>] [--args <string>] [--show]");
}
