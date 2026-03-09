using System;
using Koware.Cli.History;
using Xunit;

namespace Koware.Tests;

public class ListProgressTrackerTests
{
    [Theory]
    [InlineData(AnimeWatchStatus.PlanToWatch)]
    [InlineData(AnimeWatchStatus.OnHold)]
    [InlineData(AnimeWatchStatus.Dropped)]
    public void ComputeAnimeUpdate_ResumesInactiveEntryWhenProgressAdvances(AnimeWatchStatus startingStatus)
    {
        var now = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var existing = new AnimeListEntry
        {
            Id = 1,
            AnimeId = "a1",
            AnimeTitle = "Test Anime",
            Status = startingStatus,
            TotalEpisodes = 24,
            EpisodesWatched = 3,
            AddedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-10)
        };

        var snapshot = ListProgressTracker.ComputeAnimeUpdate(existing, 4, 24, now);

        Assert.Equal(4, snapshot.EpisodesWatched);
        Assert.Equal(24, snapshot.TotalEpisodes);
        Assert.Equal(AnimeWatchStatus.Watching, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void ResolveRecordedChapterNumber_PrefersFinalNavigatedChapter()
    {
        var trackedChapter = ListProgressTracker.ResolveRecordedChapterNumber(55f, 57.5f);

        Assert.Equal(57.5f, trackedChapter);
    }

    [Fact]
    public void ComputeMangaUpdate_ClampsFractionalChapterProgressToKnownTotal()
    {
        var now = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var existing = new MangaListEntry
        {
            Id = 1,
            MangaId = "m1",
            MangaTitle = "Tokyo Ghoul:re",
            Status = MangaReadStatus.PlanToRead,
            TotalChapters = 12,
            ChaptersRead = 0,
            AddedAt = now.AddDays(-7),
            UpdatedAt = now.AddDays(-7)
        };

        var snapshot = ListProgressTracker.ComputeMangaUpdate(existing, 12.5f, 12, now);

        Assert.Equal(12, snapshot.ChaptersRead);
        Assert.Equal(12, snapshot.TotalChapters);
        Assert.Equal(MangaReadStatus.Completed, snapshot.Status);
        Assert.Equal(now, snapshot.CompletedAt);
    }

    [Fact]
    public void ComputeMangaUpdate_ReopensCompletedEntryWhenObservedTotalGrows()
    {
        var completedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var now = completedAt.AddDays(8);
        var existing = new MangaListEntry
        {
            Id = 1,
            MangaId = "m1",
            MangaTitle = "Yuru Camp",
            Status = MangaReadStatus.Completed,
            TotalChapters = 12,
            ChaptersRead = 12,
            AddedAt = completedAt.AddDays(-10),
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };

        var snapshot = ListProgressTracker.ComputeMangaUpdate(existing, 12f, 13, now);

        Assert.Equal(12, snapshot.ChaptersRead);
        Assert.Equal(13, snapshot.TotalChapters);
        Assert.Equal(MangaReadStatus.Reading, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void ComputeAnimeUpdate_ReopensCompletedEntryWhenProgressAdvancesWithoutKnownTotal()
    {
        var now = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var existing = new AnimeListEntry
        {
            Id = 1,
            AnimeId = "a1",
            AnimeTitle = "Test Anime",
            Status = AnimeWatchStatus.Completed,
            TotalEpisodes = null,
            EpisodesWatched = 3,
            CompletedAt = now.AddDays(-2),
            AddedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-10)
        };

        var snapshot = ListProgressTracker.ComputeAnimeUpdate(existing, 4, null, now);

        Assert.Equal(4, snapshot.EpisodesWatched);
        Assert.Equal(AnimeWatchStatus.Watching, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void ComputeAnimeUpdate_PreservesHigherKnownTotalWhenObservedTotalShrinks()
    {
        var now = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var existing = new AnimeListEntry
        {
            Id = 1,
            AnimeId = "a1",
            AnimeTitle = "Long Runner",
            Status = AnimeWatchStatus.Watching,
            TotalEpisodes = 24,
            EpisodesWatched = 12,
            AddedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-1)
        };

        var snapshot = ListProgressTracker.ComputeAnimeUpdate(existing, 13, 12, now);

        Assert.Equal(13, snapshot.EpisodesWatched);
        Assert.Equal(24, snapshot.TotalEpisodes);
        Assert.Equal(AnimeWatchStatus.Watching, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void ComputeAnimeUpdate_RefreshesCompletedAtWhenEntryRecompletesAfterTotalGrowth()
    {
        var completedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var now = completedAt.AddDays(8);
        var existing = new AnimeListEntry
        {
            Id = 1,
            AnimeId = "a1",
            AnimeTitle = "Seasonal Anime",
            Status = AnimeWatchStatus.Completed,
            TotalEpisodes = 12,
            EpisodesWatched = 12,
            AddedAt = completedAt.AddDays(-7),
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };

        var snapshot = ListProgressTracker.ComputeAnimeUpdate(existing, 13, 13, now);

        Assert.Equal(13, snapshot.EpisodesWatched);
        Assert.Equal(13, snapshot.TotalEpisodes);
        Assert.Equal(AnimeWatchStatus.Completed, snapshot.Status);
        Assert.Equal(now, snapshot.CompletedAt);
    }

    [Theory]
    [InlineData(MangaReadStatus.PlanToRead)]
    [InlineData(MangaReadStatus.OnHold)]
    [InlineData(MangaReadStatus.Dropped)]
    public void ComputeMangaUpdate_ResumesInactiveEntryWhenProgressAdvances(MangaReadStatus startingStatus)
    {
        var now = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var existing = new MangaListEntry
        {
            Id = 1,
            MangaId = "m1",
            MangaTitle = "Test Manga",
            Status = startingStatus,
            TotalChapters = 24,
            ChaptersRead = 3,
            AddedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-10)
        };

        var snapshot = ListProgressTracker.ComputeMangaUpdate(existing, 4f, 24, now);

        Assert.Equal(4, snapshot.ChaptersRead);
        Assert.Equal(24, snapshot.TotalChapters);
        Assert.Equal(MangaReadStatus.Reading, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void ComputeMangaUpdate_ReopensCompletedEntryWhenProgressAdvancesWithoutKnownTotal()
    {
        var now = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);
        var existing = new MangaListEntry
        {
            Id = 1,
            MangaId = "m1",
            MangaTitle = "Mystery Manga",
            Status = MangaReadStatus.Completed,
            TotalChapters = null,
            ChaptersRead = 10,
            CompletedAt = now.AddDays(-2),
            AddedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-10)
        };

        var snapshot = ListProgressTracker.ComputeMangaUpdate(existing, 11f, null, now);

        Assert.Equal(11, snapshot.ChaptersRead);
        Assert.Null(snapshot.TotalChapters);
        Assert.Equal(MangaReadStatus.Reading, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public void ComputeMangaUpdate_RefreshesCompletedAtWhenEntryRecompletesAfterTotalGrowth()
    {
        var completedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var now = completedAt.AddDays(8);
        var existing = new MangaListEntry
        {
            Id = 1,
            MangaId = "m1",
            MangaTitle = "Seasonal Manga",
            Status = MangaReadStatus.Completed,
            TotalChapters = 12,
            ChaptersRead = 12,
            AddedAt = completedAt.AddDays(-7),
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };

        var snapshot = ListProgressTracker.ComputeMangaUpdate(existing, 13f, 13, now);

        Assert.Equal(13, snapshot.ChaptersRead);
        Assert.Equal(13, snapshot.TotalChapters);
        Assert.Equal(MangaReadStatus.Completed, snapshot.Status);
        Assert.Equal(now, snapshot.CompletedAt);
    }
}
