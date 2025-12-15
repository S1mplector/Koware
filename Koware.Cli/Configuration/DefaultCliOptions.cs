// Author: Ilgaz Mehmetoğlu
namespace Koware.Cli.Configuration;

/// <summary>
/// CLI mode: anime or manga.
/// </summary>
public enum CliMode
{
    Anime,
    Manga
}

/// <summary>
/// Configuration model for CLI defaults such as quality, preferred match index, and mode.
/// Bound from the "Defaults" section in appsettings.json or appsettings.user.json.
/// </summary>
public sealed class DefaultCliOptions
{
    /// <summary>Default quality label (e.g., "1080p", "720p").</summary>
    public string? Quality { get; set; }

    /// <summary>Default match index (1-based) to use when multiple results found.</summary>
    public int? PreferredMatchIndex { get; set; }

    /// <summary>Current CLI mode: "anime" or "manga".</summary>
    public string Mode { get; set; } = "anime";

    /// <summary>Default download directory for anime episodes.</summary>
    public string? AnimeDownloadPath { get; set; }

    /// <summary>Default download directory for manga chapters.</summary>
    public string? MangaDownloadPath { get; set; }

    /// <summary>Parse the Mode string into a CliMode enum.</summary>
    public CliMode GetMode() => Mode?.Equals("manga", StringComparison.OrdinalIgnoreCase) == true
        ? CliMode.Manga
        : CliMode.Anime;

    /// <summary>Get the effective anime download path, defaulting to current directory.</summary>
    public string GetAnimeDownloadPath() => 
        string.IsNullOrWhiteSpace(AnimeDownloadPath) ? Environment.CurrentDirectory : AnimeDownloadPath;

    /// <summary>Get the effective manga download path, defaulting to current directory.</summary>
    public string GetMangaDownloadPath() => 
        string.IsNullOrWhiteSpace(MangaDownloadPath) ? Environment.CurrentDirectory : MangaDownloadPath;
}
