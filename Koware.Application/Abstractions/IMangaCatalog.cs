// Author: Ilgaz Mehmetoğlu
using Koware.Domain.Models;

namespace Koware.Application.Abstractions;

/// <summary>
/// Abstraction for manga catalog operations (search, chapters, pages).
/// Implemented by single-provider catalogs (AllManga) and multi-source aggregators.
/// </summary>
public interface IMangaCatalog
{
    /// <summary>
    /// Search for manga by title or keywords.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of matching manga.</returns>
    Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for manga with filters.
    /// </summary>
    /// <param name="query">Search query (can be empty for browse).</param>
    /// <param name="filters">Search filters (genre, year, status, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of matching manga.</returns>
    Task<IReadOnlyCollection<Manga>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
        => SearchAsync(query, cancellationToken); // Default: ignore filters

    /// <summary>
    /// Browse popular/trending manga (for recommendations).
    /// </summary>
    /// <param name="filters">Optional filters to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of popular manga.</returns>
    Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<Manga>>(Array.Empty<Manga>()); // Optional: not all providers support this

    /// <summary>
    /// Get all chapters for a manga.
    /// </summary>
    /// <param name="manga">The manga to fetch chapters for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of chapters.</returns>
    Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all pages/images for a chapter.
    /// </summary>
    /// <param name="chapter">The chapter to fetch pages for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of chapter pages with image URLs.</returns>
    Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default);
}
