// Author: Ilgaz Mehmetoğlu
using System.Text.Json.Serialization;

namespace Koware.Autoconfig.Models;

/// <summary>
/// Complete configuration for a dynamically configured provider.
/// This is the portable format that can be saved, shared, and loaded.
/// </summary>
public sealed record DynamicProviderConfig
{
    /// <summary>User-friendly display name.</summary>
    public required string Name { get; init; }
    
    /// <summary>Internal slug identifier (lowercase, no spaces).</summary>
    public required string Slug { get; init; }
    
    /// <summary>Type of content this provider supports.</summary>
    public ProviderType Type { get; init; } = ProviderType.Anime;
    
    /// <summary>Configuration version for migration support.</summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>When this configuration was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    
    /// <summary>When this configuration was last validated.</summary>
    public DateTimeOffset? LastValidatedAt { get; init; }
    
    /// <summary>Host and network configuration.</summary>
    public required HostConfig Hosts { get; init; }
    
    /// <summary>Authentication configuration if required.</summary>
    public AuthConfig? Auth { get; init; }
    
    /// <summary>Search configuration.</summary>
    public required SearchConfig Search { get; init; }
    
    /// <summary>Content fetching configuration.</summary>
    public required ContentConfig Content { get; init; }
    
    /// <summary>Media URL resolution configuration.</summary>
    public required MediaConfig Media { get; init; }
    
    /// <summary>Custom transform rules.</summary>
    public IReadOnlyList<TransformRule> Transforms { get; init; } = [];
    
    /// <summary>Rate limiting configuration.</summary>
    public RateLimitConfig? RateLimit { get; init; }
    
    /// <summary>Notes about this provider.</summary>
    public string? Notes { get; init; }
    
    /// <summary>Whether this is a built-in provider.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; init; }
}

/// <summary>
/// Host and network configuration.
/// </summary>
public sealed record HostConfig
{
    /// <summary>Base hostname (without protocol).</summary>
    public required string BaseHost { get; init; }
    
    /// <summary>API base URL (full URL with protocol).</summary>
    public string? ApiBase { get; init; }
    
    /// <summary>Referer header value.</summary>
    public required string Referer { get; init; }
    
    /// <summary>User-Agent string.</summary>
    public string UserAgent { get; init; } = 
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0";
    
    /// <summary>Additional custom headers.</summary>
    public IReadOnlyDictionary<string, string> CustomHeaders { get; init; } = 
        new Dictionary<string, string>();
}

/// <summary>
/// Authentication configuration.
/// </summary>
public sealed record AuthConfig
{
    /// <summary>Type of authentication.</summary>
    public AuthType Type { get; init; }
    
    /// <summary>Header name for API key auth.</summary>
    public string? HeaderName { get; init; }
    
    /// <summary>Cookie names required.</summary>
    public IReadOnlyList<string> RequiredCookies { get; init; } = [];
}

/// <summary>
/// Type of authentication required.
/// </summary>
public enum AuthType
{
    /// <summary>No authentication required.</summary>
    None,
    
    /// <summary>API key in header.</summary>
    ApiKey,
    
    /// <summary>Cookie-based authentication.</summary>
    Cookie,
    
    /// <summary>Cloudflare challenge solving required.</summary>
    Cloudflare
}

/// <summary>
/// Search operation configuration.
/// </summary>
public sealed record SearchConfig
{
    /// <summary>Method used for search.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint URL or path.</summary>
    public required string Endpoint { get; init; }
    
    /// <summary>Query template with placeholders.</summary>
    public required string QueryTemplate { get; init; }
    
    /// <summary>Field mappings for result extraction.</summary>
    public IReadOnlyList<FieldMapping> ResultMapping { get; init; } = [];
    
    /// <summary>Maximum results per page.</summary>
    public int PageSize { get; init; } = 20;
}

/// <summary>
/// Content fetching configuration.
/// </summary>
public sealed record ContentConfig
{
    /// <summary>Episode fetching configuration (for anime).</summary>
    public EndpointConfig? Episodes { get; init; }
    
    /// <summary>Chapter fetching configuration (for manga).</summary>
    public EndpointConfig? Chapters { get; init; }
    
    /// <summary>Details page configuration.</summary>
    public EndpointConfig? Details { get; init; }
}

/// <summary>
/// Configuration for a single endpoint.
/// </summary>
public sealed record EndpointConfig
{
    /// <summary>Method used.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint URL or path.</summary>
    public required string Endpoint { get; init; }
    
    /// <summary>Query template with placeholders.</summary>
    public required string QueryTemplate { get; init; }
    
    /// <summary>Field mappings for extraction.</summary>
    public IReadOnlyList<FieldMapping> ResultMapping { get; init; } = [];
}

/// <summary>
/// Media URL resolution configuration.
/// </summary>
public sealed record MediaConfig
{
    /// <summary>Stream URL configuration (for anime).</summary>
    public StreamConfig? Streams { get; init; }
    
    /// <summary>Page image configuration (for manga).</summary>
    public PageConfig? Pages { get; init; }
}

/// <summary>
/// Stream URL resolution configuration.
/// </summary>
public sealed record StreamConfig
{
    /// <summary>Method used.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint URL or path.</summary>
    public required string Endpoint { get; init; }
    
    /// <summary>Query template.</summary>
    public required string QueryTemplate { get; init; }
    
    /// <summary>Field mappings.</summary>
    public IReadOnlyList<FieldMapping> ResultMapping { get; init; } = [];
    
    /// <summary>Custom decoder name if URLs need decoding.</summary>
    public string? CustomDecoder { get; init; }
}

/// <summary>
/// Page image resolution configuration.
/// </summary>
public sealed record PageConfig
{
    /// <summary>Method used.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint URL or path.</summary>
    public required string Endpoint { get; init; }
    
    /// <summary>Query template.</summary>
    public required string QueryTemplate { get; init; }
    
    /// <summary>Field mappings.</summary>
    public IReadOnlyList<FieldMapping> ResultMapping { get; init; } = [];
    
    /// <summary>Base URL for image CDN.</summary>
    public string? ImageBaseUrl { get; init; }
}

/// <summary>
/// Custom transform rule.
/// </summary>
public sealed record TransformRule
{
    /// <summary>Name of the transform.</summary>
    public required string Name { get; init; }
    
    /// <summary>Type of transform.</summary>
    public TransformType Type { get; init; }
    
    /// <summary>Regex pattern for RegexExtract.</summary>
    public string? Pattern { get; init; }
    
    /// <summary>Replacement string for regex.</summary>
    public string? Replacement { get; init; }
    
    /// <summary>Custom decoder class name.</summary>
    public string? DecoderClass { get; init; }
}

/// <summary>
/// Rate limiting configuration.
/// </summary>
public sealed record RateLimitConfig
{
    /// <summary>Maximum requests per minute.</summary>
    public int RequestsPerMinute { get; init; } = 60;
    
    /// <summary>Default retry delay.</summary>
    public TimeSpan RetryAfterDefault { get; init; } = TimeSpan.FromSeconds(5);
    
    /// <summary>Use exponential backoff.</summary>
    public bool UseExponentialBackoff { get; init; } = true;
}
