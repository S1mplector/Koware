// Author: Ilgaz Mehmetoğlu
// Configuration options for the 9anime/aniwatch-style provider.
namespace Koware.Infrastructure.Configuration;

public class NineAnimeOptions
{
    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Site base URL (for example: https://aniwatchtv.to).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// HTTP Referer header value. Defaults to BaseUrl when not set.
    /// </summary>
    public string? Referer { get; set; }

    /// <summary>
    /// User-Agent string for requests.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0";

    /// <summary>
    /// Preferred video server label (for example: hd-1).
    /// </summary>
    public string PreferredServer { get; set; } = "hd-1";

    /// <summary>
    /// Maximum search results returned.
    /// </summary>
    public int SearchLimit { get; set; } = 20;

    /// <summary>
    /// Returns true if the source has valid configuration.
    /// </summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// Returns the effective referer, falling back to BaseUrl when unset.
    /// </summary>
    public string EffectiveReferer => string.IsNullOrWhiteSpace(Referer)
        ? (BaseUrl ?? "")
        : Referer!;
}
