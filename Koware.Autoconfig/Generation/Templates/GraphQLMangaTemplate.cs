// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation.Templates;

/// <summary>
/// Template for GraphQL-based manga sites (AllManga-style).
/// </summary>
public sealed class GraphQLMangaTemplate : IProviderTemplate
{
    public string Id => "graphql-manga";
    public string Name => "GraphQL Manga";
    public string Description => "GraphQL-based manga reading sites with mangas/chapters structure";
    public ProviderType SupportedTypes => ProviderType.Manga;

    public int CalculateMatchScore(SiteProfile profile, ContentSchema schema)
    {
        var score = 0;

        if (!profile.HasGraphQL)
            return 0;

        if (profile.Category == ContentCategory.Manga || profile.Category == ContentCategory.Both)
            score += 30;

        var hasGraphQLEndpoint = schema.Endpoints.Any(e => e.Type == ApiType.GraphQL);
        if (hasGraphQLEndpoint)
            score += 30;

        if (schema.SearchPattern?.Method == SearchMethod.GraphQL)
            score += 20;

        if (schema.ChapterPattern != null)
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
            Type = ProviderType.Manga,
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
                    "query($search: SearchInput, $limit: Int) { mangas(search: $search, limit: $limit) { edges { _id name englishName thumbnail } } }",
                ResultMapping = schema.SearchPattern?.FieldMappings.ToList() ?? GetDefaultSearchMappings(),
                PageSize = 20
            },
            Content = new ContentConfig
            {
                Chapters = new EndpointConfig
                {
                    Method = SearchMethod.GraphQL,
                    Endpoint = graphqlEndpoint,
                    QueryTemplate = schema.ChapterPattern?.QueryTemplate ??
                        "query($mangaId: String!) { manga(_id: $mangaId) { _id availableChaptersDetail } }",
                    ResultMapping = schema.ChapterPattern?.FieldMappings.ToList() ?? GetDefaultChapterMappings()
                }
            },
            Media = new MediaConfig
            {
                Pages = new PageConfig
                {
                    Method = SearchMethod.GraphQL,
                    Endpoint = graphqlEndpoint,
                    QueryTemplate = schema.MediaPattern?.QueryTemplate ??
                        "query($mangaId: String!, $translationType: VaildTranslationTypeMangaEnumType!, $chapterString: String!) { chapterPages(mangaId: $mangaId, translationType: $translationType, chapterString: $chapterString) { edges { pictureUrls pictureUrlHead } } }",
                    ResultMapping = schema.MediaPattern?.FieldMappings.ToList() ?? GetDefaultPageMappings()
                }
            },
            Transforms = []
        };
    }

    private static string GenerateSlug(string name) =>
        name.ToLowerInvariant().Replace(" ", "-").Replace(".", "").Replace("_", "-");

    private static List<FieldMapping> GetDefaultSearchMappings() =>
    [
        new FieldMapping { SourcePath = "$._id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.name", TargetField = "Title" },
        new FieldMapping { SourcePath = "$.thumbnail", TargetField = "CoverImage" },
        new FieldMapping { SourcePath = "$.description", TargetField = "Synopsis" }
    ];

    private static List<FieldMapping> GetDefaultChapterMappings() =>
    [
        new FieldMapping { SourcePath = "$", TargetField = "Number" }
    ];

    private static List<FieldMapping> GetDefaultPageMappings() =>
    [
        new FieldMapping { SourcePath = "$.url", TargetField = "Url" },
        new FieldMapping { SourcePath = "$.num", TargetField = "Number" }
    ];
}
