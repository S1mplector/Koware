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
                Endpoint = "/search",
                QueryTemplate = schema.SearchPattern?.QueryTemplate ?? "?q=${query}",
                ResultMapping = schema.SearchPattern?.FieldMappings.ToList() ?? GetDefaultSearchMappings(),
                PageSize = 20
            },
            Content = new ContentConfig
            {
                Chapters = new EndpointConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = "/info",
                    QueryTemplate = schema.ChapterPattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = schema.ChapterPattern?.FieldMappings.ToList() ?? GetDefaultChapterMappings()
                }
            },
            Media = new MediaConfig
            {
                Pages = new PageConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = "/read",
                    QueryTemplate = schema.MediaPattern?.QueryTemplate ?? "/${chapterId}",
                    ResultMapping = schema.MediaPattern?.FieldMappings.ToList() ?? GetDefaultPageMappings()
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
