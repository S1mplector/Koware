// Author: Ilgaz Mehmetoğlu
namespace Koware.Autoconfig.Models;

/// <summary>
/// Schema describing how content is structured on a website.
/// </summary>
public sealed record ContentSchema
{
    /// <summary>Pattern for searching content.</summary>
    public SearchPattern? SearchPattern { get; init; }
    
    /// <summary>Pattern for content identifiers.</summary>
    public ContentIdentifierPattern? IdPattern { get; init; }
    
    /// <summary>Pattern for episodes (anime sites).</summary>
    public EpisodePattern? EpisodePattern { get; init; }
    
    /// <summary>Pattern for chapters (manga sites).</summary>
    public ChapterPattern? ChapterPattern { get; init; }
    
    /// <summary>Pattern for media delivery.</summary>
    public MediaPattern? MediaPattern { get; init; }
    
    /// <summary>Discovered API endpoints.</summary>
    public IReadOnlyList<ApiEndpoint> Endpoints { get; init; } = [];
}

/// <summary>
/// Describes how to search for content on the site.
/// </summary>
public sealed record SearchPattern
{
    /// <summary>Method used for search.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint URL or path.</summary>
    public string? Endpoint { get; init; }
    
    /// <summary>Query template (GraphQL query or URL pattern).</summary>
    public string? QueryTemplate { get; init; }
    
    /// <summary>JSON path or CSS selector for results.</summary>
    public string? ResultsPath { get; init; }
    
    /// <summary>Field mappings for extracting data.</summary>
    public IReadOnlyList<FieldMapping> FieldMappings { get; init; } = [];
}

/// <summary>
/// Describes how content IDs are formatted.
/// </summary>
public sealed record ContentIdentifierPattern
{
    /// <summary>Regex pattern for extracting ID from URL.</summary>
    public string? UrlPattern { get; init; }
    
    /// <summary>JSON path for ID in API responses.</summary>
    public string? JsonPath { get; init; }
    
    /// <summary>Example ID value.</summary>
    public string? Example { get; init; }
}

/// <summary>
/// Describes how episodes are structured (anime).
/// </summary>
public sealed record EpisodePattern
{
    /// <summary>Method to fetch episode list.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint for fetching episodes.</summary>
    public string? Endpoint { get; init; }
    
    /// <summary>Query template.</summary>
    public string? QueryTemplate { get; init; }
    
    /// <summary>Path to episode list in response.</summary>
    public string? ListPath { get; init; }
    
    /// <summary>Field mappings for episode data.</summary>
    public IReadOnlyList<FieldMapping> FieldMappings { get; init; } = [];
}

/// <summary>
/// Describes how chapters are structured (manga).
/// </summary>
public sealed record ChapterPattern
{
    /// <summary>Method to fetch chapter list.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint for fetching chapters.</summary>
    public string? Endpoint { get; init; }
    
    /// <summary>Query template.</summary>
    public string? QueryTemplate { get; init; }
    
    /// <summary>Path to chapter list in response.</summary>
    public string? ListPath { get; init; }
    
    /// <summary>Field mappings for chapter data.</summary>
    public IReadOnlyList<FieldMapping> FieldMappings { get; init; } = [];
}

/// <summary>
/// Describes how media URLs are obtained.
/// </summary>
public sealed record MediaPattern
{
    /// <summary>Method to fetch media URLs.</summary>
    public SearchMethod Method { get; init; }
    
    /// <summary>Endpoint for fetching streams/pages.</summary>
    public string? Endpoint { get; init; }
    
    /// <summary>Query template.</summary>
    public string? QueryTemplate { get; init; }
    
    /// <summary>Path to media URLs in response.</summary>
    public string? MediaPath { get; init; }
    
    /// <summary>Whether URLs need decoding.</summary>
    public bool RequiresDecoding { get; init; }
    
    /// <summary>Name of custom decoder if needed.</summary>
    public string? CustomDecoder { get; init; }
    
    /// <summary>Field mappings for media data.</summary>
    public IReadOnlyList<FieldMapping> FieldMappings { get; init; } = [];
}

/// <summary>
/// Maps a source field to a target field with optional transformation.
/// </summary>
public sealed record FieldMapping
{
    /// <summary>JSONPath or CSS selector for source data.</summary>
    public required string SourcePath { get; init; }
    
    /// <summary>Target field name (e.g., "Title", "Id", "CoverImage").</summary>
    public required string TargetField { get; init; }
    
    /// <summary>Transformation to apply.</summary>
    public TransformType Transform { get; init; } = TransformType.None;
    
    /// <summary>Additional parameters for the transform.</summary>
    public string? TransformParams { get; init; }
}
