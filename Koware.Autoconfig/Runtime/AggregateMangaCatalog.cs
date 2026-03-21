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
        var allResults = new List<Manga>();
        var tasks = new List<Task<IReadOnlyCollection<Manga>>>();

        foreach (var provider in _orderedBuiltInProviders)
        {
            tasks.Add(SafeSearchAsync(provider.Catalog, provider.Name, provider.Slug, query, filters, cancellationToken));
        }

        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeConfig is not null && activeConfig.Type is ProviderType.Manga or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeConfig);
            tasks.Add(SafeSearchAsync(dynamicCatalog, activeConfig.Name, activeConfig.Slug, query, filters, cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return Array.Empty<Manga>();
        }

        var results = await Task.WhenAll(tasks);
        foreach (var result in results)
        {
            allResults.AddRange(result);
        }

        return allResults;
    }

    public async Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        var allResults = new List<Manga>();
        var tasks = new List<Task<IReadOnlyCollection<Manga>>>();

        foreach (var provider in _orderedBuiltInProviders)
        {
            tasks.Add(SafeBrowseAsync(provider.Catalog, provider.Name, provider.Slug, filters, cancellationToken));
        }

        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeConfig is not null && activeConfig.Type is ProviderType.Manga or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeConfig);
            tasks.Add(SafeBrowseAsync(dynamicCatalog, activeConfig.Name, activeConfig.Slug, filters, cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return Array.Empty<Manga>();
        }

        var results = await Task.WhenAll(tasks);
        foreach (var result in results)
        {
            allResults.AddRange(result);
        }

        return allResults;
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        if (TryExtractProviderSlug(manga.Id.Value, out var providerSlug))
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

        var fallbackProvider = GetFallbackBuiltInProvider();
        if (fallbackProvider is not null)
        {
            return await SafeGetChaptersAsync(fallbackProvider.Catalog, fallbackProvider.Name, fallbackProvider.Slug, manga, cancellationToken);
        }

        var activeDynamic = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeDynamic is not null && activeDynamic.Type is ProviderType.Manga or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeDynamic);
            return await SafeGetChaptersAsync(dynamicCatalog, activeDynamic.Name, activeDynamic.Slug, manga, cancellationToken);
        }

        return Array.Empty<Chapter>();
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        if (TryExtractProviderSlug(chapter.Id.Value, out var providerSlug))
        {
            if (_builtInProviders.TryGetValue(providerSlug, out var builtInProvider))
            {
                var strippedChapter = StripProviderPrefix(chapter, providerSlug);
                return await SafeGetPagesAsync(builtInProvider.Catalog, builtInProvider.Name, strippedChapter, cancellationToken);
            }

            var dynamicConfig = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (dynamicConfig is not null)
            {
                var dynamicCatalog = CreateDynamicCatalog(dynamicConfig);
                var strippedChapter = StripProviderPrefix(chapter, providerSlug);
                return await SafeGetPagesAsync(dynamicCatalog, dynamicConfig.Name, strippedChapter, cancellationToken);
            }
        }

        var fallbackProvider = GetFallbackBuiltInProvider();
        if (fallbackProvider is not null)
        {
            return await SafeGetPagesAsync(fallbackProvider.Catalog, fallbackProvider.Name, chapter, cancellationToken);
        }

        var activeDynamic = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeDynamic is not null && activeDynamic.Type is ProviderType.Manga or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeDynamic);
            return await SafeGetPagesAsync(dynamicCatalog, activeDynamic.Name, chapter, cancellationToken);
        }

        return Array.Empty<ChapterPage>();
    }

    private BuiltInMangaProvider? GetFallbackBuiltInProvider()
    {
        if (_builtInProviders.TryGetValue("allmanga", out var allManga))
        {
            return allManga;
        }

        return _orderedBuiltInProviders.FirstOrDefault();
    }

    private DynamicMangaCatalog CreateDynamicCatalog(DynamicProviderConfig config)
    {
        return new DynamicMangaCatalog(
            config,
            _httpClient,
            _transforms,
            _loggerFactory.CreateLogger<DynamicMangaCatalog>());
    }

    private async Task<IReadOnlyCollection<Manga>> SafeSearchAsync(
        IMangaCatalog catalog,
        string providerName,
        string providerSlug,
        string query,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await catalog.SearchAsync(query, filters, cancellationToken);
            return PrefixMangaIds(results, providerSlug);
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Search blocked for provider {Provider}: {Kind} - {Message}",
                providerName,
                ex.Kind,
                ex.Message);
            return Array.Empty<Manga>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search failed for provider {Provider}, continuing with other providers", providerName);
            return Array.Empty<Manga>();
        }
    }

    private async Task<IReadOnlyCollection<Manga>> SafeBrowseAsync(
        IMangaCatalog catalog,
        string providerName,
        string providerSlug,
        SearchFilters? filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await catalog.BrowsePopularAsync(filters, cancellationToken);
            return PrefixMangaIds(results, providerSlug);
        }
        catch (DynamicProviderRuntimeException ex)
        {
            _logger.LogWarning(
                "Browse blocked for provider {Provider}: {Kind} - {Message}",
                providerName,
                ex.Kind,
                ex.Message);
            return Array.Empty<Manga>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browse failed for provider {Provider}, continuing with other providers", providerName);
            return Array.Empty<Manga>();
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chapter fetch failed for provider {Provider}", providerName);
            return Array.Empty<Chapter>();
        }
    }

    private async Task<IReadOnlyCollection<ChapterPage>> SafeGetPagesAsync(
        IMangaCatalog catalog,
        string providerName,
        Chapter chapter,
        CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.GetPagesAsync(chapter, cancellationToken);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Page fetch failed for provider {Provider}", providerName);
            return Array.Empty<ChapterPage>();
        }
    }

    private static IReadOnlyCollection<Manga> PrefixMangaIds(IReadOnlyCollection<Manga> results, string providerSlug)
    {
        return results.Select(manga => new Manga(
            new MangaId(PrefixId(providerSlug, manga.Id.Value)),
            manga.Title,
            manga.Synopsis,
            manga.CoverImage,
            manga.DetailPage,
            PrefixChapterIds(manga.Chapters, providerSlug))).ToArray();
    }

    private static IReadOnlyCollection<Chapter> PrefixChapterIds(IReadOnlyCollection<Chapter> chapters, string providerSlug)
    {
        return chapters.Select(chapter => new Chapter(
            new ChapterId(PrefixId(providerSlug, chapter.Id.Value)),
            chapter.Title,
            chapter.Number,
            chapter.PageUrl)).ToArray();
    }

    private static Manga StripProviderPrefix(Manga manga, string providerSlug)
    {
        return new Manga(
            new MangaId(RemovePrefix(providerSlug, manga.Id.Value)),
            manga.Title,
            manga.Synopsis,
            manga.CoverImage,
            manga.DetailPage,
            manga.Chapters.Select(ch => StripProviderPrefix(ch, providerSlug)).ToArray());
    }

    private static Chapter StripProviderPrefix(Chapter chapter, string providerSlug)
    {
        return new Chapter(
            new ChapterId(RemovePrefix(providerSlug, chapter.Id.Value)),
            chapter.Title,
            chapter.Number,
            chapter.PageUrl);
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

    public sealed record BuiltInMangaProvider(string Slug, string Name, IMangaCatalog Catalog);
}
