// Author: Ilgaz Mehmetoğlu
// Options for GogoAnime provider (user must configure - no defaults).
namespace Koware.Infrastructure.Configuration;

public sealed class GogoAnimeOptions
{
    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// API base URL (user must configure).
    /// </summary>
    public string? ApiBase { get; set; }

    /// <summary>
    /// Site base URL (user must configure).
    /// </summary>
    public string? SiteBase { get; set; }

    /// <summary>
    /// User-Agent string for requests.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Koware/1.0";

    /// <summary>
    /// Returns true if the source has valid configuration.
    /// </summary>
    public bool IsConfigured => Enabled && 
        !string.IsNullOrWhiteSpace(ApiBase) && 
        !string.IsNullOrWhiteSpace(SiteBase);
}
