// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Strongly-typed identifier for a manga entity.
/// </summary>
/// <param name="Value">The underlying ID string (typically from a provider).</param>
public sealed record MangaId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Domain model representing a manga with metadata and chapters.
/// </summary>
public sealed record Manga
{
    /// <summary>
    /// Create a new manga instance.
    /// </summary>
    /// <param name="id">Unique identifier for this manga.</param>
    /// <param name="title">Display title (must not be empty).</param>
    /// <param name="synopsis">Optional synopsis/description.</param>
    /// <param name="coverImage">Optional cover image URL.</param>
    /// <param name="detailPage">URI to the detail page on the provider site.</param>
    /// <param name="chapters">Collection of chapters; can be empty initially.</param>
    /// <exception cref="ArgumentException">Thrown if title is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown if id or detailPage is null.</exception>
    public Manga(MangaId id, string title, string? synopsis, Uri? coverImage, Uri detailPage, IReadOnlyCollection<Chapter> chapters)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title is required", nameof(title));
        }

        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title.Trim();
        Synopsis = synopsis;
        CoverImage = coverImage;
        DetailPage = detailPage ?? throw new ArgumentNullException(nameof(detailPage));
        Chapters = chapters ?? Array.Empty<Chapter>();
    }

    /// <summary>Unique identifier for this manga.</summary>
    public MangaId Id { get; }

    /// <summary>Display title of the manga.</summary>
    public string Title { get; }

    /// <summary>Optional synopsis or description.</summary>
    public string? Synopsis { get; }

    /// <summary>Optional cover image URL.</summary>
    public Uri? CoverImage { get; }

    /// <summary>URI to the detail page on the provider site.</summary>
    public Uri DetailPage { get; }

    /// <summary>Collection of chapters for this manga.</summary>
    public IReadOnlyCollection<Chapter> Chapters { get; init; }

    /// <summary>
    /// Return a copy of this manga with a different chapter list.
    /// </summary>
    /// <param name="chapters">New chapters collection.</param>
    /// <returns>New Manga instance with updated chapters.</returns>
    public Manga WithChapters(IReadOnlyCollection<Chapter> chapters) => this with
    {
        Chapters = chapters ?? Array.Empty<Chapter>()
    };
}
