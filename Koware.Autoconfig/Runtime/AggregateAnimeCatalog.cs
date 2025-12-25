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
    private readonly IAnimeCatalog _builtInCatalog;
    private readonly IProviderStore _providerStore;
    private readonly ITransformEngine _transforms;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AggregateAnimeCatalog> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public AggregateAnimeCatalog(
        IAnimeCatalog builtInCatalog,
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

        // Search built-in catalog
        tasks.Add(SafeSearchAsync(_builtInCatalog, "AllAnime", query, filters, cancellationToken));

        // Get active dynamic anime provider
        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeConfig != null && activeConfig.Type is ProviderType.Anime or ProviderType.Both)
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

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        var allResults = new List<Anime>();
        var tasks = new List<Task<IReadOnlyCollection<Anime>>>();

        // Browse built-in catalog
        tasks.Add(SafeBrowseAsync(_builtInCatalog, "AllAnime", filters, cancellationToken));

        // Get active dynamic anime provider
        var activeConfig = await _providerStore.GetActiveAsync(ProviderType.Anime, cancellationToken);
        if (activeConfig != null && activeConfig.Type is ProviderType.Anime or ProviderType.Both)
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

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        // Determine which provider to use based on anime ID prefix
        var providerSlug = ExtractProviderSlug(anime.Id.Value);
        
        if (!string.IsNullOrEmpty(providerSlug))
        {
            var config = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (config != null)
            {
                var dynamicCatalog = CreateDynamicCatalog(config);
                return await dynamicCatalog.GetEpisodesAsync(anime, cancellationToken);
            }
        }

        // Fall back to built-in catalog
        return await _builtInCatalog.GetEpisodesAsync(anime, cancellationToken);
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        // Determine which provider to use based on episode ID prefix
        var providerSlug = ExtractProviderSlug(episode.Id.Value);
        
        if (!string.IsNullOrEmpty(providerSlug))
        {
            var config = await _providerStore.GetAsync(providerSlug, cancellationToken);
            if (config != null)
            {
                var dynamicCatalog = CreateDynamicCatalog(config);
                return await dynamicCatalog.GetStreamsAsync(episode, cancellationToken);
            }
        }

        // Fall back to built-in catalog
        return await _builtInCatalog.GetStreamsAsync(episode, cancellationToken);
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
        string query,
        SearchFilters filters,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await catalog.SearchAsync(query, filters, cancellationToken);
            
            // Prefix anime IDs with provider slug for routing
            if (catalog is DynamicAnimeCatalog dynamicCatalog)
            {
                return results.Select(a => new Anime(
                    new AnimeId($"{dynamicCatalog.ProviderSlug}:{a.Id.Value}"),
                    a.Title,
                    a.Synopsis,
                    a.CoverImage,
                    a.DetailPage,
                    a.Episodes
                )).ToList();
            }
            
            return results;
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
            return Array.Empty<Anime>();
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
