// Author: Ilgaz Mehmetoğlu
namespace Koware.Application.Models;

/// <summary>
/// Immutable plan describing query, episode, quality, and selection preferences.
/// </summary>
/// <param name="Query">Search query for anime title.</param>
/// <param name="EpisodeNumber">Specific episode number to resolve; null for first available.</param>
/// <param name="PreferredQuality">Quality label preference (e.g., "1080p").</param>
/// <param name="PreferredMatchIndex">1-based index of preferred match from search results.</param>
/// <param name="NonInteractive">If true, skip interactive prompts and use defaults.</param>
public sealed record ScrapePlan(
    string Query,
    int? EpisodeNumber = null,
    string? PreferredQuality = null,
    int? PreferredMatchIndex = null,
    bool NonInteractive = false);
