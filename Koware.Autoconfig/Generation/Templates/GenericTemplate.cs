// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation.Templates;

/// <summary>
/// Generic fallback template for sites that don't match specific patterns.
/// </summary>
public sealed class GenericTemplate : IProviderTemplate
{
    public string Id => "generic";
    public string Name => "Generic";
    public string Description => "Fallback template for sites without a specific match";
    public ProviderType SupportedTypes => ProviderType.Both;

    public int CalculateMatchScore(SiteProfile profile, ContentSchema schema)
    {
        // Always return a low score - this is the fallback
        return 10;
    }

    public DynamicProviderConfig Apply(SiteProfile profile, ContentSchema schema, string providerName)
    {
        var slug = GenerateSlug(providerName);
        var providerType = profile.Category switch
        {
            ContentCategory.Anime => ProviderType.Anime,
            ContentCategory.Manga => ProviderType.Manga,
            ContentCategory.Both => ProviderType.Both,
            _ => ProviderType.Anime
        };

        var method = profile.HasGraphQL ? SearchMethod.GraphQL : SearchMethod.REST;
        var endpoint = schema.Endpoints.FirstOrDefault()?.Url.PathAndQuery ?? "/api";

        return new DynamicProviderConfig
        {
            Name = providerName,
            Slug = slug,
            Type = providerType,
            Hosts = new HostConfig
            {
                BaseHost = profile.BaseUrl.Host,
                ApiBase = $"{profile.BaseUrl.Scheme}://{profile.BaseUrl.Host}",
                Referer = profile.BaseUrl.ToString(),
                CustomHeaders = profile.RequiredHeaders.ToDictionary(k => k.Key, v => v.Value)
            },
            Search = new SearchConfig
            {
                Method = method,
                Endpoint = endpoint,
                QueryTemplate = schema.SearchPattern?.QueryTemplate ?? "?q=${query}",
                ResultMapping = schema.SearchPattern?.FieldMappings.ToList() ?? GetDefaultMappings(),
                PageSize = 20
            },
            Content = new ContentConfig
            {
                Episodes = providerType != ProviderType.Manga ? new EndpointConfig
                {
                    Method = method,
                    Endpoint = endpoint,
                    QueryTemplate = schema.EpisodePattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = []
                } : null,
                Chapters = providerType != ProviderType.Anime ? new EndpointConfig
                {
                    Method = method,
                    Endpoint = endpoint,
                    QueryTemplate = schema.ChapterPattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = []
                } : null
            },
            Media = new MediaConfig
            {
                Streams = providerType != ProviderType.Manga ? new StreamConfig
                {
                    Method = method,
                    Endpoint = endpoint,
                    QueryTemplate = schema.MediaPattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = []
                } : null,
                Pages = providerType != ProviderType.Anime ? new PageConfig
                {
                    Method = method,
                    Endpoint = endpoint,
                    QueryTemplate = schema.MediaPattern?.QueryTemplate ?? "/${id}",
                    ResultMapping = []
                } : null
            },
            Transforms = [],
            Notes = "This is a generic configuration that may require manual adjustment."
        };
    }

    private static string GenerateSlug(string name) =>
        name.ToLowerInvariant().Replace(" ", "-").Replace(".", "").Replace("_", "-");

    private static List<FieldMapping> GetDefaultMappings() =>
    [
        new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" },
        new FieldMapping { SourcePath = "$.name", TargetField = "Title" }
    ];
}
