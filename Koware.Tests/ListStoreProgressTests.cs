using System.Threading.Tasks;
using Koware.Cli.History;
using Xunit;

namespace Koware.Tests;

public class ListStoreProgressTests
{
    [Fact]
    public async Task RecordChapterReadAsync_ClampsProgressToKnownTotal()
    {
        using var factory = new TestDatabaseConnectionFactory();
        var store = new SqliteMangaListStore(factory);

        await store.AddAsync("m1", "Tokyo Ghoul:re", MangaReadStatus.PlanToRead, totalChapters: 12);
        await store.RecordChapterReadAsync("m1", "Tokyo Ghoul:re", 12.5f, totalChapters: 12);

        var entry = await store.GetByTitleAsync("Tokyo Ghoul:re");

        Assert.NotNull(entry);
        Assert.Equal(12, entry!.ChaptersRead);
        Assert.Equal(12, entry.TotalChapters);
        Assert.Equal(MangaReadStatus.Completed, entry.Status);
        Assert.NotNull(entry.CompletedAt);
    }

    [Fact]
    public async Task RecordChapterReadAsync_ReopensCompletedMangaWhenMoreChaptersAppear()
    {
        using var factory = new TestDatabaseConnectionFactory();
        var store = new SqliteMangaListStore(factory);

        await store.AddAsync("m1", "Yuru Camp", MangaReadStatus.Completed, totalChapters: 12);
        await store.UpdateAsync("Yuru Camp", chaptersRead: 12, cancellationToken: default);

        await store.RecordChapterReadAsync("m1", "Yuru Camp", 12f, totalChapters: 13);

        var entry = await store.GetByTitleAsync("Yuru Camp");

        Assert.NotNull(entry);
        Assert.Equal(12, entry!.ChaptersRead);
        Assert.Equal(13, entry.TotalChapters);
        Assert.Equal(MangaReadStatus.Reading, entry.Status);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public async Task RecordEpisodeWatchedAsync_PreservesHigherKnownTotal()
    {
        using var factory = new TestDatabaseConnectionFactory();
        var store = new SqliteAnimeListStore(factory);

        await store.AddAsync("a1", "Long Runner", AnimeWatchStatus.Watching, totalEpisodes: 24);
        await store.UpdateAsync("Long Runner", episodesWatched: 12, cancellationToken: default);

        await store.RecordEpisodeWatchedAsync("a1", "Long Runner", 13, totalEpisodes: 12);

        var entry = await store.GetByTitleAsync("Long Runner");

        Assert.NotNull(entry);
        Assert.Equal(13, entry!.EpisodesWatched);
        Assert.Equal(24, entry.TotalEpisodes);
        Assert.Equal(AnimeWatchStatus.Watching, entry.Status);
    }

    [Fact]
    public async Task RecordEpisodeWatchedAsync_ReopensCompletedAnimeWhenProgressAdvancesWithoutKnownTotal()
    {
        using var factory = new TestDatabaseConnectionFactory();
        var store = new SqliteAnimeListStore(factory);

        await store.AddAsync("a1", "Unknown Length Anime", AnimeWatchStatus.Completed);
        await store.UpdateAsync("Unknown Length Anime", episodesWatched: 3, cancellationToken: default);

        await store.RecordEpisodeWatchedAsync("a1", "Unknown Length Anime", 4);

        var entry = await store.GetByTitleAsync("Unknown Length Anime");

        Assert.NotNull(entry);
        Assert.Equal(4, entry!.EpisodesWatched);
        Assert.Null(entry.TotalEpisodes);
        Assert.Equal(AnimeWatchStatus.Watching, entry.Status);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public async Task RecordChapterReadAsync_ReopensCompletedMangaWhenProgressAdvancesWithoutKnownTotal()
    {
        using var factory = new TestDatabaseConnectionFactory();
        var store = new SqliteMangaListStore(factory);

        await store.AddAsync("m1", "Unknown Length Manga", MangaReadStatus.Completed);
        await store.UpdateAsync("Unknown Length Manga", chaptersRead: 10, cancellationToken: default);

        await store.RecordChapterReadAsync("m1", "Unknown Length Manga", 11f);

        var entry = await store.GetByTitleAsync("Unknown Length Manga");

        Assert.NotNull(entry);
        Assert.Equal(11, entry!.ChaptersRead);
        Assert.Null(entry.TotalChapters);
        Assert.Equal(MangaReadStatus.Reading, entry.Status);
        Assert.Null(entry.CompletedAt);
    }
}
