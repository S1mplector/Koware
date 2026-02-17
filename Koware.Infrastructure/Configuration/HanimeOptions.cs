// Author: Ilgaz Mehmetoğlu
// Configuration options for the Hanime provider.
namespace Koware.Infrastructure.Configuration;

public sealed class HanimeOptions
{
    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Site base URL (for example: https://hanime.tv).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional explicit search-index endpoint.
    /// Defaults to https://cached.freeanimehentai.net/api/v10/search_hvs.
    /// </summary>
    public string? SearchApiUrl { get; set; }

    /// <summary>
    /// HTTP Referer header value. Defaults to BaseUrl when not set.
    /// </summary>
    public string? Referer { get; set; }

    /// <summary>
    /// User-Agent string for requests.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0";

    /// <summary>
    /// Maximum search results returned.
    /// </summary>
    public int SearchLimit { get; set; } = 20;

    /// <summary>
    /// Returns true if the source has valid configuration.
    /// </summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// Effective referer value used for requests.
    /// </summary>
    public string EffectiveReferer => string.IsNullOrWhiteSpace(Referer)
        ? (BaseUrl ?? "")
        : Referer!;

    /// <summary>
    /// Effective search index endpoint.
    /// </summary>
    public string EffectiveSearchApiUrl => string.IsNullOrWhiteSpace(SearchApiUrl)
        ? "https://cached.freeanimehentai.net/api/v10/search_hvs"
        : SearchApiUrl!;
}
