// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Strongly-typed identifier for an anime entity.
/// </summary>
/// <param name="Value">The underlying ID string (typically from a provider).</param>
public sealed record AnimeId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Domain model representing an anime with metadata and episodes.
/// </summary>
public sealed record Anime
{
    /// <summary>
    /// Create a new anime instance.
    /// </summary>
    /// <param name="id">Unique identifier for this anime.</param>
    /// <param name="title">Display title (must not be empty).</param>
    /// <param name="synopsis">Optional synopsis/description.</param>
    /// <param name="detailPage">URI to the detail page on the provider site.</param>
    /// <param name="episodes">Collection of episodes; can be empty initially.</param>
    /// <exception cref="ArgumentException">Thrown if title is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown if id or detailPage is null.</exception>
    public Anime(AnimeId id, string title, string? synopsis, Uri detailPage, IReadOnlyCollection<Episode> episodes)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title is required", nameof(title));
        }

        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title.Trim();
        Synopsis = synopsis;
        DetailPage = detailPage ?? throw new ArgumentNullException(nameof(detailPage));
        Episodes = episodes ?? Array.Empty<Episode>();
    }

    /// <summary>Unique identifier for this anime.</summary>
    public AnimeId Id { get; }

    /// <summary>Display title of the anime.</summary>
    public string Title { get; }

    /// <summary>Optional synopsis or description.</summary>
    public string? Synopsis { get; }

    /// <summary>URI to the detail page on the provider site.</summary>
    public Uri DetailPage { get; }

    /// <summary>Collection of episodes for this anime.</summary>
    public IReadOnlyCollection<Episode> Episodes { get; init; }

    /// <summary>
    /// Return a copy of this anime with a different episode list.
    /// </summary>
    /// <param name="episodes">New episodes collection.</param>
    /// <returns>New Anime instance with updated episodes.</returns>
    public Anime WithEpisodes(IReadOnlyCollection<Episode> episodes) => this with
    {
        Episodes = episodes ?? Array.Empty<Episode>()
    };
}
