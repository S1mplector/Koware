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
}
