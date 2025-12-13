// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation.Templates;

/// <summary>
/// Template for GraphQL-based anime sites (AllAnime-style).
/// </summary>
public sealed class GraphQLAnimeTemplate : IProviderTemplate
{
    public string Id => "graphql-anime";
    public string Name => "GraphQL Anime";
    public string Description => "GraphQL-based anime streaming sites with shows/episodes structure";
    public ProviderType SupportedTypes => ProviderType.Anime;

    public int CalculateMatchScore(SiteProfile profile, ContentSchema schema)
    {
        var score = 0;

        // Must have GraphQL
        if (!profile.HasGraphQL)
            return 0;

        // Anime content
        if (profile.Category == ContentCategory.Anime || profile.Category == ContentCategory.Both)
            score += 30;

        // Has GraphQL endpoints
        var hasGraphQLEndpoint = schema.Endpoints.Any(e => e.Type == ApiType.GraphQL);
        if (hasGraphQLEndpoint)
            score += 30;

        // Has search pattern
        if (schema.SearchPattern?.Method == SearchMethod.GraphQL)
            score += 20;

        // Has episode pattern
        if (schema.EpisodePattern != null)
            score += 20;

        return score;
    }

    public DynamicProviderConfig Apply(SiteProfile profile, ContentSchema schema, string providerName)
    {
        var slug = GenerateSlug(providerName);
        var graphqlEndpoint = schema.Endpoints
            .FirstOrDefault(e => e.Type == ApiType.GraphQL)?.Url.PathAndQuery ?? "/api";

        return new DynamicProviderConfig
        {
            Name = providerName,
            Slug = slug,
            Type = ProviderType.Anime,
            Hosts = new HostConfig
            {
                BaseHost = profile.BaseUrl.Host,
                ApiBase = $"{profile.BaseUrl.Scheme}://{profile.BaseUrl.Host}",
                Referer = profile.BaseUrl.ToString(),
                CustomHeaders = profile.RequiredHeaders.ToDictionary(k => k.Key, v => v.Value)
            },
            Search = new SearchConfig
            {
                Method = SearchMethod.GraphQL,
                Endpoint = graphqlEndpoint,
                QueryTemplate = schema.SearchPattern?.QueryTemplate ?? 
                    "query($search: SearchInput, $limit: Int) { shows(search: $search, limit: $limit) { edges { _id name thumbnail } } }",
                ResultMapping = schema.SearchPattern?.FieldMappings.ToList() ?? GetDefaultSearchMappings(),
                PageSize = 20
            },
            Content = new ContentConfig
            {
                Episodes = new EndpointConfig
                {
                    Method = SearchMethod.GraphQL,
                    Endpoint = graphqlEndpoint,
                    QueryTemplate = schema.EpisodePattern?.QueryTemplate ??
                        "query($showId: String!) { show(_id: $showId) { _id availableEpisodesDetail } }",
                    ResultMapping = schema.EpisodePattern?.FieldMappings.ToList() ?? GetDefaultEpisodeMappings()
                }
            },
            Media = new MediaConfig
            {
                Streams = new StreamConfig
                {
                    Method = SearchMethod.GraphQL,
                    Endpoint = graphqlEndpoint,
                    QueryTemplate = schema.MediaPattern?.QueryTemplate ??
                        "query($showId: String!, $translationType: VaildTranslationTypeEnumType!, $episodeString: String!) { episode(showId: $showId, translationType: $translationType, episodeString: $episodeString) { sourceUrls } }",
                    ResultMapping = schema.MediaPattern?.FieldMappings.ToList() ?? GetDefaultStreamMappings(),
                    CustomDecoder = schema.MediaPattern?.CustomDecoder
                }
            },
            Transforms = schema.MediaPattern?.CustomDecoder != null
                ? [new TransformRule { Name = schema.MediaPattern.CustomDecoder, Type = TransformType.Custom }]
                : []
        };
    }

    private static string GenerateSlug(string name) =>
        name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace(".", "")
            .Replace("_", "-");

    private static List<FieldMapping> GetDefaultSearchMappings() =>
    [
        new FieldMapping { SourcePath = "$._id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.name", TargetField = "Title" },
        new FieldMapping { SourcePath = "$.thumbnail", TargetField = "CoverImage" },
        new FieldMapping { SourcePath = "$.description", TargetField = "Synopsis" }
    ];

    private static List<FieldMapping> GetDefaultEpisodeMappings() =>
    [
        new FieldMapping { SourcePath = "$", TargetField = "Number" }
    ];

    private static List<FieldMapping> GetDefaultStreamMappings() =>
    [
        new FieldMapping { SourcePath = "$.sourceUrl", TargetField = "Url" },
        new FieldMapping { SourcePath = "$.sourceName", TargetField = "Provider" }
    ];
}
