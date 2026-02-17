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
        var allResults = new List<Anime>();
        var tasks = new List<Task<IReadOnlyCollection<Anime>>>();

        foreach (var provider in _orderedBuiltInProviders)
        {
            tasks.Add(SafeSearchAsync(provider.Catalog, provider.Name, provider.Slug, query, filters, cancellationToken));
        }

        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeConfig is not null && activeConfig.Type is ProviderType.Anime or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeConfig);
            tasks.Add(SafeSearchAsync(dynamicCatalog, activeConfig.Name, activeConfig.Slug, query, filters, cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return Array.Empty<Anime>();
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            allResults.AddRange(result);
        }

        return allResults;
    }

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        var allResults = new List<Anime>();
        var tasks = new List<Task<IReadOnlyCollection<Anime>>>();

        foreach (var provider in _orderedBuiltInProviders)
        {
            tasks.Add(SafeBrowseAsync(provider.Catalog, provider.Name, provider.Slug, filters, cancellationToken));
        }

        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeConfig is not null && activeConfig.Type is ProviderType.Anime or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeConfig);
            tasks.Add(SafeBrowseAsync(dynamicCatalog, activeConfig.Name, activeConfig.Slug, filters, cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return Array.Empty<Anime>();
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            allResults.AddRange(result);
        }

        return allResults;
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (TryExtractProviderSlug(anime.Id.Value, out var providerSlug))
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

        var fallbackProvider = GetFallbackBuiltInProvider();
        if (fallbackProvider is not null)
        {
            return await SafeGetEpisodesAsync(fallbackProvider.Catalog, fallbackProvider.Name, fallbackProvider.Slug, anime, cancellationToken);
        }

        var activeDynamic = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeDynamic is not null && activeDynamic.Type is ProviderType.Anime or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeDynamic);
            return await SafeGetEpisodesAsync(dynamicCatalog, activeDynamic.Name, activeDynamic.Slug, anime, cancellationToken);
        }

        return Array.Empty<Episode>();
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (TryExtractProviderSlug(episode.Id.Value, out var providerSlug))
        {
            if (_builtInProviders.TryGetValue(providerSlug, out var builtInProvider))
            {
                var strippedEpisode = StripProviderPrefix(episode, providerSlug);
                return await SafeGetStreamsAsync(builtInProvider.Catalog, builtInProvider.Name, strippedEpisode, cancellationToken);
            }

            var dynamicConfig = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (dynamicConfig is not null)
            {
                var dynamicCatalog = CreateDynamicCatalog(dynamicConfig);
                var strippedEpisode = StripProviderPrefix(episode, providerSlug);
                return await SafeGetStreamsAsync(dynamicCatalog, dynamicConfig.Name, strippedEpisode, cancellationToken);
            }
        }

        var fallbackProvider = GetFallbackBuiltInProvider();
        if (fallbackProvider is not null)
        {
            return await SafeGetStreamsAsync(fallbackProvider.Catalog, fallbackProvider.Name, episode, cancellationToken);
        }

        var activeDynamic = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeDynamic is not null && activeDynamic.Type is ProviderType.Anime or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeDynamic);
            return await SafeGetStreamsAsync(dynamicCatalog, activeDynamic.Name, episode, cancellationToken);
        }

        return Array.Empty<StreamLink>();
    }

    private BuiltInAnimeProvider? GetFallbackBuiltInProvider()
    {
        if (_builtInProviders.TryGetValue("allanime", out var allanime))
        {
            return allanime;
        }

        return _orderedBuiltInProviders.FirstOrDefault();
    }

    private DynamicAnimeCatalog CreateDynamicCatalog(DynamicProviderConfig config)
    {
        return new DynamicAnimeCatalog(
            config,
            _httpClient,
            _transforms,
            _loggerFactory.CreateLogger<DynamicAnimeCatalog>());
    }

    private async Task<IReadOnlyCollection<Anime>> SafeSearchAsync(
        IAnimeCatalog catalog,
        string providerName,
        string providerSlug,
        string query,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await catalog.SearchAsync(query, filters, cancellationToken);
            return PrefixAnimeIds(results, providerSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search failed for provider {Provider}, continuing with other providers", providerName);
            return Array.Empty<Anime>();
        }
    }

    private async Task<IReadOnlyCollection<Anime>> SafeBrowseAsync(
        IAnimeCatalog catalog,
        string providerName,
        string providerSlug,
        SearchFilters? filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await catalog.BrowsePopularAsync(filters, cancellationToken);
            return PrefixAnimeIds(results, providerSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browse failed for provider {Provider}, continuing with other providers", providerName);
            return Array.Empty<Anime>();
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Episode fetch failed for provider {Provider}", providerName);
            return Array.Empty<Episode>();
        }
    }

    private async Task<IReadOnlyCollection<StreamLink>> SafeGetStreamsAsync(
        IAnimeCatalog catalog,
        string providerName,
        Episode episode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.GetStreamsAsync(episode, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream fetch failed for provider {Provider}", providerName);
            return Array.Empty<StreamLink>();
        }
    }

    private static IReadOnlyCollection<Anime> PrefixAnimeIds(IReadOnlyCollection<Anime> results, string providerSlug)
    {
        return results.Select(anime => new Anime(
            new AnimeId(PrefixId(providerSlug, anime.Id.Value)),
            anime.Title,
            anime.Synopsis,
            anime.CoverImage,
            anime.DetailPage,
            PrefixEpisodeIds(anime.Episodes, providerSlug))).ToArray();
    }

    private static IReadOnlyCollection<Episode> PrefixEpisodeIds(IReadOnlyCollection<Episode> episodes, string providerSlug)
    {
        return episodes.Select(ep => new Episode(
            new EpisodeId(PrefixId(providerSlug, ep.Id.Value)),
            ep.Title,
            ep.Number,
            ep.PageUrl)).ToArray();
    }

    private static Anime StripProviderPrefix(Anime anime, string providerSlug)
    {
        return new Anime(
            new AnimeId(RemovePrefix(providerSlug, anime.Id.Value)),
            anime.Title,
            anime.Synopsis,
            anime.CoverImage,
            anime.DetailPage,
            anime.Episodes.Select(ep => StripProviderPrefix(ep, providerSlug)).ToArray());
    }

    private static Episode StripProviderPrefix(Episode episode, string providerSlug)
    {
        return new Episode(
            new EpisodeId(RemovePrefix(providerSlug, episode.Id.Value)),
            episode.Title,
            episode.Number,
            episode.PageUrl);
    }

    private static string PrefixId(string providerSlug, string id)
    {
        if (string.IsNullOrWhiteSpace(providerSlug) || string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return id.StartsWith(providerSlug + ":", StringComparison.OrdinalIgnoreCase)
            ? id
            : $"{providerSlug}:{id}";
    }

    private static string RemovePrefix(string providerSlug, string id)
    {
        if (string.IsNullOrWhiteSpace(providerSlug) || string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        var prefix = providerSlug + ":";
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? id[prefix.Length..]
            : id;
    }

    private static bool TryExtractProviderSlug(string id, out string providerSlug)
    {
        providerSlug = string.Empty;

        var colonIndex = id.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        var possibleSlug = id[..colonIndex];
        if (possibleSlug.All(char.IsDigit))
        {
            return false;
        }

        providerSlug = possibleSlug.ToLowerInvariant();
        return true;
    }

    public sealed record BuiltInAnimeProvider(string Slug, string Name, IAnimeCatalog Catalog);
}
