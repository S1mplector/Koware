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
}
