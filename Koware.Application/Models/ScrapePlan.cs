// Author: Ilgaz Mehmetoğlu
// Immutable plan describing query, episode, quality, and selection preferences.
namespace Koware.Application.Models;

public sealed record ScrapePlan(
    string Query,
    int? EpisodeNumber = null,
    string? PreferredQuality = null,
    int? PreferredMatchIndex = null,
    bool NonInteractive = false);
