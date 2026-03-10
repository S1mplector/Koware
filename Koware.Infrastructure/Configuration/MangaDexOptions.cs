// Author: Ilgaz Mehmetoğlu
// Configuration options for the MangaDex provider.
namespace Koware.Infrastructure.Configuration;

public sealed class MangaDexOptions
{
    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// MangaDex API base URL.
    /// </summary>
    public string? ApiBase { get; set; } = "https://api.mangadex.org";

    /// <summary>
    /// MangaDex website base URL used for detail/chapter links.
    /// </summary>
    public string? WebBase { get; set; } = "https://mangadex.org";

    /// <summary>
    /// Cover CDN base URL.
    /// </summary>
    public string? CoverBase { get; set; } = "https://uploads.mangadex.org/covers";

    /// <summary>
    /// HTTP Referer header value.
    /// </summary>
    public string? Referer { get; set; } = "https://mangadex.org";

    /// <summary>
    /// User-Agent string for requests.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0";

    /// <summary>
    /// Preferred translated chapter language (for example: en, tr, ja).
    /// </summary>
    public string TranslatedLanguage { get; set; } = "en";

    /// <summary>
    /// Whether pornographic content should be included.
    /// </summary>
    public bool IncludeNsfw { get; set; } = false;

    /// <summary>
    /// Whether to prefer the data-saver image set.
    /// </summary>
    public bool UseDataSaver { get; set; } = false;

    /// <summary>
    /// Maximum search results returned.
    /// </summary>
    public int SearchLimit { get; set; } = 20;

    /// <summary>
    /// Maximum chapters to request for a manga (MangaDex feed pagination cap).
    /// </summary>
    public int MaxChapterCount { get; set; } = 500;

    /// <summary>
    /// Returns true if the source has valid configuration.
    /// </summary>
    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ApiBase) &&
        !string.IsNullOrWhiteSpace(WebBase) &&
        !string.IsNullOrWhiteSpace(Referer);
}
