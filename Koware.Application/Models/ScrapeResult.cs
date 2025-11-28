// Author: Ilgaz Mehmetoğlu
using Koware.Domain.Models;

namespace Koware.Application.Models;

/// <summary>
/// Result DTO carrying matches, selections, and resolved streams from scraping.
/// </summary>
/// <param name="Matches">All anime matching the search query.</param>
/// <param name="SelectedAnime">The anime selected for playback/download; null if no match.</param>
/// <param name="Episodes">All episodes for the selected anime; null if anime not selected.</param>
/// <param name="SelectedEpisode">The episode selected for playback; null if not resolved.</param>
/// <param name="Streams">Available streams for the selected episode; null if not resolved.</param>
public sealed record ScrapeResult(
    IReadOnlyCollection<Anime> Matches,
    Anime? SelectedAnime,
    IReadOnlyCollection<Episode>? Episodes,
    Episode? SelectedEpisode,
    IReadOnlyCollection<StreamLink>? Streams);
