// Author: Ilgaz Mehmetoğlu
// Composite catalog that prefers a primary provider and falls back to secondary (e.g., GogoAnime).
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Infrastructure.Scraping;

public sealed class MultiSourceAnimeCatalog : IAnimeCatalog
{
    private readonly IAnimeCatalog _primary;
    private readonly IAnimeCatalog _secondary;
    private readonly ILogger<MultiSourceAnimeCatalog> _logger;

    public MultiSourceAnimeCatalog(IAnimeCatalog primary, IAnimeCatalog secondary, ILogger<MultiSourceAnimeCatalog> logger)
    {
        _primary = primary;
        _secondary = secondary;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var primaryResults = await TryPrimary(() => _primary.SearchAsync(query, cancellationToken), "search");
        if (primaryResults is { Count: > 0 })
        {
            return primaryResults;
        }

        var secondary = await _secondary.SearchAsync(query, cancellationToken);
        return secondary;
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (IsGogo(anime.Id))
        {
            return await _secondary.GetEpisodesAsync(anime, cancellationToken);
        }

        var primaryEpisodes = await TryPrimary(() => _primary.GetEpisodesAsync(anime, cancellationToken), "episodes");
        if (primaryEpisodes is { Count: > 0 })
        {
            return primaryEpisodes;
        }

        return await _secondary.GetEpisodesAsync(anime, cancellationToken);
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (IsGogo(episode.Id))
        {
            return await _secondary.GetStreamsAsync(episode, cancellationToken);
        }

        var primaryStreams = await TryPrimary(() => _primary.GetStreamsAsync(episode, cancellationToken), "streams");
        if (primaryStreams is { Count: > 0 })
        {
            return primaryStreams;
        }

        return await _secondary.GetStreamsAsync(episode, cancellationToken);
    }

    private async Task<IReadOnlyCollection<T>?> TryPrimary<T>(Func<Task<IReadOnlyCollection<T>>> action, string stage)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Primary provider failed during {Stage}, falling back to secondary.", stage);
            return null;
        }
    }

    private static bool IsGogo(AnimeId id) => id.Value.StartsWith("gogo:", StringComparison.OrdinalIgnoreCase);
    private static bool IsGogo(EpisodeId id) => id.Value.StartsWith("gogo:", StringComparison.OrdinalIgnoreCase);
}
