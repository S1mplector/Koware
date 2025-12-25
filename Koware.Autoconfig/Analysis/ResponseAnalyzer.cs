// Author: Ilgaz Mehmetoğlu
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Analyzes API responses to automatically detect field mappings.
/// Uses heuristics to identify common fields like title, id, cover image, etc.
/// </summary>
public sealed class ResponseAnalyzer : IResponseAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ResponseAnalyzer> _logger;

    // Common test queries for different content types
    private static readonly string[] TestQueries = ["naruto", "one piece", "attack", "demon", "love"];
    
    // Field detection patterns - maps target field to potential JSON property names
    private static readonly Dictionary<string, string[]> FieldPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = [
            "id", "_id", "ID", "Id", "mediaId", "media_id", "slug", "code", 
            "galleryId", "gallery_id", "hentai_video_id", "videoId", "video_id",
            "manga_id", "anime_id", "content_id", "item_id"
        ],
        ["Title"] = [
            "title", "name", "Title", "Name", "englishTitle", "english_title", 
            "romajiTitle", "romaji_title", "japanese_title", "japaneseTitle",
            "pretty", "title_english", "title_romaji", "alt_title", "original_title"
        ],
        ["Synopsis"] = [
            "synopsis", "description", "summary", "plot", "desc", "about", 
            "overview", "info", "details", "content"
        ],
        ["CoverImage"] = [
            "cover", "coverImage", "cover_image", "thumbnail", "thumb", "poster", 
            "image", "img", "cover_url", "coverUrl", "images.cover", "poster_url",
            "thumbnail_url", "banner", "artwork", "photo"
        ],
        ["DetailPage"] = [
            "url", "link", "detailPage", "detail_page", "href", "page_url", 
            "pageUrl", "permalink", "canonical"
        ],
        ["Number"] = [
            "number", "num", "episode", "chapter", "ep", "ch", "episodeNumber", 
            "chapterNumber", "episode_number", "chapter_number", "sequence", "order"
        ],
        ["Url"] = [
            "url", "src", "source", "link", "stream", "video", "file", "hls", 
            "m3u8", "mp4", "video_url", "stream_url", "manifest", "player_url"
        ],
        ["Quality"] = [
            "quality", "resolution", "res", "height", "label", "format", 
            "video_quality", "bitrate"
        ],
        ["Language"] = [
            "lang", "language", "locale", "sub", "dub", "audio", "subtitles"
        ],
        ["Tags"] = [
            "tags", "genres", "categories", "labels", "keywords"
        ],
        ["PageCount"] = [
            "num_pages", "page_count", "pages", "total_pages", "num_images"
        ],
        ["ReleaseDate"] = [
            "release_date", "released", "released_at", "created_at", "upload_date",
            "date", "published", "aired"
        ]
    };

    // URL patterns for detecting image/video fields
    private static readonly Regex ImageUrlPattern = new(@"\.(jpg|jpeg|png|gif|webp|svg|avif)(\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VideoUrlPattern = new(@"\.(m3u8|mp4|webm|mkv|ts)(\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ResponseAnalyzer(HttpClient httpClient, ILogger<ResponseAnalyzer> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Analyze a search endpoint and generate result mappings.
    /// </summary>
    public async Task<ResponseAnalysisResult> AnalyzeSearchEndpointAsync(
        string baseUrl,
        string searchEndpoint,
        string queryTemplate,
        Dictionary<string, string> headers,
        CancellationToken ct = default)
    {
        var result = new ResponseAnalysisResult();

        foreach (var testQuery in TestQueries)
        {
            try
            {
                var url = BuildSearchUrl(baseUrl, searchEndpoint, queryTemplate, testQuery);
                var response = await FetchJsonAsync(url, headers, ct);
                
                if (response == null)
                    continue;

                var analysis = AnalyzeJsonStructure(response, "Search");
                
                if (analysis.Mappings.Count > 0)
                {
                    result.SearchMappings = analysis.Mappings;
                    result.ResultsPath = analysis.ArrayPath;
                    result.SampleResponse = response;
                    result.SuccessfulQuery = testQuery;
                    result.Confidence = analysis.Confidence;
                    
                    _logger.LogInformation("Successfully analyzed search endpoint with query '{Query}', found {Count} mappings",
                        testQuery, analysis.Mappings.Count);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Test query '{Query}' failed", testQuery);
            }
        }

        return result;
    }

    /// <summary>
    /// Analyze an endpoint response and detect field mappings.
    /// </summary>
    public async Task<ResponseAnalysisResult> AnalyzeEndpointAsync(
        string url,
        Dictionary<string, string> headers,
        string purpose,
        CancellationToken ct = default)
    {
        var result = new ResponseAnalysisResult();

        try
        {
            var response = await FetchJsonAsync(url, headers, ct);
            
            if (response != null)
            {
                var analysis = AnalyzeJsonStructure(response, purpose);
                result.SearchMappings = analysis.Mappings;
                result.ResultsPath = analysis.ArrayPath;
                result.SampleResponse = response;
                result.Confidence = analysis.Confidence;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to analyze endpoint {Url}", url);
        }

        return result;
    }

    /// <summary>
    /// Analyze a JSON response and detect field mappings using heuristics.
    /// </summary>
    public JsonAnalysisResult AnalyzeJsonStructure(string json, string purpose)
    {
        var result = new JsonAnalysisResult();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Find the results array (could be root, or nested in data/results/items)
            var (arrayElement, arrayPath) = FindResultsArray(root);
            result.ArrayPath = arrayPath;

            if (arrayElement.HasValue && arrayElement.Value.ValueKind == JsonValueKind.Array && arrayElement.Value.GetArrayLength() > 0)
            {
                // Analyze first item in array
                var firstItem = arrayElement.Value[0];
                result.Mappings = AnalyzeObject(firstItem, "$" + arrayPath + "[*]", purpose);
                result.Confidence = CalculateConfidence(result.Mappings, purpose);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Single object response
                result.Mappings = AnalyzeObject(root, "$", purpose);
                result.Confidence = CalculateConfidence(result.Mappings, purpose);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse JSON response");
        }

        return result;
    }

    private (JsonElement? element, string path) FindResultsArray(JsonElement root)
    {
        // Common paths where results arrays are found (expanded for more sites)
        string[] commonPaths = [
            "data", "results", "items", "list", "entries", "edges", "nodes", 
            "galleries", "videos", "anime", "manga", "shows", "result",
            "hentai_videos", "search_results", "content", "hits", "records",
            "chapters", "episodes", "pages", "images", "streams"
        ];

        // Check if root is already an array
        if (root.ValueKind == JsonValueKind.Array)
        {
            return (root, "");
        }

        // Check common nested paths
        foreach (var path in commonPaths)
        {
            if (root.TryGetProperty(path, out var element))
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    return (element, "." + path);
                }
                
                // Check one level deeper (e.g., data.results, data.items)
                foreach (var subPath in commonPaths)
                {
                    if (element.TryGetProperty(subPath, out var subElement) && subElement.ValueKind == JsonValueKind.Array)
                    {
                        return (subElement, $".{path}.{subPath}");
                    }
                }
                
                // Check for edges (GraphQL pattern)
                if (element.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                {
                    return (edges, $".{path}.edges");
                }
            }
        }

        // Check for GraphQL data wrapper
        if (root.TryGetProperty("data", out var dataElement))
        {
            return FindResultsArray(dataElement);
        }

        return (null, "");
    }

    private List<FieldMapping> AnalyzeObject(JsonElement obj, string basePath, string purpose)
    {
        var mappings = new List<FieldMapping>();
        var detectedFields = new HashSet<string>();

        if (obj.ValueKind != JsonValueKind.Object)
            return mappings;

        foreach (var property in obj.EnumerateObject())
        {
            var propName = property.Name;
            var propValue = property.Value;
            var propPath = $"{basePath}.{propName}";

            // Try to match this property to a known field
            foreach (var (targetField, patterns) in FieldPatterns)
            {
                if (detectedFields.Contains(targetField))
                    continue;

                // Check name match
                if (patterns.Any(p => p.Equals(propName, StringComparison.OrdinalIgnoreCase)))
                {
                    mappings.Add(CreateMapping(propPath, targetField, propValue));
                    detectedFields.Add(targetField);
                    break;
                }

                // Check value patterns (e.g., URLs)
                if (propValue.ValueKind == JsonValueKind.String)
                {
                    var strValue = propValue.GetString() ?? "";
                    
                    if (targetField == "CoverImage" && !detectedFields.Contains("CoverImage") && ImageUrlPattern.IsMatch(strValue))
                    {
                        mappings.Add(CreateMapping(propPath, "CoverImage", propValue));
                        detectedFields.Add("CoverImage");
                        break;
                    }
                    
                    if (targetField == "Url" && !detectedFields.Contains("Url") && VideoUrlPattern.IsMatch(strValue))
                    {
                        mappings.Add(CreateMapping(propPath, "Url", propValue));
                        detectedFields.Add("Url");
                        break;
                    }
                }
            }

            // Recurse into nested objects for images
            if (propValue.ValueKind == JsonValueKind.Object && !detectedFields.Contains("CoverImage"))
            {
                var nestedMappings = AnalyzeNestedForImages(propValue, propPath);
                foreach (var mapping in nestedMappings)
                {
                    if (!detectedFields.Contains(mapping.TargetField))
                    {
                        mappings.Add(mapping);
                        detectedFields.Add(mapping.TargetField);
                    }
                }
            }

            // Check arrays for nested items (e.g., images array)
            if (propValue.ValueKind == JsonValueKind.Array && propValue.GetArrayLength() > 0)
            {
                var firstArrayItem = propValue[0];
                
                // Images array
                if (propName.Contains("image", StringComparison.OrdinalIgnoreCase) || 
                    propName.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                    propName.Contains("thumb", StringComparison.OrdinalIgnoreCase))
                {
                    if (!detectedFields.Contains("CoverImage"))
                    {
                        if (firstArrayItem.ValueKind == JsonValueKind.String)
                        {
                            mappings.Add(new FieldMapping
                            {
                                SourcePath = $"{propPath}[0]",
                                TargetField = "CoverImage",
                                Transform = TransformType.None
                            });
                            detectedFields.Add("CoverImage");
                        }
                        else if (firstArrayItem.ValueKind == JsonValueKind.Object)
                        {
                            // Look for url/src in the object
                            foreach (var urlProp in new[] { "url", "src", "t", "w", "s" })
                            {
                                if (firstArrayItem.TryGetProperty(urlProp, out _))
                                {
                                    mappings.Add(new FieldMapping
                                    {
                                        SourcePath = $"{propPath}[0].{urlProp}",
                                        TargetField = "CoverImage",
                                        Transform = TransformType.None
                                    });
                                    detectedFields.Add("CoverImage");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Ensure we have at least Id and Title for search results
        if (purpose == "Search" && !detectedFields.Contains("Id"))
        {
            // Look for any numeric or string field that could be an ID
            foreach (var property in obj.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number || 
                    (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()?.Length < 50))
                {
                    mappings.Insert(0, CreateMapping($"{basePath}.{property.Name}", "Id", property.Value));
                    break;
                }
            }
        }

        return mappings;
    }

    private List<FieldMapping> AnalyzeNestedForImages(JsonElement obj, string basePath)
    {
        var mappings = new List<FieldMapping>();
        
        foreach (var property in obj.EnumerateObject())
        {
            var propName = property.Name.ToLowerInvariant();
            
            if (propName.Contains("cover") || propName.Contains("thumb") || propName.Contains("poster") || 
                propName.Contains("image") || propName.Contains("large") || propName.Contains("medium"))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var strValue = property.Value.GetString() ?? "";
                    if (ImageUrlPattern.IsMatch(strValue) || strValue.StartsWith("http"))
                    {
                        mappings.Add(new FieldMapping
                        {
                            SourcePath = $"{basePath}.{property.Name}",
                            TargetField = "CoverImage",
                            Transform = TransformType.None
                        });
                        break;
                    }
                }
            }
        }
        
        return mappings;
    }

    private static FieldMapping CreateMapping(string sourcePath, string targetField, JsonElement value)
    {
        // Convert JSONPath from $. format to $.property format
        var jsonPath = sourcePath.Replace("[*].", "[*].");
        
        return new FieldMapping
        {
            SourcePath = jsonPath,
            TargetField = targetField,
            Transform = TransformType.None
        };
    }

    private static float CalculateConfidence(List<FieldMapping> mappings, string purpose)
    {
        var requiredFields = purpose switch
        {
            "Search" => new[] { "Id", "Title" },
            "Episodes" or "Chapters" => new[] { "Id", "Number" },
            "Streams" or "Pages" => new[] { "Url" },
            _ => new[] { "Id" }
        };

        var foundRequired = requiredFields.Count(r => mappings.Any(m => m.TargetField == r));
        var baseConfidence = (float)foundRequired / requiredFields.Length;
        
        // Bonus for optional fields
        var optionalFields = new[] { "Synopsis", "CoverImage", "Title", "Quality" };
        var foundOptional = optionalFields.Count(o => mappings.Any(m => m.TargetField == o));
        var optionalBonus = foundOptional * 0.1f;
        
        return Math.Min(1f, baseConfidence * 0.7f + optionalBonus + 0.1f);
    }

    private string BuildSearchUrl(string baseUrl, string endpoint, string queryTemplate, string query)
    {
        var template = queryTemplate
            .Replace("${query}", Uri.EscapeDataString(query))
            .Replace("${search}", Uri.EscapeDataString(query))
            .Replace("$(query)", Uri.EscapeDataString(query))
            .Replace("$query", Uri.EscapeDataString(query));
        
        var fullEndpoint = endpoint.StartsWith("/") ? endpoint : "/" + endpoint;
        return $"{baseUrl.TrimEnd('/')}{fullEndpoint}{template}";
    }

    private async Task<string?> FetchJsonAsync(string url, Dictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
            
            request.Headers.Accept.ParseAdd("application/json");
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            
            var response = await _httpClient.SendAsync(request, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Request to {Url} returned {Status}", url, response.StatusCode);
                return null;
            }
            
            var content = await response.Content.ReadAsStringAsync(cts.Token);
            
            // Validate it's JSON
            if (!content.TrimStart().StartsWith("{") && !content.TrimStart().StartsWith("["))
            {
                _logger.LogDebug("Response from {Url} is not JSON", url);
                return null;
            }
            
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch {Url}", url);
            return null;
        }
    }
}

/// <summary>
/// Interface for response analysis.
/// </summary>
public interface IResponseAnalyzer
{
    Task<ResponseAnalysisResult> AnalyzeSearchEndpointAsync(
        string baseUrl,
        string searchEndpoint,
        string queryTemplate,
        Dictionary<string, string> headers,
        CancellationToken ct = default);

    Task<ResponseAnalysisResult> AnalyzeEndpointAsync(
        string url,
        Dictionary<string, string> headers,
        string purpose,
        CancellationToken ct = default);

    JsonAnalysisResult AnalyzeJsonStructure(string json, string purpose);
}

/// <summary>
/// Result of analyzing an API response.
/// </summary>
public sealed class ResponseAnalysisResult
{
    public List<FieldMapping> SearchMappings { get; set; } = new();
    public string? ResultsPath { get; set; }
    public string? SampleResponse { get; set; }
    public string? SuccessfulQuery { get; set; }
    public float Confidence { get; set; }
}

/// <summary>
/// Result of analyzing JSON structure.
/// </summary>
public sealed class JsonAnalysisResult
{
    public List<FieldMapping> Mappings { get; set; } = new();
    public string? ArrayPath { get; set; }
    public float Confidence { get; set; }
}
