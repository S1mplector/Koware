using System.Diagnostics;
using System.Linq;
using Koware.Application.DependencyInjection;
using Koware.Application.Models;
using Koware.Application.UseCases;
using Koware.Domain.Models;
using Koware.Infrastructure.DependencyInjection;
using Koware.Cli.Configuration;
using Koware.Cli.History;
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
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection("Player"));
    builder.Services.AddSingleton<IWatchHistoryStore, SqliteWatchHistoryStore>();

    return builder.Build();
}

static async Task<int> RunAsync(IHost host, string[] args)
{
    using var scope = host.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("koware.cli");
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
                return await HandlePlanAsync(orchestrator, args, logger, cts.Token);
            case "watch":
            case "play":
                return await HandlePlayAsync(orchestrator, args, services, logger, cts.Token);
            case "last":
                return await HandleLastAsync(args, services, logger, cts.Token);
            case "continue":
                return await HandleContinueAsync(args, services, logger, cts.Token);
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
    CancellationToken cancellationToken,
    bool interactiveSelection = false)
{
    ScrapeResult result;
    if (interactiveSelection)
    {
        result = await orchestrator.ExecuteAsync(
            plan,
            matches => SelectAnimeInteractive(plan.Query, matches),
            cancellationToken);
    }
    else
    {
        result = await orchestrator.ExecuteAsync(plan, cancellationToken);
    }
    if (result.SelectedEpisode is null || result.Streams is null || result.Streams.Count == 0)
    {
        logger.LogWarning("No streams found for the query/episode.");
        RenderPlan(plan, result);
        return 1;
    }

    var stream = result.Streams.First();
    var options = services.GetRequiredService<IOptions<PlayerOptions>>().Value;
    var exitCode = LaunchPlayer(options, stream, logger);

    if (exitCode == 0 && result.SelectedAnime is not null && result.SelectedEpisode is not null)
    {
        var entry = new WatchHistoryEntry
        {
            Provider = "allanime",
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

    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();
    var plan = new ScrapePlan(entry.AnimeTitle, entry.EpisodeNumber, entry.Quality);

    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken, interactiveSelection: false);
}

static async Task<int> HandleContinueAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
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
        entry = await history.GetLastForAnimeAsync(animeQuery, cancellationToken);
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
    var plan = new ScrapePlan(entry.AnimeTitle, targetEpisode, quality);

    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken, interactiveSelection: false);
}

static async Task<int> HandleSearchAsync(ScrapeOrchestrator orchestrator, string[] args, ILogger logger, CancellationToken cancellationToken)
{
    var query = string.Join(' ', args.Skip(1)).Trim();
    if (string.IsNullOrWhiteSpace(query))
    {
        logger.LogWarning("Query is required for search");
        PrintUsage();
        return 1;
    }

    var matches = await orchestrator.SearchAsync(query, cancellationToken);
    RenderSearch(query, matches);
    return 0;
}

static async Task<int> HandlePlanAsync(ScrapeOrchestrator orchestrator, string[] args, ILogger logger, CancellationToken cancellationToken)
{
    ScrapePlan plan;
    try
    {
        plan = ParsePlan(args);
    }
    catch (ArgumentException ex)
    {
        logger.LogWarning(ex, "Invalid arguments for stream/plan command");
        PrintUsage();
        return 1;
    }

    var result = await orchestrator.ExecuteAsync(plan, cancellationToken);
    RenderPlan(plan, result);
    return 0;
}

static async Task<int> HandlePlayAsync(ScrapeOrchestrator orchestrator, string[] args, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
{
    ScrapePlan plan;
    try
    {
        plan = ParsePlan(args);
    }
    catch (ArgumentException ex)
    {
        logger.LogWarning(ex, "Invalid arguments for play command");
        PrintUsage();
        return 1;
    }

    var history = services.GetRequiredService<IWatchHistoryStore>();
    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken, interactiveSelection: true);
}

static Anime? SelectAnimeInteractive(string query, IReadOnlyCollection<Anime> matches)
{
    if (matches.Count == 0)
    {
        return null;
    }

    if (matches.Count == 1)
    {
        return matches.First();
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Multiple matches for \"{query}\":");
    Console.ResetColor();

    var index = 1;
    foreach (var anime in matches)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  [{index}] ");
        Console.ResetColor();
        Console.WriteLine(anime.Title);
        index++;
    }

    while (true)
    {
        Console.Write($"Select anime [1-{matches.Count}] (or 0 to cancel): ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (!int.TryParse(input, out var choice))
        {
            Console.WriteLine("Please enter a number.");
            continue;
        }

        if (choice == 0)
        {
            return null;
        }

        if (choice < 1 || choice > matches.Count)
        {
            Console.WriteLine("Choice out of range.");
            continue;
        }

        return matches.ElementAt(choice - 1);
    }
}

static int LaunchPlayer(PlayerOptions options, StreamLink stream, ILogger logger)
{
    var command = string.IsNullOrWhiteSpace(options.Command) ? "mpv" : options.Command;
    var arguments = string.IsNullOrWhiteSpace(options.Args)
        ? $"\"{stream.Url}\""
        : $"{options.Args} \"{stream.Url}\"";

    try
    {
        var start = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false
        };

        using var proc = Process.Start(start);
        if (proc is null)
        {
            logger.LogError("Failed to start player process.");
            return 1;
        }

        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unable to launch player {Command}", command);
        return 1;
    }
}

static ScrapePlan ParsePlan(string[] args)
{
    var queryParts = new List<string>();
    int? episodeNumber = null;
    string? preferredQuality = null;

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--episode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out var parsedEpisode))
            {
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

        queryParts.Add(arg);
    }

    if (episodeNumber is null && queryParts.Count > 1 && int.TryParse(queryParts[^1], out var positionalEpisode))
    {
        episodeNumber = positionalEpisode;
        queryParts.RemoveAt(queryParts.Count - 1);
    }

    var query = string.Join(' ', queryParts).Trim();
    if (string.IsNullOrWhiteSpace(query))
    {
        throw new ArgumentException("Query is required", nameof(args));
    }

    return new ScrapePlan(query, episodeNumber, preferredQuality);
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
        foreach (var stream in result.Streams)
        {
            Console.WriteLine($"  {stream.Quality} -> {stream.Url}");
        }
    }
}

static void PrintUsage()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Koware CLI");
    Console.ResetColor();
    Console.WriteLine("Usage:");
    Console.WriteLine("  search <query>");
    Console.WriteLine("  stream <query> [--episode <number>] [--quality <label>]");
    Console.WriteLine("  watch <query> [--episode <number>] [--quality <label>]");
    Console.WriteLine("  play  <query> [--episode <number>] [--quality <label>] (alias for 'watch')");
    Console.WriteLine("  last  [--play] [--json]");
    Console.WriteLine("  continue [<anime name>] [--from <episode>] [--quality <label>]");
}
