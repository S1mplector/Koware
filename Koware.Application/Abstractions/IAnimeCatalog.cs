// Author: Ilgaz Mehmetoğlu
using Koware.Domain.Models;

namespace Koware.Application.Abstractions;

/// <summary>
/// Abstraction for anime catalog operations (search, episodes, streams).
/// Implemented by single-provider catalogs (AllAnime, GogoAnime) and multi-source aggregators.
/// </summary>
public interface IAnimeCatalog
{
    /// <summary>
    /// Search for anime by title or keywords.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of matching anime.</returns>
    Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all episodes for an anime.
    /// </summary>
    /// <param name="anime">The anime to fetch episodes for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of episodes.</returns>
    Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available streams for an episode.
    /// </summary>
    /// <param name="episode">The episode to fetch streams for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of playable streams.</returns>
    Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default);
}
