using System.Diagnostics;
using System.IO;
using System.Linq;
using Koware.Application.DependencyInjection;
using Koware.Application.Models;
using Koware.Application.UseCases;
using Koware.Domain.Models;
using Koware.Infrastructure.DependencyInjection;
using Koware.Infrastructure.Configuration;
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
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("koware", LogLevel.Information);
    builder.Logging.AddFilter("Koware", LogLevel.Information);

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
    CancellationToken cancellationToken)
{
    var result = await orchestrator.ExecuteAsync(plan, cancellationToken);
    if (result.SelectedEpisode is null || result.Streams is null || result.Streams.Count == 0)
    {
        logger.LogWarning("No streams found for the query/episode.");
        RenderPlan(plan, result);
        return 1;
    }

    var stream = PickBestStream(result.Streams);
    if (stream is null)
    {
        logger.LogWarning("No playable streams found.");
        return 1;
    }
    logger.LogInformation("Selected stream {Quality} from host {Host}", stream.Quality ?? "unknown", stream.Url.Host);

    var playerOptions = services.GetRequiredService<IOptions<PlayerOptions>>().Value;
    var allAnimeOptions = services.GetService<IOptions<AllAnimeOptions>>()?.Value;
    var exitCode = LaunchPlayer(playerOptions, stream, logger, allAnimeOptions?.Referer, allAnimeOptions?.UserAgent);

    if (result.SelectedAnime is not null && result.SelectedEpisode is not null)
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

    var orchestrator = services.GetRequiredService<ScrapeOrchestrator>();
    var plan = new ScrapePlan(entry.AnimeTitle, entry.EpisodeNumber, entry.Quality);

    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
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

    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
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

    plan = await MaybeSelectMatchAsync(orchestrator, plan, logger, cancellationToken);

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

    plan = await MaybeSelectMatchAsync(orchestrator, plan, logger, cancellationToken);

    var history = services.GetRequiredService<IWatchHistoryStore>();
    return await ExecuteAndPlayAsync(orchestrator, plan, services, history, logger, cancellationToken);
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

static string? ResolveExecutablePath(string command)
{
    if (string.IsNullOrWhiteSpace(command))
    {
        return null;
    }

    if (Path.IsPathRooted(command) && File.Exists(command))
    {
        return command;
    }

    var wellKnown = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
    };

    foreach (var path in wellKnown)
    {
        if (File.Exists(path) && command.Equals("vlc", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
    }

    var candidates = Path.HasExtension(command)
        ? new[] { command }
        : new[] { command, $"{command}.exe" };

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

static int LaunchPlayer(PlayerOptions options, StreamLink stream, ILogger logger, string? httpReferrer, string? httpUserAgent)
{
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(options.Command))
    {
        candidates.Add(options.Command);
    }
    candidates.AddRange(new[] { "vlc", "mpv" });
    candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    string? resolvedCommand = null;
    string chosen = candidates.FirstOrDefault(c => (resolvedCommand = ResolveExecutablePath(c)) is not null)
        ?? string.Empty;

    if (resolvedCommand is null)
    {
        logger.LogError("No supported player found (tried {Candidates}). Install VLC or mpv, or set Player:Command in appsettings.json.", string.Join(", ", candidates));
        return 1;
    }

    var playerName = Path.GetFileNameWithoutExtension(resolvedCommand);
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

    try
    {
        logger.LogInformation("Launching player: {Player} {Args}", resolvedCommand, arguments);
        var start = new ProcessStartInfo
        {
            FileName = resolvedCommand,
            Arguments = arguments,
            UseShellExecute = false
        };

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
        logger.LogError(ex, "Unable to launch player {Command}", chosen);
        return 1;
    }
}

static ScrapePlan ParsePlan(string[] args)
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
        episodeNumber = positionalEpisode;
        queryParts.RemoveAt(queryParts.Count - 1);
    }

    var query = string.Join(' ', queryParts).Trim();
    if (string.IsNullOrWhiteSpace(query))
    {
        throw new ArgumentException("Query is required", nameof(args));
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
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Koware CLI");
    Console.ResetColor();
    Console.WriteLine("Usage:");
    Console.WriteLine("  search <query>");
    Console.WriteLine("  stream <query> [--episode <number>] [--quality <label>] [--index <n>] [--non-interactive]");
    Console.WriteLine("  watch <query> [--episode <number>] [--quality <label>] [--index <n>] [--non-interactive]");
    Console.WriteLine("  play  <query> [--episode <number>] [--quality <label>] [--index <n>] [--non-interactive] (alias for 'watch')");
    Console.WriteLine("  last  [--play] [--json]");
    Console.WriteLine("  continue [<anime name>] [--from <episode>] [--quality <label>]");
}
