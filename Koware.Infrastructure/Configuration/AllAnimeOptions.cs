// Author: Ilgaz Mehmetoğlu
// Configuration options for the AllAnime provider (hosts, user agent, translation type).
namespace Koware.Infrastructure.Configuration;

public sealed class AllAnimeOptions
{
    /// <summary>
    /// Whether this source is enabled. Sources without configuration are disabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base hostname for the provider (user must configure).
    /// </summary>
    public string? BaseHost { get; set; }

    /// <summary>
    /// API base URL (user must configure).
    /// </summary>
    public string? ApiBase { get; set; }

    /// <summary>
    /// HTTP Referer header value (user must configure).
    /// </summary>
    public string? Referer { get; set; }

    /// <summary>
    /// User-Agent string for requests.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0";

    /// <summary>
    /// Either "sub" or "dub" (as used by the provider).
    /// </summary>
    public string TranslationType { get; set; } = "sub";

    /// <summary>
    /// Maximum search results returned.
    /// </summary>
    public int SearchLimit { get; set; } = 20;

    /// <summary>
    /// Returns true if the source has valid configuration.
    /// </summary>
    public bool IsConfigured => Enabled && 
        !string.IsNullOrWhiteSpace(BaseHost) && 
        !string.IsNullOrWhiteSpace(ApiBase) && 
        !string.IsNullOrWhiteSpace(Referer);
}
