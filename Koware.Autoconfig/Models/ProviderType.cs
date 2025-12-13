// Author: Ilgaz Mehmetoğlu
namespace Koware.Autoconfig.Models;

/// <summary>
/// Type of content a provider supports.
/// </summary>
public enum ProviderType
{
    /// <summary>Anime streaming provider.</summary>
    Anime,
    
    /// <summary>Manga reading provider.</summary>
    Manga,
    
    /// <summary>Provider supports both anime and manga.</summary>
    Both
}

/// <summary>
/// Type of site architecture detected.
/// </summary>
public enum SiteType
{
    /// <summary>Traditional server-rendered HTML pages.</summary>
    Static,
    
    /// <summary>Single-page application (React, Vue, Angular).</summary>
    SPA,
    
    /// <summary>Server-rendered with client-side hydration.</summary>
    Hybrid,
    
    /// <summary>Could not determine site type.</summary>
    Unknown
}

/// <summary>
/// Category of content detected on the site.
/// </summary>
public enum ContentCategory
{
    /// <summary>Site hosts anime/video content.</summary>
    Anime,
    
    /// <summary>Site hosts manga/comic content.</summary>
    Manga,
    
    /// <summary>Site hosts both anime and manga.</summary>
    Both,
    
    /// <summary>Could not determine content type.</summary>
    Unknown
}

/// <summary>
/// Type of API discovered on the site.
/// </summary>
public enum ApiType
{
    /// <summary>GraphQL API.</summary>
    GraphQL,
    
    /// <summary>REST API.</summary>
    REST,
    
    /// <summary>Custom/proprietary API format.</summary>
    Custom,
    
    /// <summary>No API, HTML scraping required.</summary>
    HtmlScrape
}

/// <summary>
/// Purpose of a discovered API endpoint.
/// </summary>
public enum EndpointPurpose
{
    /// <summary>Search for content.</summary>
    Search,
    
    /// <summary>Get content details.</summary>
    Details,
    
    /// <summary>List episodes for anime.</summary>
    Episodes,
    
    /// <summary>Get stream URLs for an episode.</summary>
    Streams,
    
    /// <summary>List chapters for manga.</summary>
    Chapters,
    
    /// <summary>Get page images for a chapter.</summary>
    Pages,
    
    /// <summary>Unknown or general purpose.</summary>
    Unknown
}

/// <summary>
/// Type of data transformation to apply.
/// </summary>
public enum TransformType
{
    /// <summary>No transformation.</summary>
    None,
    
    /// <summary>Decode Base64 encoded string.</summary>
    DecodeBase64,
    
    /// <summary>Decode hex-encoded string.</summary>
    DecodeHex,
    
    /// <summary>Parse embedded JSON.</summary>
    ParseJson,
    
    /// <summary>Extract using regex pattern.</summary>
    RegexExtract,
    
    /// <summary>URL decode string.</summary>
    UrlDecode,
    
    /// <summary>Prepend base host to relative URL.</summary>
    PrependHost,
    
    /// <summary>Custom site-specific decoder.</summary>
    Custom
}

/// <summary>
/// Method used to fetch data.
/// </summary>
public enum SearchMethod
{
    /// <summary>GraphQL query.</summary>
    GraphQL,
    
    /// <summary>REST API call.</summary>
    REST,
    
    /// <summary>HTML page scraping.</summary>
    HtmlScrape
}
