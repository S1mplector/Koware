// Author: Ilgaz Mehmetoğlu
using System.Reflection;
using Koware.Application.Abstractions;
using Koware.Application.Environment;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Browser.Services;

/// <summary>
/// Service providing access to anime and manga catalogs for the browser UI.
/// Uses the same catalog composition and config path as the CLI.
/// </summary>
public sealed class CatalogService
{
    private readonly IAnimeCatalog _animeCatalog;
    private readonly IMangaCatalog _mangaCatalog;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(IAnimeCatalog animeCatalog, IMangaCatalog mangaCatalog, ILogger<CatalogService> logger)
    {
        _animeCatalog = animeCatalog;
        _mangaCatalog = mangaCatalog;
        _logger = logger;
    }

    public string ConfigurationFilePath => KowarePaths.GetUserConfigFilePath();

    public string VersionLabel
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null)
            {
                return "Version unknown";
            }

            var parts = version.ToString().Split('.');
            var trimmed = parts.Length >= 3 ? string.Join('.', parts.Take(3)) : version.ToString();
            return $"Version {trimmed}";
        }
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAnimeAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var results = await _animeCatalog.SearchAsync(query, ct);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search anime for query: {Query}", query);
            return Array.Empty<Anime>();
        }
    }

    public async Task<IReadOnlyCollection<Manga>> SearchMangaAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var results = await _mangaCatalog.SearchAsync(query, ct);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search manga for query: {Query}", query);
            return Array.Empty<Manga>();
        }
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken ct = default)
    {
        try
        {
            return await _animeCatalog.GetEpisodesAsync(anime, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get episodes for anime: {Title}", anime.Title);
            return Array.Empty<Episode>();
        }
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken ct = default)
    {
        try
        {
            return await _mangaCatalog.GetChaptersAsync(manga, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get chapters for manga: {Title}", manga.Title);
            return Array.Empty<Chapter>();
        }
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken ct = default)
    {
        try
        {
            return await _animeCatalog.GetStreamsAsync(episode, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get streams for episode: {Title}", episode.Title);
            return Array.Empty<StreamLink>();
        }
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        try
        {
            return await _mangaCatalog.GetPagesAsync(chapter, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pages for chapter: {Title}", chapter.Title);
            return Array.Empty<ChapterPage>();
        }
    }
}
