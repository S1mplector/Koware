// Author: Ilgaz Mehmetoğlu
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Storage;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Aggregating IAnimeCatalog that combines built-in and dynamic providers.
/// Searches across all active providers and returns combined results.
/// </summary>
public sealed class AggregateAnimeCatalog : IAnimeCatalog
{
    private readonly IReadOnlyDictionary<string, BuiltInAnimeProvider> _builtInProviders;
    private readonly IReadOnlyList<BuiltInAnimeProvider> _orderedBuiltInProviders;
    private readonly IProviderStore _providerStore;
    private readonly ITransformEngine _transforms;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AggregateAnimeCatalog> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public AggregateAnimeCatalog(
        IEnumerable<BuiltInAnimeProvider> builtInProviders,
        IProviderStore providerStore,
        ITransformEngine transforms,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        var map = new Dictionary<string, BuiltInAnimeProvider>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BuiltInAnimeProvider>();

        foreach (var provider in builtInProviders)
        {
            if (provider.Catalog is null || string.IsNullOrWhiteSpace(provider.Slug))
            {
                continue;
            }

            var slug = provider.Slug.Trim().ToLowerInvariant();
            if (map.ContainsKey(slug))
            {
                continue;
            }

            var normalized = new BuiltInAnimeProvider(
                slug,
                string.IsNullOrWhiteSpace(provider.Name) ? slug : provider.Name.Trim(),
                provider.Catalog);

            map[slug] = normalized;
            ordered.Add(normalized);
        }

        _builtInProviders = map;
        _orderedBuiltInProviders = ordered;
        _providerStore = providerStore;
        _transforms = transforms;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AggregateAnimeCatalog>();
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        return await SearchAsync(query, SearchFilters.Empty, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        var candidates = await GetProviderCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return Array.Empty<Anime>();
        }

        var results = await Task.WhenAll(candidates.Select(provider =>
            SafeSearchAsync(provider, query, filters, cancellationToken)));

        return RankAnimeResults(results.SelectMany(r => r), query);
    }

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        var candidates = await GetProviderCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return Array.Empty<Anime>();
        }

        var results = await Task.WhenAll(candidates.Select(provider =>
            SafeBrowseAsync(provider, filters, cancellationToken)));

        return RankAnimeResults(results.SelectMany(r => r), query: string.Empty);
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (ProviderAggregationHelpers.TryExtractProviderSlug(anime.Id.Value, out var providerSlug))
        {
            if (_builtInProviders.TryGetValue(providerSlug, out var builtInProvider))
            {
                var strippedAnime = StripProviderPrefix(anime, providerSlug);
                return await SafeGetEpisodesAsync(builtInProvider.Catalog, builtInProvider.Name, providerSlug, strippedAnime, cancellationToken);
            }

            var dynamicConfig = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (dynamicConfig is not null)
            {
                var dynamicCatalog = CreateDynamicCatalog(dynamicConfig);
                var strippedAnime = StripProviderPrefix(anime, providerSlug);
                return await SafeGetEpisodesAsync(dynamicCatalog, dynamicConfig.Name, providerSlug, strippedAnime, cancellationToken);
            }
        }

        foreach (var provider in await GetProviderCandidatesAsync(cancellationToken))
        {
            var episodes = await SafeGetEpisodesAsync(provider.Catalog, provider.Name, provider.Slug, anime, cancellationToken);
            if (episodes.Count > 0)
            {
                return episodes;
            }
        }

        return Array.Empty<Episode>();
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (ProviderAggregationHelpers.TryExtractProviderSlug(episode.Id.Value, out var providerSlug))
        {
            if (_builtInProviders.TryGetValue(providerSlug, out var builtInProvider))
            {
                var strippedEpisode = StripProviderPrefix(episode, providerSlug);
                return await SafeGetStreamsAsync(builtInProvider.Catalog, builtInProvider.Name, builtInProvider.Slug, strippedEpisode, cancellationToken);
            }

            var dynamicConfig = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (dynamicConfig is not null)
            {
                var dynamicCatalog = CreateDynamicCatalog(dynamicConfig);
                var strippedEpisode = StripProviderPrefix(episode, providerSlug);
                return await SafeGetStreamsAsync(dynamicCatalog, dynamicConfig.Name, providerSlug, strippedEpisode, cancellationToken);
            }
        }

        foreach (var provider in await GetProviderCandidatesAsync(cancellationToken))
        {
            var streams = await SafeGetStreamsAsync(provider.Catalog, provider.Name, provider.Slug, episode, cancellationToken);
            if (streams.Count > 0)
            {
                return streams;
            }
        }

        return Array.Empty<StreamLink>();
    }

    private async Task<IReadOnlyList<AnimeProviderCandidate>> GetProviderCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<AnimeProviderCandidate>();
        var rank = 0;

        foreach (var provider in _orderedBuiltInProviders)
        {
            candidates.Add(new AnimeProviderCandidate(provider.Slug, provider.Name, provider.Catalog, rank++));
        }

        var dynamicConfigs = await GetDynamicProviderConfigsAsync(cancellationToken);
        foreach (var config in dynamicConfigs)
        {
            candidates.Add(new AnimeProviderCandidate(
                ProviderAggregationHelpers.NormalizeSlug(config.Slug),
                string.IsNullOrWhiteSpace(config.Name) ? config.Slug : config.Name,
                CreateDynamicCatalog(config),
                rank++));
        }

        return candidates;
    }

    private async Task<IReadOnlyList<DynamicProviderConfig>> GetDynamicProviderConfigsAsync(CancellationToken cancellationToken)
    {
        var bySlug = new Dictionary<string, (DynamicProviderConfig Config, bool IsActive)>(StringComparer.OrdinalIgnoreCase);
        var providers = await _providerStore.ListAsync(cancellationToken);

        foreach (var provider in providers)
        {
            if (provider.Type is not (ProviderType.Anime or ProviderType.Both))
            {
                continue;
            }

            var config = await _providerStore.GetAsync(provider.Slug, cancellationToken);
            if (config is not null && config.Type is (ProviderType.Anime or ProviderType.Both))
            {
                bySlug[ProviderAggregationHelpers.NormalizeSlug(config.Slug)] = (config, provider.IsActive);
            }
        }

        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeConfig is not null && activeConfig.Type is (ProviderType.Anime or ProviderType.Both))
        {
            bySlug[ProviderAggregationHelpers.NormalizeSlug(activeConfig.Slug)] = (activeConfig, true);
        }

        return bySlug.Values
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Config.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Config)
            .ToArray();
    }

    private DynamicAnimeCatalog CreateDynamicCatalog(DynamicProviderConfig config)
    {
        return new DynamicAnimeCatalog(
            config,
            _httpClient,
            _transforms,
            _loggerFactory.CreateLogger<DynamicAnimeCatalog>());
    }

    private async Task<IReadOnlyCollection<ProviderResult<Anime>>> SafeSearchAsync(
        AnimeProviderCandidate provider,
        string query,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await provider.Catalog.SearchAsync(query, filters, cancellationToken);
            return PrefixAnimeIds(results, provider.Slug)
                .Select((anime, index) => new ProviderResult<Anime>(anime, provider.Rank, index))
                .ToArray();
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Search blocked for provider {Provider}: {Kind} - {Message}",
                provider.Name,
                ex.Kind,
                ex.Message);
            return Array.Empty<ProviderResult<Anime>>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Search failed for provider {Provider}, continuing with other providers", provider.Name);
            return Array.Empty<ProviderResult<Anime>>();
        }
    }

    private async Task<IReadOnlyCollection<ProviderResult<Anime>>> SafeBrowseAsync(
        AnimeProviderCandidate provider,
        SearchFilters? filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await provider.Catalog.BrowsePopularAsync(filters, cancellationToken);
            return PrefixAnimeIds(results, provider.Slug)
                .Select((anime, index) => new ProviderResult<Anime>(anime, provider.Rank, index))
                .ToArray();
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Browse blocked for provider {Provider}: {Kind} - {Message}",
                provider.Name,
                ex.Kind,
                ex.Message);
            return Array.Empty<ProviderResult<Anime>>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Browse failed for provider {Provider}, continuing with other providers", provider.Name);
            return Array.Empty<ProviderResult<Anime>>();
        }
    }

    private async Task<IReadOnlyCollection<Episode>> SafeGetEpisodesAsync(
        IAnimeCatalog catalog,
        string providerName,
        string providerSlug,
        Anime anime,
        CancellationToken cancellationToken)
    {
        try
        {
            var episodes = await catalog.GetEpisodesAsync(anime, cancellationToken);
            return PrefixEpisodeIds(episodes, providerSlug);
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Episode fetch blocked for provider {Provider}: {Kind} - {Message}",
                providerName,
                ex.Kind,
                ex.Message);
            return Array.Empty<Episode>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Episode fetch failed for provider {Provider}", providerName);
            return Array.Empty<Episode>();
        }
    }

    private async Task<IReadOnlyCollection<StreamLink>> SafeGetStreamsAsync(
        IAnimeCatalog catalog,
        string providerName,
        string providerSlug,
        Episode episode,
        CancellationToken cancellationToken)
    {
        try
        {
            var streams = await catalog.GetStreamsAsync(episode, cancellationToken);
            return TagStreams(streams, providerSlug, providerName);
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Stream fetch blocked for provider {Provider}: {Kind} - {Message}",
                providerName,
                ex.Kind,
                ex.Message);
            return Array.Empty<StreamLink>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Stream fetch failed for provider {Provider}", providerName);
            return Array.Empty<StreamLink>();
        }
    }

    private static IReadOnlyCollection<Anime> RankAnimeResults(IEnumerable<ProviderResult<Anime>> results, string query)
    {
        return results
            .GroupBy(r => r.Item.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.ProviderRank).ThenBy(r => r.ResultIndex).First())
            .OrderByDescending(r => ProviderAggregationHelpers.ScoreTitle(query, r.Item.Title))
            .ThenBy(r => r.ProviderRank)
            .ThenBy(r => r.ResultIndex)
            .Select(r => r.Item)
            .ToArray();
    }

    private static IReadOnlyCollection<Anime> PrefixAnimeIds(IReadOnlyCollection<Anime> results, string providerSlug)
    {
        return results.Select(anime => new Anime(
            new AnimeId(ProviderAggregationHelpers.PrefixId(providerSlug, anime.Id.Value)),
            anime.Title,
            anime.Synopsis,
            anime.CoverImage,
            anime.DetailPage,
            PrefixEpisodeIds(anime.Episodes, providerSlug))).ToArray();
    }

    private static IReadOnlyCollection<Episode> PrefixEpisodeIds(IReadOnlyCollection<Episode> episodes, string providerSlug)
    {
        return episodes.Select(ep => new Episode(
            new EpisodeId(ProviderAggregationHelpers.PrefixId(providerSlug, ep.Id.Value)),
            ep.Title,
            ep.Number,
            ep.PageUrl)).ToArray();
    }

    private static Anime StripProviderPrefix(Anime anime, string providerSlug)
    {
        return new Anime(
            new AnimeId(ProviderAggregationHelpers.RemovePrefix(providerSlug, anime.Id.Value)),
            anime.Title,
            anime.Synopsis,
            anime.CoverImage,
            anime.DetailPage,
            anime.Episodes.Select(ep => StripProviderPrefix(ep, providerSlug)).ToArray());
    }

    private static Episode StripProviderPrefix(Episode episode, string providerSlug)
    {
        return new Episode(
            new EpisodeId(ProviderAggregationHelpers.RemovePrefix(providerSlug, episode.Id.Value)),
            episode.Title,
            episode.Number,
            episode.PageUrl);
    }

    private static IReadOnlyCollection<StreamLink> TagStreams(IReadOnlyCollection<StreamLink> streams, string providerSlug, string providerName)
    {
        return streams.Select(stream => stream with
        {
            SourceTag = string.IsNullOrWhiteSpace(stream.SourceTag) ? providerSlug : stream.SourceTag,
            Provider = string.IsNullOrWhiteSpace(stream.Provider) ? providerName : stream.Provider
        }).ToArray();
    }

    public sealed record BuiltInAnimeProvider(string Slug, string Name, IAnimeCatalog Catalog);

    private sealed record AnimeProviderCandidate(string Slug, string Name, IAnimeCatalog Catalog, int Rank);

    private sealed record ProviderResult<T>(T Item, int ProviderRank, int ResultIndex);
}
