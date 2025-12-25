// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Analysis;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Koware.Tests.Autoconfig;

public sealed class GraphQLIntrospectorTests
{
    private readonly GraphQLIntrospector _introspector;
    private readonly StubHttpMessageHandler _httpHandler;

    public GraphQLIntrospectorTests()
    {
        _httpHandler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler);
        var logger = new LoggerFactory().CreateLogger<GraphQLIntrospector>();
        _introspector = new GraphQLIntrospector(httpClient, logger);
    }

    [Fact]
    public async Task IntrospectAsync_ParsesValidSchema()
    {
        // Arrange
        var introspectionResponse = """
        {
            "data": {
                "__schema": {
                    "queryType": { "name": "Query" },
                    "mutationType": null,
                    "types": [
                        {
                            "kind": "OBJECT",
                            "name": "Query",
                            "description": null,
                            "fields": [
                                {
                                    "name": "shows",
                                    "description": "Search shows",
                                    "args": [
                                        {
                                            "name": "search",
                                            "type": { "name": "SearchInput", "kind": "INPUT_OBJECT", "ofType": null },
                                            "defaultValue": null
                                        }
                                    ],
                                    "type": { "name": "ShowConnection", "kind": "OBJECT", "ofType": null }
                                },
                                {
                                    "name": "show",
                                    "description": "Get show by ID",
                                    "args": [
                                        {
                                            "name": "_id",
                                            "type": { "name": null, "kind": "NON_NULL", "ofType": { "name": "String", "kind": "SCALAR" } },
                                            "defaultValue": null
                                        }
                                    ],
                                    "type": { "name": "Show", "kind": "OBJECT", "ofType": null }
                                }
                            ],
                            "inputFields": null,
                            "enumValues": null
                        },
                        {
                            "kind": "OBJECT",
                            "name": "Show",
                            "description": "An anime show",
                            "fields": [
                                {
                                    "name": "_id",
                                    "description": null,
                                    "args": [],
                                    "type": { "name": "String", "kind": "SCALAR", "ofType": null }
                                },
                                {
                                    "name": "name",
                                    "description": null,
                                    "args": [],
                                    "type": { "name": "String", "kind": "SCALAR", "ofType": null }
                                },
                                {
                                    "name": "availableEpisodesDetail",
                                    "description": null,
                                    "args": [],
                                    "type": { "name": "EpisodeDetail", "kind": "OBJECT", "ofType": null }
                                }
                            ],
                            "inputFields": null,
                            "enumValues": null
                        }
                    ]
                }
            }
        }
        """;

        _httpHandler.SetResponse(introspectionResponse);

        var profile = CreateTestProfile();
        var endpoint = new Uri("https://api.example.com/graphql");

        // Act
        var schema = await _introspector.IntrospectAsync(endpoint, profile);

        // Assert
        Assert.NotNull(schema);
        Assert.Equal(endpoint, schema.Endpoint);
        Assert.True(schema.SupportsIntrospection);
        Assert.True(schema.Types.Count > 0);
        Assert.True(schema.Queries.Count > 0);
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsQueries()
    {
        // Arrange
        var introspectionResponse = """
        {
            "data": {
                "__schema": {
                    "queryType": { "name": "Query" },
                    "mutationType": null,
                    "types": [
                        {
                            "kind": "OBJECT",
                            "name": "Query",
                            "fields": [
                                {
                                    "name": "search",
                                    "args": [{ "name": "query", "type": { "name": "String", "kind": "SCALAR" } }],
                                    "type": { "name": "[Result]", "kind": "LIST" }
                                },
                                {
                                    "name": "episode",
                                    "args": [{ "name": "id", "type": { "name": "ID", "kind": "SCALAR" } }],
                                    "type": { "name": "Episode", "kind": "OBJECT" }
                                }
                            ]
                        }
                    ]
                }
            }
        }
        """;

        _httpHandler.SetResponse(introspectionResponse);

        var profile = CreateTestProfile();
        var endpoint = new Uri("https://api.example.com/graphql");

        // Act
        var schema = await _introspector.IntrospectAsync(endpoint, profile);

        // Assert
        Assert.NotNull(schema);
        Assert.Equal(2, schema.Queries.Count);
        
        var searchQuery = schema.Queries.FirstOrDefault(q => q.Name == "search");
        Assert.NotNull(searchQuery);
        Assert.Equal(QueryPurpose.Search, searchQuery.InferredPurpose);
        
        var episodeQuery = schema.Queries.FirstOrDefault(q => q.Name == "episode");
        Assert.NotNull(episodeQuery);
        Assert.Equal(QueryPurpose.GetEpisodes, episodeQuery.InferredPurpose);
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsNullForInvalidEndpoint()
    {
        // Arrange
        _httpHandler.SetResponse("Not Found", System.Net.HttpStatusCode.NotFound);

        var profile = CreateTestProfile();
        var endpoint = new Uri("https://api.example.com/invalid");

        // Act
        var schema = await _introspector.IntrospectAsync(endpoint, profile);

        // Assert
        Assert.Null(schema);
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsNullForNonGraphQLEndpoint()
    {
        // Arrange
        _httpHandler.SetResponse("<html><body>Not GraphQL</body></html>");

        var profile = CreateTestProfile();
        var endpoint = new Uri("https://api.example.com/notgraphql");

        // Act
        var schema = await _introspector.IntrospectAsync(endpoint, profile);

        // Assert
        Assert.Null(schema);
    }

    [Fact]
    public void GenerateQueries_CreatesSearchQuery()
    {
        // Arrange
        var schema = CreateTestSchema();

        // Act
        var queries = _introspector.GenerateQueries(schema, ProviderType.Anime);

        // Assert
        var searchQuery = queries.FirstOrDefault(q => q.Purpose == QueryPurpose.Search);
        Assert.NotNull(searchQuery);
        Assert.Contains("query", searchQuery.Query);
        Assert.True(searchQuery.Confidence > 0);
    }

    [Fact]
    public void GenerateQueries_CreatesAnimeQueries()
    {
        // Arrange
        var schema = CreateTestSchema();

        // Act
        var queries = _introspector.GenerateQueries(schema, ProviderType.Anime);

        // Assert
        Assert.True(queries.Count > 0);
        // Should have search-related queries
        Assert.Contains(queries, q => q.Purpose == QueryPurpose.Search || q.Purpose == QueryPurpose.GetById);
    }

    [Fact]
    public void GenerateQueries_CreatesMangaQueries()
    {
        // Arrange
        var schema = CreateTestSchemaForManga();

        // Act
        var queries = _introspector.GenerateQueries(schema, ProviderType.Manga);

        // Assert
        Assert.True(queries.Count > 0);
    }

    [Fact]
    public async Task IntrospectAsync_HandlesPartialSchema()
    {
        // Arrange - Schema without mutation type
        var introspectionResponse = """
        {
            "data": {
                "__schema": {
                    "queryType": { "name": "Query" },
                    "types": [
                        {
                            "kind": "OBJECT",
                            "name": "Query",
                            "fields": [
                                {
                                    "name": "anime",
                                    "args": [],
                                    "type": { "name": "Anime", "kind": "OBJECT" }
                                }
                            ]
                        }
                    ]
                }
            }
        }
        """;

        _httpHandler.SetResponse(introspectionResponse);

        var profile = CreateTestProfile();
        var endpoint = new Uri("https://api.example.com/graphql");

        // Act
        var schema = await _introspector.IntrospectAsync(endpoint, profile);

        // Assert
        Assert.NotNull(schema);
        Assert.Empty(schema.Mutations);
        Assert.Single(schema.Queries);
    }

    private static SiteProfile CreateTestProfile()
    {
        return new SiteProfile
        {
            BaseUrl = new Uri("https://example.com"),
            Type = SiteType.SPA,
            Category = ContentCategory.Anime,
            RequiresJavaScript = true,
            HasCloudflareProtection = false,
            HasGraphQL = true,
            ServerSoftware = null,
            JsFramework = "React",
            DetectedApiEndpoints = ["/graphql"],
            DetectedCdnHosts = [],
            RequiredHeaders = new Dictionary<string, string>
            {
                ["Referer"] = "https://example.com"
            },
            SiteTitle = "Test Anime Site",
            SiteDescription = "Watch anime online",
            RobotsTxt = null,
            Errors = []
        };
    }

    private static GraphQLSchemaInfo CreateTestSchema()
    {
        return new GraphQLSchemaInfo
        {
            Endpoint = new Uri("https://api.example.com/graphql"),
            Types =
            [
                new GraphQLType
                {
                    Name = "Show",
                    Kind = GraphQLTypeKind.Object,
                    Fields =
                    [
                        new GraphQLField { Name = "_id", TypeName = "String" },
                        new GraphQLField { Name = "name", TypeName = "String" },
                        new GraphQLField { Name = "availableEpisodesDetail", TypeName = "EpisodeDetail" }
                    ]
                }
            ],
            Queries =
            [
                new GraphQLQuery
                {
                    Name = "shows",
                    ReturnType = "ShowConnection",
                    Arguments = [new GraphQLArgument { Name = "search", TypeName = "SearchInput" }],
                    InferredPurpose = QueryPurpose.Search
                },
                new GraphQLQuery
                {
                    Name = "show",
                    ReturnType = "Show",
                    Arguments = [new GraphQLArgument { Name = "_id", TypeName = "String!", IsRequired = true }],
                    InferredPurpose = QueryPurpose.GetById
                }
            ],
            Mutations = [],
            SupportsIntrospection = true
        };
    }

    private static GraphQLSchemaInfo CreateTestSchemaForManga()
    {
        return new GraphQLSchemaInfo
        {
            Endpoint = new Uri("https://api.example.com/graphql"),
            Types =
            [
                new GraphQLType
                {
                    Name = "Manga",
                    Kind = GraphQLTypeKind.Object,
                    Fields =
                    [
                        new GraphQLField { Name = "_id", TypeName = "String" },
                        new GraphQLField { Name = "name", TypeName = "String" },
                        new GraphQLField { Name = "availableChaptersDetail", TypeName = "ChapterDetail" }
                    ]
                }
            ],
            Queries =
            [
                new GraphQLQuery
                {
                    Name = "mangas",
                    ReturnType = "MangaConnection",
                    Arguments = [new GraphQLArgument { Name = "search", TypeName = "SearchInput" }],
                    InferredPurpose = QueryPurpose.Search
                },
                new GraphQLQuery
                {
                    Name = "chapters",
                    ReturnType = "[Chapter]",
                    Arguments = [new GraphQLArgument { Name = "mangaId", TypeName = "String!", IsRequired = true }],
                    InferredPurpose = QueryPurpose.GetChapters
                }
            ],
            Mutations = [],
            SupportsIntrospection = true
        };
    }
}
