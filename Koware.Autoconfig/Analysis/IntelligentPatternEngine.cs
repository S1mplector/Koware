// Author: Ilgaz Mehmetoğlu
// Intelligent pattern recognition engine for advanced site analysis.
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Intelligent pattern recognition engine that uses heuristics and machine learning-inspired
/// approaches to identify site structures and API patterns.
/// </summary>
public interface IIntelligentPatternEngine
{
    /// <summary>
    /// Analyze a site and detect patterns using intelligent heuristics.
    /// </summary>
    Task<PatternAnalysisResult> AnalyzeAsync(SiteProfile profile, CancellationToken ct = default);
    
    /// <summary>
    /// Learn from a known working configuration to improve future detection.
    /// </summary>
    void LearnFromConfig(DynamicProviderConfig config);
    
    /// <summary>
    /// Get confidence score for a detected pattern.
    /// </summary>
    float GetPatternConfidence(string patternType, string detectedValue);
}

/// <summary>
/// Result of intelligent pattern analysis.
/// </summary>
public sealed record PatternAnalysisResult
{
    public required SiteFingerprint Fingerprint { get; init; }
    public required IReadOnlyList<DetectedPattern> Patterns { get; init; }
    public required IReadOnlyList<string> Recommendations { get; init; }
    public float OverallConfidence { get; init; }
}

/// <summary>
/// Site fingerprint for quick identification.
/// </summary>
public sealed record SiteFingerprint
{
    public required string Hash { get; init; }
    public required string Architecture { get; init; }
    public required IReadOnlyList<string> Technologies { get; init; }
    public required IReadOnlyList<string> ApiSignatures { get; init; }
}

/// <summary>
/// A detected pattern with confidence score.
/// </summary>
public sealed record DetectedPattern
{
    public required string Type { get; init; }
    public required string Value { get; init; }
    public float Confidence { get; init; }
    public string? Evidence { get; init; }
}

/// <summary>
/// Default implementation of the intelligent pattern engine.
/// </summary>
public sealed class IntelligentPatternEngine : IIntelligentPatternEngine
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<IntelligentPatternEngine> _logger;
    
    // Known site architectures and their signatures
    private static readonly Dictionary<string, SiteArchitectureSignature> KnownArchitectures = new()
    {
        ["AllAnime"] = new SiteArchitectureSignature
        {
            Indicators = ["allanime", "api.allanime", "__typename", "availableEpisodesDetail"],
            ApiType = ApiType.GraphQL,
            IdPattern = @"[a-zA-Z0-9]{24}",
            SearchQueryTemplate = "query($search: SearchInput) { shows(search: $search) { edges { _id name } } }",
            EpisodeQueryTemplate = "query($showId: String!) { show(_id: $showId) { availableEpisodesDetail } }",
            StreamQueryTemplate = "query($showId: String!, $ep: String!) { episode(showId: $showId, episodeString: $ep) { sourceUrls } }"
        },
        ["Gogoanime"] = new SiteArchitectureSignature
        {
            Indicators = ["gogoanime", "ajax/search", "category/", "vidcdn", "streaming.php"],
            ApiType = ApiType.REST,
            IdPattern = @"[a-z0-9-]+",
            SearchQueryTemplate = "/search.html?keyword=${query}",
            EpisodeQueryTemplate = "/category/${id}",
            StreamQueryTemplate = "/ajax/episode/${id}"
        },
        ["Aniwatch"] = new SiteArchitectureSignature
        {
            Indicators = ["aniwatch", "zoro", "aniwatch.to", "ajax/v2"],
            ApiType = ApiType.REST,
            IdPattern = @"[a-z0-9-]+-\d+",
            SearchQueryTemplate = "/ajax/search/suggest?keyword=${query}",
            EpisodeQueryTemplate = "/ajax/v2/episode/list/${id}",
            StreamQueryTemplate = "/ajax/v2/episode/sources?id=${id}"
        },
        ["MangaDex"] = new SiteArchitectureSignature
        {
            Indicators = ["mangadex", "api.mangadex", "chapter/", "manga/"],
            ApiType = ApiType.REST,
            IdPattern = @"[a-f0-9-]{36}",
            SearchQueryTemplate = "/manga?title=${query}",
            EpisodeQueryTemplate = "/manga/${id}/feed",
            StreamQueryTemplate = "/at-home/server/${id}"
        },
        ["GenericGraphQL"] = new SiteArchitectureSignature
        {
            Indicators = ["graphql", "__schema", "__typename"],
            ApiType = ApiType.GraphQL,
            IdPattern = @".+",
            SearchQueryTemplate = "query($q: String!) { search(query: $q) { id title } }",
            EpisodeQueryTemplate = "query($id: ID!) { anime(id: $id) { episodes } }",
            StreamQueryTemplate = "query($id: ID!) { episode(id: $id) { sources } }"
        }
    };

    // Pattern templates for common structures
    private static readonly List<PatternTemplate> PatternTemplates =
    [
        new PatternTemplate
        {
            Type = "GraphQLEndpoint",
            Patterns = [@"/graphql", @"/api/graphql", @"/gql"],
            Weight = 1.0f
        },
        new PatternTemplate
        {
            Type = "SearchEndpoint", 
            Patterns = [@"/search", @"/api/search", @"/ajax/search", @"search\?"],
            Weight = 0.9f
        },
        new PatternTemplate
        {
            Type = "EpisodeEndpoint",
            Patterns = [@"/episode", @"/episodes", @"/ajax/episode", @"episode-\d+"],
            Weight = 0.85f
        },
        new PatternTemplate
        {
            Type = "ChapterEndpoint",
            Patterns = [@"/chapter", @"/chapters", @"/ajax/chapter", @"chapter-\d+"],
            Weight = 0.85f
        },
        new PatternTemplate
        {
            Type = "StreamEndpoint",
            Patterns = [@"/source", @"/sources", @"/stream", @"/ajax/.*source", @"streaming\.php"],
            Weight = 0.8f
        },
        new PatternTemplate
        {
            Type = "CdnHost",
            Patterns = [@"cdn\.", @"static\.", @"img\.", @"media\.", @"cloudfront", @"bunnycdn"],
            Weight = 0.7f
        }
    ];

    public IntelligentPatternEngine(HttpClient httpClient, ILogger<IntelligentPatternEngine> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PatternAnalysisResult> AnalyzeAsync(SiteProfile profile, CancellationToken ct = default)
    {
        _logger.LogInformation("Running intelligent pattern analysis for {Url}", profile.BaseUrl);
        
        var patterns = new List<DetectedPattern>();
        var recommendations = new List<string>();
        
        // Step 1: Fingerprint the site
        var fingerprint = await CreateFingerprintAsync(profile, ct);
        
        // Step 2: Match against known architectures
        var (matchedArchitecture, architectureConfidence) = MatchArchitecture(profile, fingerprint);
        
        if (matchedArchitecture != null)
        {
            patterns.Add(new DetectedPattern
            {
                Type = "Architecture",
                Value = matchedArchitecture,
                Confidence = architectureConfidence,
                Evidence = $"Matched {KnownArchitectures[matchedArchitecture].Indicators.Length} indicators"
            });
            
            recommendations.Add($"Site matches '{matchedArchitecture}' architecture - using optimized templates");
        }
        
        // Step 3: Detect API patterns
        var apiPatterns = DetectApiPatterns(profile);
        patterns.AddRange(apiPatterns);
        
        // Step 4: Analyze response structures
        var structurePatterns = await AnalyzeResponseStructuresAsync(profile, ct);
        patterns.AddRange(structurePatterns);
        
        // Step 5: Detect encoding/decoding requirements
        var encodingPatterns = DetectEncodingPatterns(profile);
        patterns.AddRange(encodingPatterns);
        
        // Step 6: Generate recommendations
        recommendations.AddRange(GenerateRecommendations(patterns, profile));
        
        // Calculate overall confidence
        var overallConfidence = patterns.Count > 0 
            ? patterns.Average(p => p.Confidence) 
            : 0f;

        return new PatternAnalysisResult
        {
            Fingerprint = fingerprint,
            Patterns = patterns,
            Recommendations = recommendations,
            OverallConfidence = overallConfidence
        };
    }

    public void LearnFromConfig(DynamicProviderConfig config)
    {
        _logger.LogInformation("Learning from config '{Name}'", config.Name);
        // Store successful patterns for future reference
        // This could be persisted to improve detection over time
    }

    public float GetPatternConfidence(string patternType, string detectedValue)
    {
        var template = PatternTemplates.FirstOrDefault(t => t.Type == patternType);
        if (template == null) return 0.5f;
        
        foreach (var pattern in template.Patterns)
        {
            if (Regex.IsMatch(detectedValue, pattern, RegexOptions.IgnoreCase))
            {
                return template.Weight;
            }
        }
        
        return 0.3f;
    }

    private async Task<SiteFingerprint> CreateFingerprintAsync(SiteProfile profile, CancellationToken ct)
    {
        var technologies = new List<string>();
        var apiSignatures = new List<string>();
        
        // Detect technologies
        if (!string.IsNullOrEmpty(profile.JsFramework))
            technologies.Add(profile.JsFramework);
        if (profile.HasGraphQL)
            technologies.Add("GraphQL");
        if (profile.HasCloudflareProtection)
            technologies.Add("Cloudflare");
        if (profile.RequiresJavaScript)
            technologies.Add("SPA");
        if (!string.IsNullOrEmpty(profile.ServerSoftware))
            technologies.Add(profile.ServerSoftware);
            
        // Create API signatures from detected endpoints
        foreach (var endpoint in profile.DetectedApiEndpoints.Take(5))
        {
            var normalized = NormalizeEndpoint(endpoint);
            if (!string.IsNullOrEmpty(normalized))
                apiSignatures.Add(normalized);
        }
        
        // Generate fingerprint hash
        var hashInput = string.Join("|", technologies.Concat(apiSignatures));
        var hash = ComputeSimpleHash(hashInput);
        
        return new SiteFingerprint
        {
            Hash = hash,
            Architecture = profile.HasGraphQL ? "GraphQL-SPA" : "REST-Traditional",
            Technologies = technologies,
            ApiSignatures = apiSignatures
        };
    }

    private (string? architecture, float confidence) MatchArchitecture(SiteProfile profile, SiteFingerprint fingerprint)
    {
        string? bestMatch = null;
        var bestScore = 0f;
        
        var fullText = $"{profile.SiteTitle} {profile.SiteDescription} {string.Join(" ", profile.DetectedApiEndpoints)}".ToLowerInvariant();
        
        foreach (var (name, signature) in KnownArchitectures)
        {
            var matchCount = signature.Indicators.Count(i => fullText.Contains(i.ToLowerInvariant()));
            var score = (float)matchCount / signature.Indicators.Length;
            
            // Boost score for API type match
            if (profile.HasGraphQL && signature.ApiType == ApiType.GraphQL)
                score += 0.2f;
            else if (!profile.HasGraphQL && signature.ApiType == ApiType.REST)
                score += 0.1f;
            
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = name;
            }
        }
        
        return bestScore >= 0.3f ? (bestMatch, Math.Min(bestScore, 1f)) : (null, 0f);
    }

    private List<DetectedPattern> DetectApiPatterns(SiteProfile profile)
    {
        var patterns = new List<DetectedPattern>();
        
        foreach (var endpoint in profile.DetectedApiEndpoints)
        {
            foreach (var template in PatternTemplates)
            {
                foreach (var pattern in template.Patterns)
                {
                    if (Regex.IsMatch(endpoint, pattern, RegexOptions.IgnoreCase))
                    {
                        patterns.Add(new DetectedPattern
                        {
                            Type = template.Type,
                            Value = endpoint,
                            Confidence = template.Weight,
                            Evidence = $"Matched pattern: {pattern}"
                        });
                        break;
                    }
                }
            }
        }
        
        return patterns.DistinctBy(p => p.Type + p.Value).ToList();
    }

    private async Task<List<DetectedPattern>> AnalyzeResponseStructuresAsync(SiteProfile profile, CancellationToken ct)
    {
        var patterns = new List<DetectedPattern>();
        
        // Try to fetch and analyze a sample API response
        foreach (var endpoint in profile.DetectedApiEndpoints.Take(3))
        {
            try
            {
                var url = endpoint.StartsWith("http") 
                    ? endpoint 
                    : new Uri(profile.BaseUrl, endpoint).ToString();
                    
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0");
                request.Headers.Referrer = profile.BaseUrl;
                
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) continue;
                
                var content = await response.Content.ReadAsStringAsync(ct);
                
                // Analyze JSON structure
                var structurePatterns = AnalyzeJsonStructure(content, endpoint);
                patterns.AddRange(structurePatterns);
            }
            catch
            {
                // Continue to next endpoint
            }
        }
        
        return patterns;
    }

    private List<DetectedPattern> AnalyzeJsonStructure(string json, string endpoint)
    {
        var patterns = new List<DetectedPattern>();
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Detect GraphQL response structure
            if (root.TryGetProperty("data", out var data))
            {
                patterns.Add(new DetectedPattern
                {
                    Type = "ResponseStructure",
                    Value = "GraphQL",
                    Confidence = 0.95f,
                    Evidence = "Contains 'data' root property"
                });
                
                // Look for common patterns
                foreach (var prop in data.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("edges", out _))
                    {
                        patterns.Add(new DetectedPattern
                        {
                            Type = "PaginationStyle",
                            Value = "Relay-Connection",
                            Confidence = 0.9f,
                            Evidence = $"Found 'edges' in {prop.Name}"
                        });
                    }
                }
            }
            
            // Detect array-based responses
            if (root.ValueKind == JsonValueKind.Array)
            {
                patterns.Add(new DetectedPattern
                {
                    Type = "ResponseStructure",
                    Value = "ArrayRoot",
                    Confidence = 0.8f,
                    Evidence = "Root element is array"
                });
            }
            
            // Detect common field patterns
            var fields = ExtractFieldNames(root);
            
            if (fields.Contains("_id") || fields.Contains("id"))
            {
                patterns.Add(new DetectedPattern
                {
                    Type = "IdField",
                    Value = fields.Contains("_id") ? "_id" : "id",
                    Confidence = 0.95f
                });
            }
            
            if (fields.Any(f => f.Contains("episode", StringComparison.OrdinalIgnoreCase)))
            {
                patterns.Add(new DetectedPattern
                {
                    Type = "ContentType",
                    Value = "Anime",
                    Confidence = 0.8f,
                    Evidence = "Contains episode-related fields"
                });
            }
            
            if (fields.Any(f => f.Contains("chapter", StringComparison.OrdinalIgnoreCase)))
            {
                patterns.Add(new DetectedPattern
                {
                    Type = "ContentType",
                    Value = "Manga",
                    Confidence = 0.8f,
                    Evidence = "Contains chapter-related fields"
                });
            }
        }
        catch (JsonException)
        {
            // Not JSON, might be HTML
            patterns.Add(new DetectedPattern
            {
                Type = "ResponseFormat",
                Value = "HTML",
                Confidence = 0.6f,
                Evidence = "Response is not valid JSON"
            });
        }
        
        return patterns;
    }

    private List<DetectedPattern> DetectEncodingPatterns(SiteProfile profile)
    {
        var patterns = new List<DetectedPattern>();
        var fullContent = string.Join(" ", profile.DetectedApiEndpoints);
        
        // Check for base64-encoded URLs
        if (Regex.IsMatch(fullContent, @"[A-Za-z0-9+/]{40,}={0,2}"))
        {
            patterns.Add(new DetectedPattern
            {
                Type = "Encoding",
                Value = "Base64",
                Confidence = 0.7f,
                Evidence = "Detected Base64-like strings"
            });
        }
        
        // Check for hex-encoded content
        if (Regex.IsMatch(fullContent, @"[0-9a-fA-F]{32,}"))
        {
            patterns.Add(new DetectedPattern
            {
                Type = "Encoding",
                Value = "Hex",
                Confidence = 0.6f,
                Evidence = "Detected hex-encoded strings"
            });
        }
        
        // Check for AllAnime-style encoding
        if (Regex.IsMatch(fullContent, @"-[a-fA-F0-9]{20,}"))
        {
            patterns.Add(new DetectedPattern
            {
                Type = "Encoding",
                Value = "AllAnimeStyle",
                Confidence = 0.85f,
                Evidence = "Detected AllAnime-style encoded URLs"
            });
        }
        
        return patterns;
    }

    private List<string> GenerateRecommendations(List<DetectedPattern> patterns, SiteProfile profile)
    {
        var recommendations = new List<string>();
        
        // Check for GraphQL
        var hasGraphQL = patterns.Any(p => p.Type == "ResponseStructure" && p.Value == "GraphQL");
        if (hasGraphQL || profile.HasGraphQL)
        {
            recommendations.Add("GraphQL detected - use GraphQL queries for better performance");
        }
        
        // Check for encoding
        var encoding = patterns.FirstOrDefault(p => p.Type == "Encoding");
        if (encoding != null)
        {
            recommendations.Add($"URL encoding detected ({encoding.Value}) - custom decoder may be required");
        }
        
        // Check for Cloudflare
        if (profile.HasCloudflareProtection)
        {
            recommendations.Add("Cloudflare protection detected - requests may require browser-like headers");
        }
        
        // Check for SPA
        if (profile.RequiresJavaScript)
        {
            recommendations.Add("SPA detected - API endpoints should be used instead of HTML scraping");
        }
        
        // Check content type
        var contentType = patterns.FirstOrDefault(p => p.Type == "ContentType");
        if (contentType != null)
        {
            recommendations.Add($"Content type appears to be {contentType.Value}");
        }
        
        return recommendations;
    }

    private static HashSet<string> ExtractFieldNames(JsonElement element, int depth = 0)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (depth > 5) return fields;
        
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                fields.Add(prop.Name);
                foreach (var nested in ExtractFieldNames(prop.Value, depth + 1))
                {
                    fields.Add(nested);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
        {
            foreach (var nested in ExtractFieldNames(element[0], depth + 1))
            {
                fields.Add(nested);
            }
        }
        
        return fields;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        // Remove query parameters and normalize path
        var uri = endpoint.Split('?')[0];
        return Regex.Replace(uri, @"\d+", "{id}");
    }

    private static string ComputeSimpleHash(string input)
    {
        var hash = 0;
        foreach (var c in input)
        {
            hash = ((hash << 5) - hash) + c;
            hash &= hash;
        }
        return Math.Abs(hash).ToString("X8");
    }
}

/// <summary>
/// Signature for a known site architecture.
/// </summary>
internal sealed class SiteArchitectureSignature
{
    public required string[] Indicators { get; init; }
    public ApiType ApiType { get; init; }
    public required string IdPattern { get; init; }
    public required string SearchQueryTemplate { get; init; }
    public required string EpisodeQueryTemplate { get; init; }
    public required string StreamQueryTemplate { get; init; }
}

/// <summary>
/// Template for pattern detection.
/// </summary>
internal sealed class PatternTemplate
{
    public required string Type { get; init; }
    public required string[] Patterns { get; init; }
    public float Weight { get; init; }
}
