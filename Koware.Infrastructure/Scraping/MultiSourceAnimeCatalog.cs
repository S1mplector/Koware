// Author: Ilgaz Mehmetoğlu
// Composite catalog that prefers a primary provider and falls back to secondary (e.g., GogoAnime).
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class MultiSourceAnimeCatalog : IAnimeCatalog
{
    private readonly AllAnimeCatalog _primary;
    private readonly GogoAnimeCatalog _secondary;
    private readonly ProviderToggleOptions _toggles;
    private readonly ILogger<MultiSourceAnimeCatalog> _logger;

    public MultiSourceAnimeCatalog(AllAnimeCatalog primary, GogoAnimeCatalog secondary, IOptions<ProviderToggleOptions> toggles, ILogger<MultiSourceAnimeCatalog> logger)
    {
        _primary = primary;
        _secondary = secondary;
        _toggles = toggles.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (_toggles.IsEnabled("allanime"))
        {
            var primaryResults = await TryProvider(() => _primary.SearchAsync(query, cancellationToken), "allanime", "search");
            if (primaryResults is { Count: > 0 })
            {
                return primaryResults;
            }
        }

        if (_toggles.IsEnabled("gogoanime"))
        {
            var secondary = await _secondary.SearchAsync(query, cancellationToken);
            return secondary;
        }

        _logger.LogWarning("All providers disabled. Enable at least one provider to search.");
        return Array.Empty<Anime>();
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (IsGogo(anime.Id))
        {
            return _toggles.IsEnabled("gogoanime")
                ? await _secondary.GetEpisodesAsync(anime, cancellationToken)
                : Array.Empty<Episode>();
        }

        if (_toggles.IsEnabled("allanime"))
        {
            var primaryEpisodes = await TryProvider(() => _primary.GetEpisodesAsync(anime, cancellationToken), "allanime", "episodes");
            if (primaryEpisodes is { Count: > 0 })
            {
                return primaryEpisodes;
            }
        }

        if (_toggles.IsEnabled("gogoanime"))
        {
            return await _secondary.GetEpisodesAsync(anime, cancellationToken);
        }

        return Array.Empty<Episode>();
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (IsGogo(episode.Id))
        {
            return _toggles.IsEnabled("gogoanime")
                ? await _secondary.GetStreamsAsync(episode, cancellationToken)
                : Array.Empty<StreamLink>();
        }

        if (_toggles.IsEnabled("allanime"))
        {
            var primaryStreams = await TryProvider(() => _primary.GetStreamsAsync(episode, cancellationToken), "allanime", "streams");
            if (primaryStreams is { Count: > 0 })
            {
                return primaryStreams;
            }
        }

        if (_toggles.IsEnabled("gogoanime"))
        {
            return await _secondary.GetStreamsAsync(episode, cancellationToken);
        }

        return Array.Empty<StreamLink>();
    }

    private async Task<IReadOnlyCollection<T>?> TryProvider<T>(Func<Task<IReadOnlyCollection<T>>> action, string provider, string stage)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider} provider failed during {Stage}, attempting fallback.", provider, stage);
            return null;
        }
    }

    private static bool IsGogo(AnimeId id) => id.Value.StartsWith("gogo:", StringComparison.OrdinalIgnoreCase);
    private static bool IsGogo(EpisodeId id) => id.Value.StartsWith("gogo:", StringComparison.OrdinalIgnoreCase);
}
