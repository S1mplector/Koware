// Author: Ilgaz Mehmetoğlu
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Storage;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Aggregating IMangaCatalog that combines built-in and dynamic providers.
/// Searches across all active providers and routes chapter/page requests by provider-prefixed IDs.
/// </summary>
public sealed class AggregateMangaCatalog : IMangaCatalog
{
    private readonly IReadOnlyDictionary<string, BuiltInMangaProvider> _builtInProviders;
    private readonly IReadOnlyList<BuiltInMangaProvider> _orderedBuiltInProviders;
    private readonly IProviderStore _providerStore;
    private readonly ITransformEngine _transforms;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AggregateMangaCatalog> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public AggregateMangaCatalog(
        IEnumerable<BuiltInMangaProvider> builtInProviders,
        IProviderStore providerStore,
        ITransformEngine transforms,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        var map = new Dictionary<string, BuiltInMangaProvider>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BuiltInMangaProvider>();

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

            var normalized = new BuiltInMangaProvider(
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
        _logger = loggerFactory.CreateLogger<AggregateMangaCatalog>();
    }

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        return await SearchAsync(query, SearchFilters.Empty, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        var candidates = await GetProviderCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return Array.Empty<Manga>();
        }

        var results = await Task.WhenAll(candidates.Select(provider =>
            SafeSearchAsync(provider, query, filters, cancellationToken)));

        return RankMangaResults(results.SelectMany(r => r), query);
    }

    public async Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        var candidates = await GetProviderCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return Array.Empty<Manga>();
        }

        var results = await Task.WhenAll(candidates.Select(provider =>
            SafeBrowseAsync(provider, filters, cancellationToken)));

        return RankMangaResults(results.SelectMany(r => r), query: string.Empty);
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        if (ProviderAggregationHelpers.TryExtractProviderSlug(manga.Id.Value, out var providerSlug))
        {
            if (_builtInProviders.TryGetValue(providerSlug, out var builtInProvider))
            {
                var strippedManga = StripProviderPrefix(manga, providerSlug);
                return await SafeGetChaptersAsync(builtInProvider.Catalog, builtInProvider.Name, providerSlug, strippedManga, cancellationToken);
            }

            var dynamicConfig = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (dynamicConfig is not null)
            {
                var dynamicCatalog = CreateDynamicCatalog(dynamicConfig);
                var strippedManga = StripProviderPrefix(manga, providerSlug);
                return await SafeGetChaptersAsync(dynamicCatalog, dynamicConfig.Name, providerSlug, strippedManga, cancellationToken);
            }
        }

        foreach (var provider in await GetProviderCandidatesAsync(cancellationToken))
        {
            var chapters = await SafeGetChaptersAsync(provider.Catalog, provider.Name, provider.Slug, manga, cancellationToken);
            if (chapters.Count > 0)
            {
                return chapters;
            }
        }

        return Array.Empty<Chapter>();
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        if (ProviderAggregationHelpers.TryExtractProviderSlug(chapter.Id.Value, out var providerSlug))
        {
            if (_builtInProviders.TryGetValue(providerSlug, out var builtInProvider))
            {
                var strippedChapter = StripProviderPrefix(chapter, providerSlug);
                return await SafeGetPagesAsync(builtInProvider.Catalog, builtInProvider.Name, builtInProvider.Slug, strippedChapter, cancellationToken);
            }

            var dynamicConfig = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (dynamicConfig is not null)
            {
                var dynamicCatalog = CreateDynamicCatalog(dynamicConfig);
                var strippedChapter = StripProviderPrefix(chapter, providerSlug);
                return await SafeGetPagesAsync(dynamicCatalog, dynamicConfig.Name, providerSlug, strippedChapter, cancellationToken);
            }
        }

        foreach (var provider in await GetProviderCandidatesAsync(cancellationToken))
        {
            var pages = await SafeGetPagesAsync(provider.Catalog, provider.Name, provider.Slug, chapter, cancellationToken);
            if (pages.Count > 0)
            {
                return pages;
            }
        }

        return Array.Empty<ChapterPage>();
    }

    private async Task<IReadOnlyList<MangaProviderCandidate>> GetProviderCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<MangaProviderCandidate>();
        var rank = 0;

        foreach (var provider in _orderedBuiltInProviders)
        {
            candidates.Add(new MangaProviderCandidate(provider.Slug, provider.Name, provider.Catalog, rank++));
        }

        var dynamicConfigs = await GetDynamicProviderConfigsAsync(cancellationToken);
        foreach (var config in dynamicConfigs)
        {
            candidates.Add(new MangaProviderCandidate(
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
            if (provider.Type is not (ProviderType.Manga or ProviderType.Both))
            {
                continue;
            }

            var config = await _providerStore.GetAsync(provider.Slug, cancellationToken);
            if (config is not null && config.Type is (ProviderType.Manga or ProviderType.Both))
            {
                bySlug[ProviderAggregationHelpers.NormalizeSlug(config.Slug)] = (config, provider.IsActive);
            }
        }

        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeConfig is not null && activeConfig.Type is (ProviderType.Manga or ProviderType.Both))
        {
            bySlug[ProviderAggregationHelpers.NormalizeSlug(activeConfig.Slug)] = (activeConfig, true);
        }

        return bySlug.Values
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Config.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Config)
            .ToArray();
    }

    private DynamicMangaCatalog CreateDynamicCatalog(DynamicProviderConfig config)
    {
        return new DynamicMangaCatalog(
            config,
            _httpClient,
            _transforms,
            _loggerFactory.CreateLogger<DynamicMangaCatalog>());
    }

    private async Task<IReadOnlyCollection<ProviderResult<Manga>>> SafeSearchAsync(
        MangaProviderCandidate provider,
        string query,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await provider.Catalog.SearchAsync(query, filters, cancellationToken);
            return PrefixMangaIds(results, provider.Slug)
                .Select((manga, index) => new ProviderResult<Manga>(manga, provider.Rank, index))
                .ToArray();
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Search blocked for provider {Provider}: {Kind} - {Message}",
                provider.Name,
                ex.Kind,
                ex.Message);
            return Array.Empty<ProviderResult<Manga>>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Search failed for provider {Provider}, continuing with other providers", provider.Name);
            return Array.Empty<ProviderResult<Manga>>();
        }
    }

    private async Task<IReadOnlyCollection<ProviderResult<Manga>>> SafeBrowseAsync(
        MangaProviderCandidate provider,
        SearchFilters? filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await provider.Catalog.BrowsePopularAsync(filters, cancellationToken);
            return PrefixMangaIds(results, provider.Slug)
                .Select((manga, index) => new ProviderResult<Manga>(manga, provider.Rank, index))
                .ToArray();
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Browse blocked for provider {Provider}: {Kind} - {Message}",
                provider.Name,
                ex.Kind,
                ex.Message);
            return Array.Empty<ProviderResult<Manga>>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Browse failed for provider {Provider}, continuing with other providers", provider.Name);
            return Array.Empty<ProviderResult<Manga>>();
        }
    }

    private async Task<IReadOnlyCollection<Chapter>> SafeGetChaptersAsync(
        IMangaCatalog catalog,
        string providerName,
        string providerSlug,
        Manga manga,
        CancellationToken cancellationToken)
    {
        try
        {
            var chapters = await catalog.GetChaptersAsync(manga, cancellationToken);
            return PrefixChapterIds(chapters, providerSlug);
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Chapter fetch blocked for provider {Provider}: {Kind} - {Message}",
                providerName,
                ex.Kind,
                ex.Message);
            return Array.Empty<Chapter>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chapter fetch failed for provider {Provider}", providerName);
            return Array.Empty<Chapter>();
        }
    }

    private async Task<IReadOnlyCollection<ChapterPage>> SafeGetPagesAsync(
        IMangaCatalog catalog,
        string providerName,
        string providerSlug,
        Chapter chapter,
        CancellationToken cancellationToken)
    {
        try
        {
            var pages = await catalog.GetPagesAsync(chapter, cancellationToken);
            return TagPages(pages, providerSlug);
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Page fetch blocked for provider {Provider}: {Kind} - {Message}",
                providerName,
                ex.Kind,
                ex.Message);
            return Array.Empty<ChapterPage>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Page fetch failed for provider {Provider}", providerName);
            return Array.Empty<ChapterPage>();
        }
    }

    private static IReadOnlyCollection<Manga> RankMangaResults(IEnumerable<ProviderResult<Manga>> results, string query)
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

    private static IReadOnlyCollection<Manga> PrefixMangaIds(IReadOnlyCollection<Manga> results, string providerSlug)
    {
        return results.Select(manga => new Manga(
            new MangaId(ProviderAggregationHelpers.PrefixId(providerSlug, manga.Id.Value)),
            manga.Title,
            manga.Synopsis,
            manga.CoverImage,
            manga.DetailPage,
            PrefixChapterIds(manga.Chapters, providerSlug))).ToArray();
    }

    private static IReadOnlyCollection<Chapter> PrefixChapterIds(IReadOnlyCollection<Chapter> chapters, string providerSlug)
    {
        return chapters.Select(chapter => new Chapter(
            new ChapterId(ProviderAggregationHelpers.PrefixId(providerSlug, chapter.Id.Value)),
            chapter.Title,
            chapter.Number,
            chapter.PageUrl)).ToArray();
    }

    private static Manga StripProviderPrefix(Manga manga, string providerSlug)
    {
        return new Manga(
            new MangaId(ProviderAggregationHelpers.RemovePrefix(providerSlug, manga.Id.Value)),
            manga.Title,
            manga.Synopsis,
            manga.CoverImage,
            manga.DetailPage,
            manga.Chapters.Select(ch => StripProviderPrefix(ch, providerSlug)).ToArray());
    }

    private static Chapter StripProviderPrefix(Chapter chapter, string providerSlug)
    {
        return new Chapter(
            new ChapterId(ProviderAggregationHelpers.RemovePrefix(providerSlug, chapter.Id.Value)),
            chapter.Title,
            chapter.Number,
            chapter.PageUrl);
    }

    private static IReadOnlyCollection<ChapterPage> TagPages(IReadOnlyCollection<ChapterPage> pages, string providerSlug)
    {
        _ = providerSlug;
        return pages;
    }

    public sealed record BuiltInMangaProvider(string Slug, string Name, IMangaCatalog Catalog);

    private sealed record MangaProviderCandidate(string Slug, string Name, IMangaCatalog Catalog, int Rank);

    private sealed record ProviderResult<T>(T Item, int ProviderRank, int ResultIndex);
}
