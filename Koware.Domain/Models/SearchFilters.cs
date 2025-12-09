// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Airing/publication status for anime or manga.
/// </summary>
public enum ContentStatus
{
    /// <summary>No status filter applied.</summary>
    Any,
    /// <summary>Currently airing/publishing.</summary>
    Ongoing,
    /// <summary>Finished airing/publishing.</summary>
    Completed,
    /// <summary>Not yet released.</summary>
    Upcoming,
    /// <summary>On hiatus.</summary>
    Hiatus,
    /// <summary>Cancelled.</summary>
    Cancelled
}

/// <summary>
/// Sort order for search results.
/// </summary>
public enum SearchSort
{
    /// <summary>Default provider ordering (usually relevance).</summary>
    Default,
    /// <summary>Sort by popularity.</summary>
    Popularity,
    /// <summary>Sort by score/rating.</summary>
    Score,
    /// <summary>Sort by most recent updates.</summary>
    Recent,
    /// <summary>Sort alphabetically by title.</summary>
    Title
}

/// <summary>
/// Common genres for anime and manga.
/// </summary>
public static class KnownGenres
{
    public const string Action = "Action";
    public const string Adventure = "Adventure";
    public const string Comedy = "Comedy";
    public const string Drama = "Drama";
    public const string Fantasy = "Fantasy";
    public const string Horror = "Horror";
    public const string Isekai = "Isekai";
    public const string Mecha = "Mecha";
    public const string Music = "Music";
    public const string Mystery = "Mystery";
    public const string Psychological = "Psychological";
    public const string Romance = "Romance";
    public const string SciFi = "Sci-Fi";
    public const string SliceOfLife = "Slice of Life";
    public const string Sports = "Sports";
    public const string Supernatural = "Supernatural";
    public const string Thriller = "Thriller";

    /// <summary>
    /// Get all known genres.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Action, Adventure, Comedy, Drama, Fantasy, Horror, Isekai, Mecha,
        Music, Mystery, Psychological, Romance, SciFi, SliceOfLife, Sports,
        Supernatural, Thriller
    };

    /// <summary>
    /// Try to match a user input to a known genre (case-insensitive, partial match).
    /// </summary>
    public static string? TryMatch(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var normalized = input.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
        return All.FirstOrDefault(g => 
            g.Replace("-", "").Replace(" ", "").ToLowerInvariant() == normalized ||
            g.ToLowerInvariant().StartsWith(input.ToLowerInvariant()));
    }
}

/// <summary>
/// Search filters for anime/manga queries.
/// All properties are optional - providers will ignore unsupported filters.
/// </summary>
public sealed record SearchFilters
{
    /// <summary>
    /// Genres to include (e.g., "Action", "Romance"). Provider may support multiple.
    /// </summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>
    /// Release/publication year filter.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Airing/publication status filter.
    /// </summary>
    public ContentStatus Status { get; init; } = ContentStatus.Any;

    /// <summary>
    /// Minimum score filter (1-10 scale).
    /// </summary>
    public int? MinScore { get; init; }

    /// <summary>
    /// Sort order for results.
    /// </summary>
    public SearchSort Sort { get; init; } = SearchSort.Default;

    /// <summary>
    /// Country of origin filter (e.g., "JP" for Japan, "KR" for Korea, "CN" for China).
    /// </summary>
    public string? CountryOrigin { get; init; }

    /// <summary>
    /// Returns true if any filter is applied.
    /// </summary>
    public bool HasFilters => 
        Genres?.Count > 0 || 
        Year.HasValue || 
        Status != ContentStatus.Any || 
        MinScore.HasValue ||
        Sort != SearchSort.Default ||
        !string.IsNullOrWhiteSpace(CountryOrigin);

    /// <summary>
    /// Create an empty filter set.
    /// </summary>
    public static SearchFilters Empty { get; } = new();

    /// <summary>
    /// Create filters from CLI arguments.
    /// </summary>
    public static SearchFilters Parse(string[] args)
    {
        var filters = new SearchFilters();
        var genres = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--genre", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var genreInput = args[++i];
                var matched = KnownGenres.TryMatch(genreInput);
                if (matched != null) genres.Add(matched);
            }
            else if (arg.Equals("--year", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var year) && year >= 1900 && year <= 2100)
                {
                    filters = filters with { Year = year };
                }
            }
            else if (arg.Equals("--status", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var statusStr = args[++i].ToLowerInvariant();
                var status = statusStr switch
                {
                    "ongoing" or "airing" or "releasing" => ContentStatus.Ongoing,
                    "completed" or "finished" => ContentStatus.Completed,
                    "upcoming" or "notyet" => ContentStatus.Upcoming,
                    "hiatus" => ContentStatus.Hiatus,
                    "cancelled" or "canceled" => ContentStatus.Cancelled,
                    _ => ContentStatus.Any
                };
                filters = filters with { Status = status };
            }
            else if (arg.Equals("--score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var score) && score >= 1 && score <= 10)
                {
                    filters = filters with { MinScore = score };
                }
            }
            else if (arg.Equals("--sort", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var sortStr = args[++i].ToLowerInvariant();
                var sort = sortStr switch
                {
                    "popular" or "popularity" => SearchSort.Popularity,
                    "score" or "rating" => SearchSort.Score,
                    "recent" or "new" or "latest" => SearchSort.Recent,
                    "title" or "alphabetical" => SearchSort.Title,
                    _ => SearchSort.Default
                };
                filters = filters with { Sort = sort };
            }
            else if (arg.Equals("--country", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                filters = filters with { CountryOrigin = args[++i].ToUpperInvariant() };
            }
        }

        if (genres.Count > 0)
        {
            filters = filters with { Genres = genres };
        }

        return filters;
    }
}
