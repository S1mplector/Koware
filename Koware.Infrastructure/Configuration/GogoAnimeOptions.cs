// Author: Ilgaz Mehmetoğlu
// Options for GogoAnime provider.
namespace Koware.Infrastructure.Configuration;

public sealed class GogoAnimeOptions
{
    public string ApiBase { get; set; } = "https://api.consumet.org";
    public string SiteBase { get; set; } = "https://gogoanime3.co";
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Koware/1.0";
}
