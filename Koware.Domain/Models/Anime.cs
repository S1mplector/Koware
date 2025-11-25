// Author: Ilgaz Mehmetoğlu | Summary: Domain model for anime entities with immutable identifiers and episode associations.
namespace Koware.Domain.Models;

public sealed record AnimeId(string Value)
{
    public override string ToString() => Value;
}

public sealed record Anime
{
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

    public AnimeId Id { get; }

    public string Title { get; }

    public string? Synopsis { get; }

    public Uri DetailPage { get; }

    public IReadOnlyCollection<Episode> Episodes { get; init; }

    public Anime WithEpisodes(IReadOnlyCollection<Episode> episodes) => this with
    {
        Episodes = episodes ?? Array.Empty<Episode>()
    };
}
