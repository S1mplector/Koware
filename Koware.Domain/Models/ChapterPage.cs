// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Represents a single page/image from a manga chapter.
/// </summary>
public sealed record ChapterPage
{
    /// <summary>
    /// Create a new chapter page.
    /// </summary>
    /// <param name="pageNumber">1-indexed page number.</param>
    /// <param name="imageUrl">URL to the page image.</param>
    /// <param name="referrer">Optional referrer header required to load the image.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if pageNumber is zero or negative.</exception>
    /// <exception cref="ArgumentNullException">Thrown if imageUrl is null.</exception>
    public ChapterPage(int pageNumber, Uri imageUrl, string? referrer = null)
    {
        if (pageNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be greater than zero");
        }

        PageNumber = pageNumber;
        ImageUrl = imageUrl ?? throw new ArgumentNullException(nameof(imageUrl));
        Referrer = referrer;
    }

    /// <summary>1-indexed page number within the chapter.</summary>
    public int PageNumber { get; }

    /// <summary>URL to the page image.</summary>
    public Uri ImageUrl { get; }

    /// <summary>Optional referrer header required to load the image.</summary>
    public string? Referrer { get; }
}
