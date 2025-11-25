// Author: Ilgaz Mehmetoğlu
// Result DTO carrying matches, selections, and resolved streams from scraping.
using Koware.Domain.Models;

namespace Koware.Application.Models;

public sealed record ScrapeResult(
    IReadOnlyCollection<Anime> Matches,
    Anime? SelectedAnime,
    IReadOnlyCollection<Episode>? Episodes,
    Episode? SelectedEpisode,
    IReadOnlyCollection<StreamLink>? Streams);
