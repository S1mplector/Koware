using System;

namespace Koware.Cli.History;

internal sealed record AnimeProgressSnapshot(
    int EpisodesWatched,
    int? TotalEpisodes,
    AnimeWatchStatus Status,
    DateTimeOffset? CompletedAt);

internal sealed record MangaProgressSnapshot(
    int ChaptersRead,
    int? TotalChapters,
    MangaReadStatus Status,
    DateTimeOffset? CompletedAt);

internal static class ListProgressTracker
{
    private const float ChapterEpsilon = 0.001f;

    internal static float ResolveRecordedChapterNumber(float selectedChapterNumber, float finalChapterNumber)
    {
        return finalChapterNumber > 0 ? finalChapterNumber : selectedChapterNumber;
    }

    internal static AnimeProgressSnapshot ComputeAnimeUpdate(
        AnimeListEntry? existing,
        int episodeNumber,
        int? observedTotalEpisodes,
        DateTimeOffset now)
    {
        var totalEpisodes = MergeKnownTotal(existing?.TotalEpisodes, observedTotalEpisodes);
        var normalizedEpisode = NormalizeEpisodeProgress(episodeNumber, totalEpisodes);
        var previousProgress = existing?.EpisodesWatched ?? 0;
        var episodesWatched = Math.Max(previousProgress, normalizedEpisode);

        if (totalEpisodes.HasValue)
        {
            episodesWatched = Math.Min(episodesWatched, totalEpisodes.Value);
        }

        var status = ResolveAnimeStatus(existing, episodesWatched, totalEpisodes, previousProgress);
        var completedAt = ResolveCompletedAt(existing?.CompletedAt, existing?.Status, status, now);

        return new AnimeProgressSnapshot(episodesWatched, totalEpisodes, status, completedAt);
    }

    internal static MangaProgressSnapshot ComputeMangaUpdate(
        MangaListEntry? existing,
        float chapterNumber,
        int? observedTotalChapters,
        DateTimeOffset now)
    {
        var totalChapters = MergeKnownTotal(existing?.TotalChapters, observedTotalChapters);
        var normalizedChapter = NormalizeChapterProgress(chapterNumber, totalChapters);
        var previousProgress = existing?.ChaptersRead ?? 0;
        var chaptersRead = Math.Max(previousProgress, normalizedChapter);

        if (totalChapters.HasValue)
        {
            chaptersRead = Math.Min(chaptersRead, totalChapters.Value);
        }

        var status = ResolveMangaStatus(existing, chaptersRead, totalChapters, previousProgress);
        var completedAt = ResolveCompletedAt(existing?.CompletedAt, existing?.Status, status, now);

        return new MangaProgressSnapshot(chaptersRead, totalChapters, status, completedAt);
    }

    internal static int NormalizeEpisodeProgress(int episodeNumber, int? totalEpisodes)
    {
        var normalized = Math.Max(1, episodeNumber);
        if (totalEpisodes.HasValue && totalEpisodes.Value > 0)
        {
            normalized = Math.Min(normalized, totalEpisodes.Value);
        }

        return normalized;
    }

    internal static int NormalizeChapterProgress(float chapterNumber, int? totalChapters)
    {
        if (float.IsNaN(chapterNumber) || float.IsInfinity(chapterNumber))
        {
            chapterNumber = 1;
        }

        var normalized = (int)Math.Ceiling(chapterNumber - ChapterEpsilon);
        normalized = Math.Max(1, normalized);

        if (totalChapters.HasValue && totalChapters.Value > 0)
        {
            normalized = Math.Min(normalized, totalChapters.Value);
        }

        return normalized;
    }

    private static int? MergeKnownTotal(int? existingTotal, int? observedTotal)
    {
        existingTotal = SanitizeTotal(existingTotal);
        observedTotal = SanitizeTotal(observedTotal);

        if (existingTotal.HasValue && observedTotal.HasValue)
        {
            return Math.Max(existingTotal.Value, observedTotal.Value);
        }

        return observedTotal ?? existingTotal;
    }

    private static int? SanitizeTotal(int? total)
    {
        return total.HasValue && total.Value > 0 ? total : null;
    }

    private static AnimeWatchStatus ResolveAnimeStatus(
        AnimeListEntry? existing,
        int episodesWatched,
        int? totalEpisodes,
        int previousProgress)
    {
        if (existing is null)
        {
            return totalEpisodes.HasValue && episodesWatched >= totalEpisodes.Value
                ? AnimeWatchStatus.Completed
                : AnimeWatchStatus.Watching;
        }

        if (totalEpisodes.HasValue && episodesWatched >= totalEpisodes.Value)
        {
            return AnimeWatchStatus.Completed;
        }

        var progressed = episodesWatched > previousProgress;
        if (progressed && existing.Status is AnimeWatchStatus.PlanToWatch or AnimeWatchStatus.OnHold or AnimeWatchStatus.Dropped)
        {
            return AnimeWatchStatus.Watching;
        }

        if (existing.Status == AnimeWatchStatus.Completed && totalEpisodes.HasValue && episodesWatched < totalEpisodes.Value)
        {
            return AnimeWatchStatus.Watching;
        }

        return existing.Status;
    }

    private static MangaReadStatus ResolveMangaStatus(
        MangaListEntry? existing,
        int chaptersRead,
        int? totalChapters,
        int previousProgress)
    {
        if (existing is null)
        {
            return totalChapters.HasValue && chaptersRead >= totalChapters.Value
                ? MangaReadStatus.Completed
                : MangaReadStatus.Reading;
        }

        if (totalChapters.HasValue && chaptersRead >= totalChapters.Value)
        {
            return MangaReadStatus.Completed;
        }

        var progressed = chaptersRead > previousProgress;
        if (progressed && existing.Status is MangaReadStatus.PlanToRead or MangaReadStatus.OnHold or MangaReadStatus.Dropped)
        {
            return MangaReadStatus.Reading;
        }

        if (existing.Status == MangaReadStatus.Completed && totalChapters.HasValue && chaptersRead < totalChapters.Value)
        {
            return MangaReadStatus.Reading;
        }

        return existing.Status;
    }

    private static DateTimeOffset? ResolveCompletedAt(
        DateTimeOffset? existingCompletedAt,
        AnimeWatchStatus? previousStatus,
        AnimeWatchStatus nextStatus,
        DateTimeOffset now)
    {
        if (nextStatus != AnimeWatchStatus.Completed)
        {
            return null;
        }

        var wasCompleted = previousStatus == AnimeWatchStatus.Completed;
        return wasCompleted ? existingCompletedAt ?? now : now;
    }

    private static DateTimeOffset? ResolveCompletedAt(
        DateTimeOffset? existingCompletedAt,
        MangaReadStatus? previousStatus,
        MangaReadStatus nextStatus,
        DateTimeOffset now)
    {
        if (nextStatus != MangaReadStatus.Completed)
        {
            return null;
        }

        var wasCompleted = previousStatus == MangaReadStatus.Completed;
        return wasCompleted ? existingCompletedAt ?? now : now;
    }
}
