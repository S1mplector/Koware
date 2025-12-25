// Author: Ilgaz Mehmetoğlu
// Advanced GraphQL introspection for deep schema discovery.
using System.Text.Json;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Advanced GraphQL introspection engine for deep schema discovery.
/// </summary>
public interface IGraphQLIntrospector
{
    /// <summary>
    /// Perform full introspection of a GraphQL endpoint.
    /// </summary>
    Task<GraphQLSchemaInfo?> IntrospectAsync(Uri endpoint, SiteProfile profile, CancellationToken ct = default);
    
    /// <summary>
    /// Generate optimal queries based on discovered schema.
    /// </summary>
    IReadOnlyList<GeneratedQuery> GenerateQueries(GraphQLSchemaInfo schema, ProviderType targetType);
}

/// <summary>
/// Information about a GraphQL schema.
/// </summary>
public sealed record GraphQLSchemaInfo
{
    public required Uri Endpoint { get; init; }
    public required IReadOnlyList<GraphQLType> Types { get; init; }
    public required IReadOnlyList<GraphQLQuery> Queries { get; init; }
    public required IReadOnlyList<GraphQLMutation> Mutations { get; init; }
    public bool SupportsIntrospection { get; init; }
    public string? SchemaVersion { get; init; }
}

/// <summary>
/// A GraphQL type definition.
/// </summary>
public sealed record GraphQLType
{
    public required string Name { get; init; }
    public GraphQLTypeKind Kind { get; init; }
    public IReadOnlyList<GraphQLField> Fields { get; init; } = [];
    public string? Description { get; init; }
}

/// <summary>
/// GraphQL type kinds.
/// </summary>
public enum GraphQLTypeKind
{
    Scalar,
    Object,
    Interface,
    Union,
    Enum,
    InputObject,
    List,
    NonNull
}

/// <summary>
/// A GraphQL field definition.
/// </summary>
public sealed record GraphQLField
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public IReadOnlyList<GraphQLArgument> Arguments { get; init; } = [];
    public string? Description { get; init; }
}

/// <summary>
/// A GraphQL argument definition.
/// </summary>
public sealed record GraphQLArgument
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public bool IsRequired { get; init; }
    public string? DefaultValue { get; init; }
}

/// <summary>
/// A discovered GraphQL query.
/// </summary>
public sealed record GraphQLQuery
{
    public required string Name { get; init; }
    public required string ReturnType { get; init; }
    public IReadOnlyList<GraphQLArgument> Arguments { get; init; } = [];
    public QueryPurpose InferredPurpose { get; init; }
}

/// <summary>
/// A discovered GraphQL mutation.
/// </summary>
public sealed record GraphQLMutation
{
    public required string Name { get; init; }
    public required string ReturnType { get; init; }
    public IReadOnlyList<GraphQLArgument> Arguments { get; init; } = [];
}

/// <summary>
/// Inferred purpose of a query.
/// </summary>
public enum QueryPurpose
{
    Unknown,
    Search,
    GetById,
    ListAll,
    GetEpisodes,
    GetChapters,
    GetStreams,
    GetPages
}

/// <summary>
/// A generated query based on schema analysis.
/// </summary>
public sealed record GeneratedQuery
{
    public required string Name { get; init; }
    public required string Query { get; init; }
    public required string Variables { get; init; }
    public QueryPurpose Purpose { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Default implementation of GraphQL introspector.
/// </summary>
public sealed class GraphQLIntrospector : IGraphQLIntrospector
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphQLIntrospector> _logger;

    private const string FullIntrospectionQuery = @"
        query IntrospectionQuery {
            __schema {
                queryType { name }
                mutationType { name }
                types {
                    kind
                    name
                    description
                    fields(includeDeprecated: false) {
                        name
                        description
                        args {
                            name
                            type { name kind ofType { name kind } }
                            defaultValue
                        }
                        type { name kind ofType { name kind ofType { name kind } } }
                    }
                    inputFields {
                        name
                        type { name kind ofType { name kind } }
                        defaultValue
                    }
                    enumValues { name }
                }
            }
        }";

    private const string LightIntrospectionQuery = @"
        query { __schema { types { name kind } } }";

    // Keywords for inferring query purposes
    private static readonly Dictionary<QueryPurpose, string[]> PurposeKeywords = new()
    {
        [QueryPurpose.Search] = ["search", "find", "query", "lookup"],
        [QueryPurpose.GetById] = ["show", "anime", "manga", "get", "fetch"],
        [QueryPurpose.ListAll] = ["shows", "animes", "mangas", "list", "all"],
        [QueryPurpose.GetEpisodes] = ["episode", "episodes", "availableEpisodes"],
        [QueryPurpose.GetChapters] = ["chapter", "chapters", "availableChapters"],
        [QueryPurpose.GetStreams] = ["source", "sources", "stream", "streams", "sourceUrls"],
        [QueryPurpose.GetPages] = ["page", "pages", "image", "images", "pictureUrls"]
    };

    public GraphQLIntrospector(HttpClient httpClient, ILogger<GraphQLIntrospector> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<GraphQLSchemaInfo?> IntrospectAsync(Uri endpoint, SiteProfile profile, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting GraphQL introspection for {Endpoint}", endpoint);

        // Try full introspection first
        var schema = await TryIntrospectAsync(endpoint, profile, FullIntrospectionQuery, ct);
        
        if (schema == null)
        {
            // Fall back to light introspection
            _logger.LogWarning("Full introspection failed, trying light introspection");
            schema = await TryIntrospectAsync(endpoint, profile, LightIntrospectionQuery, ct);
        }

        if (schema == null)
        {
            _logger.LogWarning("GraphQL introspection failed for {Endpoint}", endpoint);
            return null;
        }

        return schema;
    }

    private async Task<GraphQLSchemaInfo?> TryIntrospectAsync(
        Uri endpoint, 
        SiteProfile profile, 
        string introspectionQuery,
        CancellationToken ct)
    {
        try
        {
            var requestUrl = new Uri($"{endpoint}?query={Uri.EscapeDataString(introspectionQuery)}");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Referrer = profile.BaseUrl;
            
            foreach (var (key, value) in profile.RequiredHeaders)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(ct);
            return ParseIntrospectionResult(endpoint, content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Introspection request failed");
            return null;
        }
    }

    private GraphQLSchemaInfo? ParseIntrospectionResult(Uri endpoint, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("__schema", out var schema))
            {
                return null;
            }

            var types = new List<GraphQLType>();
            var queries = new List<GraphQLQuery>();
            var mutations = new List<GraphQLMutation>();

            // Parse types
            if (schema.TryGetProperty("types", out var typesArray))
            {
                foreach (var typeEl in typesArray.EnumerateArray())
                {
                    var typeName = typeEl.GetProperty("name").GetString() ?? "";
                    
                    // Skip introspection types
                    if (typeName.StartsWith("__"))
                        continue;

                    var type = ParseType(typeEl);
                    if (type != null)
                        types.Add(type);
                }
            }

            // Find query type and extract queries
            var queryTypeName = "Query";
            if (schema.TryGetProperty("queryType", out var queryType) &&
                queryType.TryGetProperty("name", out var qtn))
            {
                queryTypeName = qtn.GetString() ?? "Query";
            }

            var queryRootType = types.FirstOrDefault(t => t.Name == queryTypeName);
            if (queryRootType != null)
            {
                foreach (var field in queryRootType.Fields)
                {
                    queries.Add(new GraphQLQuery
                    {
                        Name = field.Name,
                        ReturnType = field.TypeName,
                        Arguments = field.Arguments,
                        InferredPurpose = InferPurpose(field.Name, field.Arguments)
                    });
                }
            }

            // Find mutation type
            var mutationTypeName = "Mutation";
            if (schema.TryGetProperty("mutationType", out var mutationType) &&
                mutationType.TryGetProperty("name", out var mtn))
            {
                mutationTypeName = mtn.GetString() ?? "Mutation";
            }

            var mutationRootType = types.FirstOrDefault(t => t.Name == mutationTypeName);
            if (mutationRootType != null)
            {
                foreach (var field in mutationRootType.Fields)
                {
                    mutations.Add(new GraphQLMutation
                    {
                        Name = field.Name,
                        ReturnType = field.TypeName,
                        Arguments = field.Arguments
                    });
                }
            }

            _logger.LogInformation("Introspection found {Types} types, {Queries} queries, {Mutations} mutations",
                types.Count, queries.Count, mutations.Count);

            return new GraphQLSchemaInfo
            {
                Endpoint = endpoint,
                Types = types,
                Queries = queries,
                Mutations = mutations,
                SupportsIntrospection = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse introspection result");
            return null;
        }
    }

    private GraphQLType? ParseType(JsonElement typeEl)
    {
        var name = typeEl.GetProperty("name").GetString() ?? "";
        var kindStr = typeEl.GetProperty("kind").GetString() ?? "OBJECT";
        
        var kind = kindStr switch
        {
            "SCALAR" => GraphQLTypeKind.Scalar,
            "OBJECT" => GraphQLTypeKind.Object,
            "INTERFACE" => GraphQLTypeKind.Interface,
            "UNION" => GraphQLTypeKind.Union,
            "ENUM" => GraphQLTypeKind.Enum,
            "INPUT_OBJECT" => GraphQLTypeKind.InputObject,
            "LIST" => GraphQLTypeKind.List,
            "NON_NULL" => GraphQLTypeKind.NonNull,
            _ => GraphQLTypeKind.Object
        };

        var fields = new List<GraphQLField>();
        
        if (typeEl.TryGetProperty("fields", out var fieldsArray) && 
            fieldsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var fieldEl in fieldsArray.EnumerateArray())
            {
                var field = ParseField(fieldEl);
                if (field != null)
                    fields.Add(field);
            }
        }

        var description = typeEl.TryGetProperty("description", out var desc) 
            ? desc.GetString() 
            : null;

        return new GraphQLType
        {
            Name = name,
            Kind = kind,
            Fields = fields,
            Description = description
        };
    }

    private GraphQLField? ParseField(JsonElement fieldEl)
    {
        var name = fieldEl.GetProperty("name").GetString() ?? "";
        var typeName = GetTypeName(fieldEl.GetProperty("type"));
        
        var arguments = new List<GraphQLArgument>();
        
        if (fieldEl.TryGetProperty("args", out var argsArray))
        {
            foreach (var argEl in argsArray.EnumerateArray())
            {
                var arg = ParseArgument(argEl);
                if (arg != null)
                    arguments.Add(arg);
            }
        }

        var description = fieldEl.TryGetProperty("description", out var desc) 
            ? desc.GetString() 
            : null;

        return new GraphQLField
        {
            Name = name,
            TypeName = typeName,
            Arguments = arguments,
            Description = description
        };
    }

    private GraphQLArgument? ParseArgument(JsonElement argEl)
    {
        var name = argEl.GetProperty("name").GetString() ?? "";
        var typeName = GetTypeName(argEl.GetProperty("type"));
        var isRequired = typeName.EndsWith("!");
        
        var defaultValue = argEl.TryGetProperty("defaultValue", out var dv) 
            ? dv.GetString() 
            : null;

        return new GraphQLArgument
        {
            Name = name,
            TypeName = typeName,
            IsRequired = isRequired,
            DefaultValue = defaultValue
        };
    }

    private string GetTypeName(JsonElement typeEl)
    {
        if (typeEl.ValueKind == JsonValueKind.Null)
            return "Unknown";

        var kind = typeEl.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var name = typeEl.TryGetProperty("name", out var n) ? n.GetString() : null;

        if (kind == "NON_NULL" && typeEl.TryGetProperty("ofType", out var ofType))
        {
            return GetTypeName(ofType) + "!";
        }
        
        if (kind == "LIST" && typeEl.TryGetProperty("ofType", out var listOfType))
        {
            return "[" + GetTypeName(listOfType) + "]";
        }

        return name ?? "Unknown";
    }

    private QueryPurpose InferPurpose(string queryName, IReadOnlyList<GraphQLArgument> arguments)
    {
        var lowerName = queryName.ToLowerInvariant();
        
        foreach (var (purpose, keywords) in PurposeKeywords)
        {
            if (keywords.Any(kw => lowerName.Contains(kw)))
            {
                return purpose;
            }
        }

        // Infer from arguments
        var argNames = arguments.Select(a => a.Name.ToLowerInvariant()).ToList();
        
        if (argNames.Any(a => a.Contains("search") || a.Contains("query") || a.Contains("keyword")))
            return QueryPurpose.Search;
        
        if (argNames.Any(a => a.Contains("id") || a.Contains("showid") || a.Contains("mangaid")))
            return QueryPurpose.GetById;

        return QueryPurpose.Unknown;
    }

    public IReadOnlyList<GeneratedQuery> GenerateQueries(GraphQLSchemaInfo schema, ProviderType targetType)
    {
        var queries = new List<GeneratedQuery>();

        // Find search query
        var searchQuery = schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.Search)
            ?? schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.ListAll);
        
        if (searchQuery != null)
        {
            queries.Add(GenerateSearchQuery(searchQuery, schema));
        }

        // Find content detail query
        var detailQuery = schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.GetById);
        if (detailQuery != null)
        {
            queries.Add(GenerateDetailQuery(detailQuery, schema, targetType));
        }

        // Find episode/chapter query
        if (targetType == ProviderType.Anime)
        {
            var episodeQuery = schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.GetEpisodes);
            if (episodeQuery != null)
            {
                queries.Add(GenerateEpisodeQuery(episodeQuery, schema));
            }
        }
        else
        {
            var chapterQuery = schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.GetChapters);
            if (chapterQuery != null)
            {
                queries.Add(GenerateChapterQuery(chapterQuery, schema));
            }
        }

        // Find stream/page query
        var mediaQuery = targetType == ProviderType.Anime
            ? schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.GetStreams)
            : schema.Queries.FirstOrDefault(q => q.InferredPurpose == QueryPurpose.GetPages);
        
        if (mediaQuery != null)
        {
            queries.Add(GenerateMediaQuery(mediaQuery, schema, targetType));
        }

        return queries;
    }

    private GeneratedQuery GenerateSearchQuery(GraphQLQuery query, GraphQLSchemaInfo schema)
    {
        var args = query.Arguments;
        var argDefs = string.Join(", ", args.Select(a => $"${a.Name}: {a.TypeName}"));
        var argVals = string.Join(", ", args.Select(a => $"{a.Name}: ${a.Name}"));
        
        var returnType = schema.Types.FirstOrDefault(t => t.Name == query.ReturnType.TrimEnd('!', '[', ']'));
        var fields = returnType?.Fields.Take(5).Select(f => f.Name) ?? ["_id", "name"];
        
        var queryStr = $"query({argDefs}) {{ {query.Name}({argVals}) {{ {string.Join(" ", fields)} }} }}";
        var variables = "{" + string.Join(", ", args.Select(a => $"\"{a.Name}\": \"\"")) + "}";

        return new GeneratedQuery
        {
            Name = "Search",
            Query = queryStr,
            Variables = variables,
            Purpose = QueryPurpose.Search,
            Confidence = 0.8f
        };
    }

    private GeneratedQuery GenerateDetailQuery(GraphQLQuery query, GraphQLSchemaInfo schema, ProviderType type)
    {
        var args = query.Arguments;
        var argDefs = string.Join(", ", args.Select(a => $"${a.Name}: {a.TypeName}"));
        var argVals = string.Join(", ", args.Select(a => $"{a.Name}: ${a.Name}"));
        
        var extraFields = type == ProviderType.Anime ? "availableEpisodesDetail" : "availableChaptersDetail";
        
        var queryStr = $"query({argDefs}) {{ {query.Name}({argVals}) {{ _id name {extraFields} }} }}";
        var variables = "{" + string.Join(", ", args.Select(a => $"\"{a.Name}\": \"\"")) + "}";

        return new GeneratedQuery
        {
            Name = "Detail",
            Query = queryStr,
            Variables = variables,
            Purpose = QueryPurpose.GetById,
            Confidence = 0.75f
        };
    }

    private GeneratedQuery GenerateEpisodeQuery(GraphQLQuery query, GraphQLSchemaInfo schema)
    {
        var args = query.Arguments;
        var argDefs = string.Join(", ", args.Select(a => $"${a.Name}: {a.TypeName}"));
        var argVals = string.Join(", ", args.Select(a => $"{a.Name}: ${a.Name}"));
        
        var queryStr = $"query({argDefs}) {{ {query.Name}({argVals}) {{ episodeString sourceUrls }} }}";
        var variables = "{" + string.Join(", ", args.Select(a => $"\"{a.Name}\": \"\"")) + "}";

        return new GeneratedQuery
        {
            Name = "Episode",
            Query = queryStr,
            Variables = variables,
            Purpose = QueryPurpose.GetEpisodes,
            Confidence = 0.7f
        };
    }

    private GeneratedQuery GenerateChapterQuery(GraphQLQuery query, GraphQLSchemaInfo schema)
    {
        var args = query.Arguments;
        var argDefs = string.Join(", ", args.Select(a => $"${a.Name}: {a.TypeName}"));
        var argVals = string.Join(", ", args.Select(a => $"{a.Name}: ${a.Name}"));
        
        var queryStr = $"query({argDefs}) {{ {query.Name}({argVals}) {{ chapterString pictureUrls }} }}";
        var variables = "{" + string.Join(", ", args.Select(a => $"\"{a.Name}\": \"\"")) + "}";

        return new GeneratedQuery
        {
            Name = "Chapter",
            Query = queryStr,
            Variables = variables,
            Purpose = QueryPurpose.GetChapters,
            Confidence = 0.7f
        };
    }

    private GeneratedQuery GenerateMediaQuery(GraphQLQuery query, GraphQLSchemaInfo schema, ProviderType type)
    {
        var args = query.Arguments;
        var argDefs = string.Join(", ", args.Select(a => $"${a.Name}: {a.TypeName}"));
        var argVals = string.Join(", ", args.Select(a => $"{a.Name}: ${a.Name}"));
        
        var fields = type == ProviderType.Anime ? "sourceUrls" : "pictureUrls";
        
        var queryStr = $"query({argDefs}) {{ {query.Name}({argVals}) {{ {fields} }} }}";
        var variables = "{" + string.Join(", ", args.Select(a => $"\"{a.Name}\": \"\"")) + "}";

        return new GeneratedQuery
        {
            Name = type == ProviderType.Anime ? "Streams" : "Pages",
            Query = queryStr,
            Variables = variables,
            Purpose = type == ProviderType.Anime ? QueryPurpose.GetStreams : QueryPurpose.GetPages,
            Confidence = 0.65f
        };
    }
}
