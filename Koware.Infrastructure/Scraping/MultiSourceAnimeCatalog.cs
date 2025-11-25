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
    private readonly IAnimeCatalog _primary;
    private readonly IAnimeCatalog _secondary;
    private readonly ProviderToggleOptions _toggles;
    private readonly ILogger<MultiSourceAnimeCatalog> _logger;

    public MultiSourceAnimeCatalog(IAnimeCatalog primary, IAnimeCatalog secondary, IOptions<ProviderToggleOptions> toggles, ILogger<MultiSourceAnimeCatalog> logger)
    {
        _primary = primary;
        _secondary = secondary;
        _toggles = toggles.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var primaryEnabled = _toggles.IsEnabled("allanime");
        var secondaryEnabled = _toggles.IsEnabled("gogoanime");

        if (!primaryEnabled && !secondaryEnabled)
        {
            _logger.LogWarning("All providers disabled. Enable at least one provider to search.");
            return Array.Empty<Anime>();
        }

        var primaryTask = primaryEnabled ? TryProvider(() => _primary.SearchAsync(query, cancellationToken), "allanime", "search") : null;
        var secondaryTask = secondaryEnabled ? TryProvider(() => _secondary.SearchAsync(query, cancellationToken), "gogoanime", "search") : null;

        var primaryResults = primaryTask is null ? null : await primaryTask;
        if (primaryResults is { Count: > 0 })
        {
            return primaryResults;
        }

        var secondaryResults = secondaryTask is null ? null : await secondaryTask;
        if (secondaryResults is { Count: > 0 })
        {
            return secondaryResults;
        }

        return primaryResults ?? secondaryResults ?? Array.Empty<Anime>();
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (IsGogo(anime.Id))
        {
            return _toggles.IsEnabled("gogoanime")
                ? await TryProvider(() => _secondary.GetEpisodesAsync(anime, cancellationToken), "gogoanime", "episodes") ?? Array.Empty<Episode>()
                : Array.Empty<Episode>();
        }

        if (!_toggles.IsEnabled("allanime"))
        {
            _logger.LogWarning("AllAnime provider disabled while requesting episodes for {AnimeId}.", anime.Id.Value);
            return Array.Empty<Episode>();
        }

        var primaryEpisodes = await TryProvider(() => _primary.GetEpisodesAsync(anime, cancellationToken), "allanime", "episodes");
        if (primaryEpisodes is { Count: > 0 })
        {
            return primaryEpisodes;
        }

        return primaryEpisodes ?? Array.Empty<Episode>();
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (IsGogo(episode.Id))
        {
            return _toggles.IsEnabled("gogoanime")
                ? await TryProvider(() => _secondary.GetStreamsAsync(episode, cancellationToken), "gogoanime", "streams") ?? Array.Empty<StreamLink>()
                : Array.Empty<StreamLink>();
        }

        if (!_toggles.IsEnabled("allanime"))
        {
            _logger.LogWarning("AllAnime provider disabled while requesting streams for {EpisodeId}.", episode.Id.Value);
            return Array.Empty<StreamLink>();
        }

        var primaryStreams = await TryProvider(() => _primary.GetStreamsAsync(episode, cancellationToken), "allanime", "streams");
        if (primaryStreams is { Count: > 0 })
        {
            return primaryStreams;
        }

        return primaryStreams ?? Array.Empty<StreamLink>();
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
