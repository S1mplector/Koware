// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Strongly-typed identifier for an episode entity.
/// </summary>
/// <param name="Value">The underlying ID string from the provider.</param>
public sealed record EpisodeId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Domain model representing an episode with numbering, title, and page URL.
/// </summary>
public sealed record Episode
{
    /// <summary>
    /// Create a new episode instance.
    /// </summary>
    /// <param name="id">Unique identifier for this episode.</param>
    /// <param name="title">Episode title; defaults to "Episode N" if empty.</param>
    /// <param name="number">Episode number (must be > 0).</param>
    /// <param name="pageUrl">URI to the episode page on the provider site.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if number is zero or negative.</exception>
    /// <exception cref="ArgumentNullException">Thrown if id or pageUrl is null.</exception>
    public Episode(EpisodeId id, string title, int number, Uri pageUrl)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "Episode number must be greater than zero");
        }

        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = string.IsNullOrWhiteSpace(title) ? $"Episode {number}" : title.Trim();
        Number = number;
        PageUrl = pageUrl ?? throw new ArgumentNullException(nameof(pageUrl));
    }

    /// <summary>Unique identifier for this episode.</summary>
    public EpisodeId Id { get; }

    /// <summary>Episode title or default "Episode N".</summary>
    public string Title { get; }

    /// <summary>Episode number (1-indexed).</summary>
    public int Number { get; }

    /// <summary>URI to the episode page on the provider site.</summary>
    public Uri PageUrl { get; }
}
