// Author: Ilgaz Mehmetoğlu
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Analyzes content structure and patterns on a website.
/// </summary>
public sealed class ContentPatternMatcher : IContentPatternMatcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ContentPatternMatcher> _logger;

    public ContentPatternMatcher(HttpClient httpClient, ILogger<ContentPatternMatcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ContentSchema> AnalyzeAsync(
        SiteProfile profile,
        IReadOnlyList<ApiEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Analyzing content patterns for {Url}", profile.BaseUrl);

        // Find search endpoint
        var searchEndpoint = endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Search);
        var searchPattern = searchEndpoint != null 
            ? BuildSearchPattern(searchEndpoint, profile) 
            : null;

        // Find episode/chapter endpoint
        var episodeEndpoint = endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Episodes);
        var chapterEndpoint = endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Chapters);

        var episodePattern = episodeEndpoint != null 
            ? BuildEpisodePattern(episodeEndpoint, profile) 
            : null;
        var chapterPattern = chapterEndpoint != null 
            ? BuildChapterPattern(chapterEndpoint, profile) 
            : null;

        // Find stream/page endpoints
        var streamEndpoint = endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Streams);
        var pageEndpoint = endpoints.FirstOrDefault(e => e.Purpose == EndpointPurpose.Pages);

        var mediaPattern = BuildMediaPattern(streamEndpoint, pageEndpoint, profile);

        // Analyze ID patterns from sample responses
        var idPattern = AnalyzeIdPattern(endpoints);

        return new ContentSchema
        {
            SearchPattern = searchPattern,
            IdPattern = idPattern,
            EpisodePattern = episodePattern,
            ChapterPattern = chapterPattern,
            MediaPattern = mediaPattern,
            Endpoints = endpoints
        };
    }

    private SearchPattern? BuildSearchPattern(ApiEndpoint endpoint, SiteProfile profile)
    {
        var method = endpoint.Type switch
        {
            ApiType.GraphQL => SearchMethod.GraphQL,
            ApiType.REST => SearchMethod.REST,
            _ => SearchMethod.HtmlScrape
        };

        // Use auto-detected mappings from endpoint if available, otherwise analyze sample response
        var mappings = endpoint.FieldMappings ?? new List<FieldMapping>();
        
        if (mappings.Count == 0 && !string.IsNullOrEmpty(endpoint.SampleResponse))
        {
            mappings = AnalyzeJsonResponse(endpoint.SampleResponse, "search");
        }

        // Determine results path - prefer auto-detected, fallback to analysis
        var resultsPath = endpoint.ResultsPath ?? DetermineResultsPath(endpoint.SampleResponse);

        _logger.LogInformation("Built search pattern with {Count} field mappings, results path: {Path}",
            mappings.Count, resultsPath ?? "none");

        return new SearchPattern
        {
            Method = method,
            Endpoint = endpoint.Url.PathAndQuery,
            QueryTemplate = endpoint.SampleQuery ?? BuildQueryTemplate(endpoint, "search"),
            ResultsPath = resultsPath,
            FieldMappings = mappings
        };
    }

    private EpisodePattern? BuildEpisodePattern(ApiEndpoint endpoint, SiteProfile profile)
    {
        var method = endpoint.Type switch
        {
            ApiType.GraphQL => SearchMethod.GraphQL,
            ApiType.REST => SearchMethod.REST,
            _ => SearchMethod.HtmlScrape
        };

        var mappings = new List<FieldMapping>();
        
        if (!string.IsNullOrEmpty(endpoint.SampleResponse))
        {
            mappings = AnalyzeJsonResponse(endpoint.SampleResponse, "episodes");
        }

        return new EpisodePattern
        {
            Method = method,
            Endpoint = endpoint.Url.PathAndQuery,
            QueryTemplate = endpoint.SampleQuery ?? BuildQueryTemplate(endpoint, "episodes"),
            ListPath = DetermineListPath(endpoint.SampleResponse, "episodes"),
            FieldMappings = mappings.Count > 0 ? mappings : GetDefaultEpisodeMappings()
        };
    }

    private ChapterPattern? BuildChapterPattern(ApiEndpoint endpoint, SiteProfile profile)
    {
        var method = endpoint.Type switch
        {
            ApiType.GraphQL => SearchMethod.GraphQL,
            ApiType.REST => SearchMethod.REST,
            _ => SearchMethod.HtmlScrape
        };

        var mappings = new List<FieldMapping>();
        
        if (!string.IsNullOrEmpty(endpoint.SampleResponse))
        {
            mappings = AnalyzeJsonResponse(endpoint.SampleResponse, "chapters");
        }

        return new ChapterPattern
        {
            Method = method,
            Endpoint = endpoint.Url.PathAndQuery,
            QueryTemplate = endpoint.SampleQuery ?? BuildQueryTemplate(endpoint, "chapters"),
            ListPath = DetermineListPath(endpoint.SampleResponse, "chapters"),
            FieldMappings = mappings.Count > 0 ? mappings : GetDefaultChapterMappings()
        };
    }

    private MediaPattern? BuildMediaPattern(ApiEndpoint? streamEndpoint, ApiEndpoint? pageEndpoint, SiteProfile profile)
    {
        var endpoint = streamEndpoint ?? pageEndpoint;
        if (endpoint == null)
            return null;

        var method = endpoint.Type switch
        {
            ApiType.GraphQL => SearchMethod.GraphQL,
            ApiType.REST => SearchMethod.REST,
            _ => SearchMethod.HtmlScrape
        };

        // Check if URLs appear to be encoded
        var requiresDecoding = false;
        var customDecoder = (string?)null;
        
        if (!string.IsNullOrEmpty(endpoint.SampleResponse))
        {
            // Look for encoded URL patterns
            if (endpoint.SampleResponse.Contains("-") && 
                Regex.IsMatch(endpoint.SampleResponse, @"-[a-fA-F0-9]{20,}"))
            {
                requiresDecoding = true;
                customDecoder = "AllAnimeSourceDecoder";
            }
        }

        return new MediaPattern
        {
            Method = method,
            Endpoint = endpoint.Url.PathAndQuery,
            QueryTemplate = endpoint.SampleQuery ?? BuildQueryTemplate(endpoint, "media"),
            MediaPath = DetermineMediaPath(endpoint.SampleResponse),
            RequiresDecoding = requiresDecoding,
            CustomDecoder = customDecoder,
            FieldMappings = streamEndpoint != null ? GetDefaultStreamMappings() : GetDefaultPageMappings()
        };
    }

    private ContentIdentifierPattern? AnalyzeIdPattern(IReadOnlyList<ApiEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints.Where(e => !string.IsNullOrEmpty(e.SampleResponse)))
        {
            try
            {
                using var doc = JsonDocument.Parse(endpoint.SampleResponse!);
                var idValue = FindIdInJson(doc.RootElement);
                
                if (!string.IsNullOrEmpty(idValue))
                {
                    return new ContentIdentifierPattern
                    {
                        JsonPath = "$.data.*.edges[*]._id",
                        Example = idValue
                    };
                }
            }
            catch
            {
                // Continue
            }
        }

        return null;
    }

    private static string? FindIdInJson(JsonElement element, int depth = 0)
    {
        if (depth > 5) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Equals("_id", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                }
                
                var nested = FindIdInJson(prop.Value, depth + 1);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().Take(3))
            {
                var nested = FindIdInJson(item, depth + 1);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private List<FieldMapping> AnalyzeJsonResponse(string json, string context)
    {
        var mappings = new List<FieldMapping>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var fields = ExtractFieldPaths(doc.RootElement, "");
            
            // Map common field names
            var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_id"] = "Id",
                ["id"] = "Id",
                ["name"] = "Title",
                ["title"] = "Title",
                ["englishName"] = "Title",
                ["thumbnail"] = "CoverImage",
                ["cover"] = "CoverImage",
                ["image"] = "CoverImage",
                ["poster"] = "CoverImage",
                ["description"] = "Synopsis",
                ["synopsis"] = "Synopsis",
                ["number"] = "Number",
                ["episode"] = "Number",
                ["chapter"] = "Number",
                ["url"] = "Url",
                ["link"] = "Url",
                ["sourceUrl"] = "Url",
                ["quality"] = "Quality",
                ["resolution"] = "Quality"
            };

            foreach (var (path, fieldName) in fields)
            {
                if (fieldMap.TryGetValue(fieldName, out var targetField))
                {
                    mappings.Add(new FieldMapping
                    {
                        SourcePath = path,
                        TargetField = targetField
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to analyze JSON response for {Context}", context);
        }

        return mappings;
    }

    private static List<(string path, string fieldName)> ExtractFieldPaths(JsonElement element, string currentPath, int depth = 0)
    {
        var fields = new List<(string, string)>();
        if (depth > 5) return fields;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var newPath = string.IsNullOrEmpty(currentPath) 
                    ? $"$.{prop.Name}" 
                    : $"{currentPath}.{prop.Name}";

                if (prop.Value.ValueKind == JsonValueKind.String ||
                    prop.Value.ValueKind == JsonValueKind.Number)
                {
                    fields.Add((newPath, prop.Name));
                }
                else
                {
                    fields.AddRange(ExtractFieldPaths(prop.Value, newPath, depth + 1));
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
        {
            var first = element[0];
            var arrayPath = $"{currentPath}[*]";
            fields.AddRange(ExtractFieldPaths(first, arrayPath, depth + 1));
        }

        return fields;
    }

    private static string BuildQueryTemplate(ApiEndpoint endpoint, string context)
    {
        if (endpoint.Type == ApiType.GraphQL && !string.IsNullOrEmpty(endpoint.SampleQuery))
            return endpoint.SampleQuery;

        return context switch
        {
            "search" => endpoint.Type == ApiType.GraphQL 
                ? "query($search: SearchInput) { shows(search: $search) { edges { _id name } } }"
                : "?q=${query}",
            "episodes" => endpoint.Type == ApiType.GraphQL
                ? "query($showId: String!) { show(_id: $showId) { availableEpisodesDetail } }"
                : "/${id}/episodes",
            "chapters" => endpoint.Type == ApiType.GraphQL
                ? "query($mangaId: String!) { manga(_id: $mangaId) { availableChaptersDetail } }"
                : "/${id}/chapters",
            "media" => endpoint.Type == ApiType.GraphQL
                ? "query($showId: String!, $ep: String!) { episode(showId: $showId, episodeString: $ep) { sourceUrls } }"
                : "/${id}/sources",
            _ => ""
        };
    }

    private static string? DetermineResultsPath(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                foreach (var prop in data.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("edges", out _))
                        return $"$.data.{prop.Name}.edges";
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        return $"$.data.{prop.Name}";
                }
            }

            if (root.TryGetProperty("results", out _))
                return "$.results";
            if (root.TryGetProperty("items", out _))
                return "$.items";
            if (root.ValueKind == JsonValueKind.Array)
                return "$";
        }
        catch
        {
            // Continue
        }

        return null;
    }

    private static string? DetermineListPath(string? json, string context)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                var keywords = context == "episodes" 
                    ? new[] { "episodes", "availableEpisodesDetail" }
                    : new[] { "chapters", "availableChaptersDetail" };

                foreach (var prop in data.EnumerateObject())
                {
                    foreach (var keyword in keywords)
                    {
                        if (prop.Value.TryGetProperty(keyword, out _))
                            return $"$.data.{prop.Name}.{keyword}";
                    }
                }
            }
        }
        catch
        {
            // Continue
        }

        return null;
    }

    private static string? DetermineMediaPath(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                foreach (var prop in data.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("sourceUrls", out _))
                        return $"$.data.{prop.Name}.sourceUrls";
                    if (prop.Value.TryGetProperty("sources", out _))
                        return $"$.data.{prop.Name}.sources";
                    if (prop.Value.TryGetProperty("pictureUrls", out _))
                        return $"$.data.{prop.Name}.pictureUrls";
                }
            }
        }
        catch
        {
            // Continue
        }

        return null;
    }

    private static List<FieldMapping> GetDefaultEpisodeMappings() =>
    [
        new FieldMapping { SourcePath = "$._id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.number", TargetField = "Number" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" }
    ];

    private static List<FieldMapping> GetDefaultChapterMappings() =>
    [
        new FieldMapping { SourcePath = "$._id", TargetField = "Id" },
        new FieldMapping { SourcePath = "$.number", TargetField = "Number" },
        new FieldMapping { SourcePath = "$.title", TargetField = "Title" }
    ];

    private static List<FieldMapping> GetDefaultStreamMappings() =>
    [
        new FieldMapping { SourcePath = "$.sourceUrl", TargetField = "Url" },
        new FieldMapping { SourcePath = "$.sourceName", TargetField = "Provider" },
        new FieldMapping { SourcePath = "$.quality", TargetField = "Quality" }
    ];

    private static List<FieldMapping> GetDefaultPageMappings() =>
    [
        new FieldMapping { SourcePath = "$.url", TargetField = "Url" },
        new FieldMapping { SourcePath = "$.num", TargetField = "Number" }
    ];
}
