// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation.Templates;

/// <summary>
/// Template for REST API-based anime sites (Consumet-style).
/// </summary>
public sealed class RestAnimeTemplate : IProviderTemplate
{
    public string Id => "rest-anime";
    public string Name => "REST Anime";
    public string Description => "REST API-based anime streaming sites with standard endpoints";
    public ProviderType SupportedTypes => ProviderType.Anime;

    public int CalculateMatchScore(SiteProfile profile, ContentSchema schema)
    {
        var score = 0;

        // Should not have GraphQL (or prefer REST)
        if (profile.HasGraphQL)
            score -= 20;

        // Anime content
        if (profile.Category == ContentCategory.Anime || profile.Category == ContentCategory.Both)
            score += 30;

        // Has REST endpoints
        var hasRestEndpoint = schema.Endpoints.Any(e => e.Type == ApiType.REST);
        if (hasRestEndpoint)
            score += 40;

        // Has search pattern using REST
        if (schema.SearchPattern?.Method == SearchMethod.REST)
            score += 20;

        // API paths detected
        if (profile.DetectedApiEndpoints.Any(e => e.Contains("/api/")))
            score += 10;

        return Math.Max(0, score);
    }

    public DynamicProviderConfig Apply(SiteProfile profile, ContentSchema schema, string providerName)
    {
        var slug = GenerateSlug(providerName);
        var apiBase = DetermineApiBase(profile, schema);

        return new DynamicProviderConfig
        {
            Name = providerName,
            Slug = slug,
            Type = ProviderType.Anime,
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
                Endpoint = "/search",
                QueryTemplate = schema.SearchPattern?.QueryTemplate ?? "?q=${query}",
                ResultMapping = schema.SearchPattern?.FieldMappings.ToList() ?? GetDefaultSearchMappings(),
                PageSize = 20
            },
            Content = new ContentConfig
            {
                Episodes = new EndpointConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = "/info",
                    QueryTemplate = schema.EpisodePattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = schema.EpisodePattern?.FieldMappings.ToList() ?? GetDefaultEpisodeMappings()
                }
            },
            Media = new MediaConfig
            {
                Streams = new StreamConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = "/watch",
                    QueryTemplate = schema.MediaPattern?.QueryTemplate ?? "/${episodeId}",
                    ResultMapping = schema.MediaPattern?.FieldMappings.ToList() ?? GetDefaultStreamMappings()
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

    private static List<FieldMapping> GetDefaultSearchMappings() =>
    [
        new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" },
        new FieldMapping { SourcePath = "$.image", TargetField = "CoverImage" },
        new FieldMapping { SourcePath = "$.description", TargetField = "Synopsis" }
    ];

    private static List<FieldMapping> GetDefaultEpisodeMappings() =>
    [
        new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.number", TargetField = "Number" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" }
    ];

    private static List<FieldMapping> GetDefaultStreamMappings() =>
    [
        new FieldMapping { SourcePath = "$.url", TargetField = "Url" },
        new FieldMapping { SourcePath = "$.quality", TargetField = "Quality" }
    ];
}
