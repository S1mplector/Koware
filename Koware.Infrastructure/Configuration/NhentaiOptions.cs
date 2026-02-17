// Author: Ilgaz Mehmetoğlu
// Configuration options for the nHentai provider.
namespace Koware.Infrastructure.Configuration;

public sealed class NhentaiOptions
{
    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Site base URL (for example: https://nhentai.net).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional explicit API base URL.
    /// Defaults to BaseUrl + /api.
    /// </summary>
    public string? ApiBase { get; set; }

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
    /// Effective API base URL.
    /// </summary>
    public string EffectiveApiBase
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ApiBase))
            {
                return ApiBase!;
            }

            var baseUrl = (BaseUrl ?? string.Empty).TrimEnd('/');
            return string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : $"{baseUrl}/api";
        }
    }

    /// <summary>
    /// Effective referer value used for requests.
    /// </summary>
    public string EffectiveReferer => string.IsNullOrWhiteSpace(Referer)
        ? (BaseUrl ?? "")
        : Referer!;
}
