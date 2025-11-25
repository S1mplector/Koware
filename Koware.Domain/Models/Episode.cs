// Author: Ilgaz Mehmetoğlu | Summary: Domain model representing an episode with numbering, title, and page URL.
namespace Koware.Domain.Models;

public sealed record EpisodeId(string Value)
{
    public override string ToString() => Value;
}

public sealed record Episode
{
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

    public EpisodeId Id { get; }

    public string Title { get; }

    public int Number { get; }

    public Uri PageUrl { get; }
}
