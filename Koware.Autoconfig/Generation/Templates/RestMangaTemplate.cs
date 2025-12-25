// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation.Templates;

/// <summary>
/// Template for REST API-based manga sites.
/// </summary>
public sealed class RestMangaTemplate : IProviderTemplate
{
    public string Id => "rest-manga";
    public string Name => "REST Manga";
    public string Description => "REST API-based manga reading sites with standard endpoints";
    public ProviderType SupportedTypes => ProviderType.Manga;

    public int CalculateMatchScore(SiteProfile profile, ContentSchema schema)
    {
        var score = 0;

        if (profile.HasGraphQL)
            score -= 20;

        if (profile.Category == ContentCategory.Manga || profile.Category == ContentCategory.Both)
            score += 30;

        var hasRestEndpoint = schema.Endpoints.Any(e => e.Type == ApiType.REST);
        if (hasRestEndpoint)
            score += 40;

        if (schema.SearchPattern?.Method == SearchMethod.REST)
            score += 20;

        if (profile.DetectedApiEndpoints.Any(e => e.Contains("/api/")))
            score += 10;

        return Math.Max(0, score);
    }

    public DynamicProviderConfig Apply(SiteProfile profile, ContentSchema schema, string providerName)
    {
        var slug = GenerateSlug(providerName);
        var apiBase = DetermineApiBase(profile, schema);
        var knownSite = profile.KnownSiteInfo;
        
        // Get search endpoint from discovered endpoints
        var searchEndpoint = schema.Endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Search);
        var detailEndpoint = schema.Endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Details || e.Purpose == EndpointPurpose.Chapters);
        var pagesEndpoint = schema.Endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Pages);
        
        // Use discovered mappings, then known site mappings, then defaults
        var searchMappings = GetSearchMappings(searchEndpoint, schema, knownSite);
            
        // Use known site search endpoint if available and nothing was discovered
        var searchPath = DetermineSearchPath(searchEndpoint, schema, knownSite);
        var searchQuery = DetermineSearchQuery(searchEndpoint, schema, knownSite);
        var resultsPath = searchEndpoint?.ResultsPath ?? schema.SearchPattern?.ResultsPath ?? GetKnownSiteResultsPath(knownSite);

        return new DynamicProviderConfig
        {
            Name = providerName,
            Slug = slug,
            Type = ProviderType.Manga,
            Hosts = new HostConfig
            {
                BaseHost = profile.BaseUrl.Host,
                ApiBase = apiBase,
                Referer = profile.BaseUrl.ToString(),
                CustomHeaders = profile.RequiredHeaders.ToDictionary(k => k.Key, v => v.Value)
            },
            Search = new SearchConfig
            {
                Method = SearchMethod.REST,
                Endpoint = searchPath,
                QueryTemplate = searchQuery,
                ResultMapping = searchMappings,
                ResultsPath = resultsPath,
                PageSize = 20
            },
            Content = new ContentConfig
            {
                Chapters = new EndpointConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = detailEndpoint?.Url.AbsolutePath ?? "/info",
                    QueryTemplate = detailEndpoint?.SampleQuery ?? schema.ChapterPattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = detailEndpoint?.FieldMappings?.Count > 0
                        ? detailEndpoint.FieldMappings
                        : schema.ChapterPattern?.FieldMappings.ToList() ?? GetDefaultChapterMappings()
                }
            },
            Media = new MediaConfig
            {
                Pages = new PageConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = pagesEndpoint?.Url.AbsolutePath ?? "/read",
                    QueryTemplate = pagesEndpoint?.SampleQuery ?? schema.MediaPattern?.QueryTemplate ?? "/${chapterId}",
                    ResultMapping = pagesEndpoint?.FieldMappings?.Count > 0
                        ? pagesEndpoint.FieldMappings
                        : schema.MediaPattern?.FieldMappings.ToList() ?? GetDefaultPageMappings()
                }
            },
            Transforms = []
        };
    }

    private static string DetermineApiBase(SiteProfile profile, ContentSchema schema)
    {
        var restEndpoint = schema.Endpoints.FirstOrDefault(e => e.Type == ApiType.REST);
        if (restEndpoint != null)
        {
            var uri = restEndpoint.Url;
            return $"{uri.Scheme}://{uri.Host}";
        }

        return $"{profile.BaseUrl.Scheme}://{profile.BaseUrl.Host}";
    }

    private static string GenerateSlug(string name) =>
        name.ToLowerInvariant().Replace(" ", "-").Replace(".", "").Replace("_", "-");
    
    private static List<FieldMapping> GetSearchMappings(ApiEndpoint? searchEndpoint, ContentSchema schema, SiteKnowledge? knownSite)
    {
        // First try discovered mappings
        if (searchEndpoint?.FieldMappings?.Count > 0)
            return searchEndpoint.FieldMappings;
        
        if (schema.SearchPattern?.FieldMappings?.Count > 0)
            return schema.SearchPattern.FieldMappings.ToList();
        
        // Then try known site mappings
        if (knownSite?.FieldMappings != null)
        {
            return knownSite.FieldMappings
                .Select(kv => new FieldMapping { SourcePath = kv.Value, TargetField = kv.Key })
                .ToList();
        }
        
        return GetDefaultSearchMappings();
    }
    
    private static string DetermineSearchPath(ApiEndpoint? searchEndpoint, ContentSchema schema, SiteKnowledge? knownSite)
    {
        // Prefer known site pattern if available (more reliable than generic discovery)
        if (!string.IsNullOrEmpty(knownSite?.SearchEndpoint))
            return knownSite.SearchEndpoint.Split('?')[0];
        
        // Fall back to discovered endpoint if it has valid mappings
        if (searchEndpoint?.Url != null && searchEndpoint.FieldMappings?.Count > 0)
            return searchEndpoint.Url.AbsolutePath;
        
        if (searchEndpoint?.Url != null)
            return searchEndpoint.Url.AbsolutePath;
        
        return "/search";
    }
    
    private static string DetermineSearchQuery(ApiEndpoint? searchEndpoint, ContentSchema schema, SiteKnowledge? knownSite)
    {
        // Prefer known site pattern if available
        if (!string.IsNullOrEmpty(knownSite?.SearchEndpoint) && knownSite.SearchEndpoint.Contains('?'))
        {
            var queryPart = knownSite.SearchEndpoint.Split('?')[1];
            return "?" + queryPart.Replace("{query}", "${query}");
        }
        
        if (!string.IsNullOrEmpty(searchEndpoint?.SampleQuery))
            return searchEndpoint.SampleQuery;
        
        if (!string.IsNullOrEmpty(schema.SearchPattern?.QueryTemplate))
            return schema.SearchPattern.QueryTemplate;
        
        return "?q=${query}";
    }
    
    private static string? GetKnownSiteResultsPath(SiteKnowledge? knownSite)
    {
        // Return common results paths for known sites
        return knownSite?.Category switch
        {
            ContentCategory.Manga => "$.result",
            _ => null
        };
    }

    private static List<FieldMapping> GetDefaultSearchMappings() =>
    [
        new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" },
        new FieldMapping { SourcePath = "$.image", TargetField = "CoverImage" },
        new FieldMapping { SourcePath = "$.description", TargetField = "Synopsis" }
    ];

    private static List<FieldMapping> GetDefaultChapterMappings() =>
    [
        new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.number", TargetField = "Number" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" }
    ];

    private static List<FieldMapping> GetDefaultPageMappings() =>
    [
        new FieldMapping { SourcePath = "$.img", TargetField = "Url" },
        new FieldMapping { SourcePath = "$.page", TargetField = "Number" }
    ];
}
