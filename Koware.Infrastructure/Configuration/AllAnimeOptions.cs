namespace Koware.Infrastructure.Configuration;

public sealed class AllAnimeOptions
{
    public string BaseHost { get; set; } = "allanime.day";

    public string ApiBase { get; set; } = "https://api.allanime.day";

    public string Referer { get; set; } = "https://allmanga.to";

    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0";

    /// <summary>
    /// Either "sub" or "dub" (as used by the provider).
    /// </summary>
    public string TranslationType { get; set; } = "sub";

    /// <summary>
    /// Maximum search results returned.
    /// </summary>
    public int SearchLimit { get; set; } = 20;
}
