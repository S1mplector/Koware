// Author: Ilgaz Mehmetoğlu
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Storage;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Aggregating IMangaCatalog that combines built-in and dynamic providers.
/// Searches across all active providers and returns combined results.
/// </summary>
public sealed class AggregateMangaCatalog : IMangaCatalog
{
    private readonly IMangaCatalog _builtInCatalog;
    private readonly IProviderStore _providerStore;
    private readonly ITransformEngine _transforms;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AggregateMangaCatalog> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public AggregateMangaCatalog(
        IMangaCatalog builtInCatalog,
        IProviderStore providerStore,
        ITransformEngine transforms,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        _builtInCatalog = builtInCatalog;
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

        // Search built-in catalog
        tasks.Add(SafeSearchAsync(_builtInCatalog, "AllManga", query, filters, cancellationToken));

        // Get active dynamic manga provider
        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeConfig != null && activeConfig.Type is ProviderType.Manga or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeConfig);
            tasks.Add(SafeSearchAsync(dynamicCatalog, activeConfig.Name, query, filters, cancellationToken));
        }

        // Wait for all searches to complete
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

        // Browse built-in catalog
        tasks.Add(SafeBrowseAsync(_builtInCatalog, "AllManga", filters, cancellationToken));

        // Get active dynamic manga provider
        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Manga, cancellationToken);
        if (activeConfig != null && activeConfig.Type is ProviderType.Manga or ProviderType.Both)
        {
            var dynamicCatalog = CreateDynamicCatalog(activeConfig);
            tasks.Add(SafeBrowseAsync(dynamicCatalog, activeConfig.Name, filters, cancellationToken));
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
        // Determine which provider to use based on manga ID prefix
        var providerSlug = ExtractProviderSlug(manga.Id.Value);
        
        if (!string.IsNullOrEmpty(providerSlug))
        {
            var config = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (config != null)
            {
                var dynamicCatalog = CreateDynamicCatalog(config);
                return await dynamicCatalog.GetChaptersAsync(manga, cancellationToken);
            }
        }

        // Fall back to built-in catalog
        return await _builtInCatalog.GetChaptersAsync(manga, cancellationToken);
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        // Determine which provider to use based on chapter ID prefix
        var providerSlug = ExtractProviderSlug(chapter.Id.Value);
        
        if (!string.IsNullOrEmpty(providerSlug))
        {
            var config = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (config != null)
            {
                var dynamicCatalog = CreateDynamicCatalog(config);
                return await dynamicCatalog.GetPagesAsync(chapter, cancellationToken);
            }
        }

        // Fall back to built-in catalog
        return await _builtInCatalog.GetPagesAsync(chapter, cancellationToken);
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
        string query,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await catalog.SearchAsync(query, filters, cancellationToken);
            
            // Prefix manga IDs with provider slug for routing
            if (catalog is DynamicMangaCatalog dynamicCatalog)
            {
                return results.Select(m => new Manga(
                    new MangaId($"{dynamicCatalog.ProviderSlug}:{m.Id.Value}"),
                    m.Title,
                    m.Synopsis,
                    m.CoverImage,
                    m.DetailPage,
                    m.Chapters
                )).ToList();
            }
            
            return results;
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
        SearchFilters? filters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.BrowsePopularAsync(filters, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browse failed for provider {Provider}, continuing with other providers", providerName);
            return Array.Empty<Manga>();
        }
    }

    private static string? ExtractProviderSlug(string id)
    {
        // Format: "provider-slug:actual-id"
        var colonIndex = id.IndexOf(':');
        if (colonIndex > 0)
        {
            var possibleSlug = id[..colonIndex];
            // Check if it looks like a provider slug (not a numeric ID)
            if (!possibleSlug.All(char.IsDigit))
            {
                return possibleSlug;
            }
        }
        return null;
    }
}
