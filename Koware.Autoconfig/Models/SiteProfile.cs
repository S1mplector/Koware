// Author: Ilgaz Mehmetoğlu
namespace Koware.Autoconfig.Models;

/// <summary>
/// Profile of a website gathered during initial probing phase.
/// </summary>
public sealed record SiteProfile
{
    /// <summary>Base URL of the site.</summary>
    public required Uri BaseUrl { get; init; }
    
    /// <summary>Detected site architecture type.</summary>
    public SiteType Type { get; init; } = SiteType.Unknown;
    
    /// <summary>Detected content category.</summary>
    public ContentCategory Category { get; init; } = ContentCategory.Unknown;
    
    /// <summary>Whether JavaScript is required to render content.</summary>
    public bool RequiresJavaScript { get; init; }
    
    /// <summary>Whether Cloudflare protection was detected.</summary>
    public bool HasCloudflareProtection { get; init; }
    
    /// <summary>Whether the site appears to use a GraphQL API.</summary>
    public bool HasGraphQL { get; init; }
    
    /// <summary>Server software detected from headers.</summary>
    public string? ServerSoftware { get; init; }
    
    /// <summary>JavaScript framework detected (React, Vue, etc.).</summary>
    public string? JsFramework { get; init; }
    
    /// <summary>Potential API endpoints discovered.</summary>
    public IReadOnlyList<string> DetectedApiEndpoints { get; init; } = [];
    
    /// <summary>CDN hosts detected for media delivery.</summary>
    public IReadOnlyList<string> DetectedCdnHosts { get; init; } = [];
    
    /// <summary>Required HTTP headers for requests.</summary>
    public IReadOnlyDictionary<string, string> RequiredHeaders { get; init; } = 
        new Dictionary<string, string>();
    
    /// <summary>Site title from page metadata.</summary>
    public string? SiteTitle { get; init; }
    
    /// <summary>Site description from page metadata.</summary>
    public string? SiteDescription { get; init; }
    
    /// <summary>Robots.txt contents if available.</summary>
    public string? RobotsTxt { get; init; }
    
    /// <summary>Any errors encountered during probing.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    
    /// <summary>Pre-configured knowledge about this site type if recognized.</summary>
    public SiteKnowledge? KnownSiteInfo { get; init; }
}

/// <summary>
/// Pre-configured knowledge about known site types for improved detection.
/// </summary>
public sealed record SiteKnowledge
{
    /// <summary>Expected content category for this site type.</summary>
    public ContentCategory Category { get; init; } = ContentCategory.Unknown;
    
    /// <summary>Known API path patterns for this site.</summary>
    public string[] ApiPatterns { get; init; } = [];
    
    /// <summary>API base URL if different from main site.</summary>
    public string? ApiBase { get; init; }
    
    /// <summary>Known search endpoint template. Use {query} as placeholder.</summary>
    public string? SearchEndpoint { get; init; }
    
    /// <summary>HTTP method for search (GET or POST). Default is GET.</summary>
    public string? SearchMethod { get; init; }
    
    /// <summary>JSON body template for POST search requests. Use {query} as placeholder.</summary>
    public string? SearchBodyTemplate { get; init; }
    
    /// <summary>JSONPath to the results array in search responses.</summary>
    public string? ResultsPath { get; init; }
    
    /// <summary>Regex pattern to extract content IDs from URLs.</summary>
    public string? IdPattern { get; init; }
    
    /// <summary>Known content detail endpoint template. Use {id} as placeholder.</summary>
    public string? ContentEndpoint { get; init; }
    
    /// <summary>Known episodes/chapters endpoint template.</summary>
    public string? EpisodesEndpoint { get; init; }
    
    /// <summary>Known streams/pages endpoint template.</summary>
    public string? StreamsEndpoint { get; init; }
    
    /// <summary>Expected field mappings for this site type.</summary>
    public Dictionary<string, string>? FieldMappings { get; init; }
}
