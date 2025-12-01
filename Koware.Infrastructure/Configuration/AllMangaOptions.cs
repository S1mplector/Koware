// Author: Ilgaz Mehmetoğlu
// Configuration options for the AllManga provider (hosts, user agent, translation type).
namespace Koware.Infrastructure.Configuration;

public sealed class AllMangaOptions
{
    public string BaseHost { get; set; } = "allmanga.to";

    public string ApiBase { get; set; } = "https://api.allanime.day";

    public string Referer { get; set; } = "https://allmanga.to";

    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0";

    /// <summary>
    /// Translation type for manga ("sub" for translated, "raw" for original language).
    /// </summary>
    public string TranslationType { get; set; } = "sub";

    /// <summary>
    /// Maximum search results returned.
    /// </summary>
    public int SearchLimit { get; set; } = 20;
}
