// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Strongly-typed identifier for a chapter entity.
/// </summary>
/// <param name="Value">The underlying ID string from the provider.</param>
public sealed record ChapterId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Domain model representing a manga chapter with numbering, title, and page URL.
/// </summary>
public sealed record Chapter
{
    /// <summary>
    /// Create a new chapter instance.
    /// </summary>
    /// <param name="id">Unique identifier for this chapter.</param>
    /// <param name="title">Chapter title; defaults to "Chapter N" if empty.</param>
    /// <param name="number">Chapter number (must be > 0).</param>
    /// <param name="pageUrl">URI to the chapter page on the provider site.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if number is zero or negative.</exception>
    /// <exception cref="ArgumentNullException">Thrown if id or pageUrl is null.</exception>
    public Chapter(ChapterId id, string title, float number, Uri pageUrl)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "Chapter number must be greater than zero");
        }

        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = string.IsNullOrWhiteSpace(title) ? $"Chapter {number}" : title.Trim();
        Number = number;
        PageUrl = pageUrl ?? throw new ArgumentNullException(nameof(pageUrl));
    }

    /// <summary>Unique identifier for this chapter.</summary>
    public ChapterId Id { get; }

    /// <summary>Chapter title or default "Chapter N".</summary>
    public string Title { get; }

    /// <summary>Chapter number (can be decimal for sub-chapters like 10.5).</summary>
    public float Number { get; }

    /// <summary>URI to the chapter page on the provider site.</summary>
    public Uri PageUrl { get; }
}
