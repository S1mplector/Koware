// Author: Ilgaz Mehmetoğlu
// 9anime/aniwatch-style provider implementation using the shared HiAnime-like runtime.
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class NineAnimeCatalog : IAnimeCatalog
{
    private readonly HiAnimeLikeCatalogCore _core;

    public NineAnimeCatalog(HttpClient httpClient, IOptions<NineAnimeOptions> options, ILogger<NineAnimeCatalog> logger)
    {
        var value = options.Value;
        _core = new HiAnimeLikeCatalogCore(
            httpClient,
            providerSlug: "9anime",
            providerName: "NineAnime",
            enabled: value.Enabled,
            baseUrl: value.BaseUrl,
            referer: value.EffectiveReferer,
            userAgent: value.UserAgent,
            preferredServer: value.PreferredServer,
            searchLimit: value.SearchLimit,
            logger);
    }

    public bool IsConfigured => _core.IsConfigured;

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => _core.SearchAsync(query, cancellationToken);

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
        => _core.SearchAsync(query, filters, cancellationToken);

    public Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
        => _core.BrowsePopularAsync(filters, cancellationToken);

    public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
        => _core.GetEpisodesAsync(anime, cancellationToken);

    public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
        => _core.GetStreamsAsync(episode, cancellationToken);
}
