// Author: Ilgaz Mehmetoğlu
// Tests for DownloadStore persistence and operations.
using System;
using System.IO;
using System.Threading.Tasks;
using Koware.Cli.History;
using Xunit;

namespace Koware.Tests;

public class DownloadStoreTests : IAsyncLifetime
{
    private readonly string _testDbPath;
    private readonly SqliteDownloadStore _store;

    public DownloadStoreTests()
    {
        // Use a temporary test database - store uses default path
        _testDbPath = Path.Combine(Path.GetTempPath(), $"koware_test_{Guid.NewGuid()}.db");
        _store = new SqliteDownloadStore();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        // Clean up test database
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    #region DownloadType Enum Tests

    [Theory]
    [InlineData(DownloadType.Episode, "Episode")]
    [InlineData(DownloadType.Chapter, "Chapter")]
    public void DownloadType_HasExpectedValues(DownloadType type, string expectedName)
    {
        Assert.Equal(expectedName, type.ToString());
    }

    #endregion

    #region DownloadEntry Tests

    [Fact]
    public void DownloadEntry_DefaultValues()
    {
        var entry = new DownloadEntry();

        Assert.Equal(0, entry.Id);
        Assert.Equal(DownloadType.Episode, entry.Type);
        Assert.Equal("", entry.ContentId);
        Assert.Equal("", entry.ContentTitle);
        Assert.Equal(0, entry.Number);
        Assert.Null(entry.Quality);
        Assert.Equal("", entry.FilePath);
        Assert.Equal(0, entry.FileSizeBytes);
        Assert.Equal(default, entry.DownloadedAt);
        Assert.False(entry.Exists); // File doesn't exist
    }

    [Fact]
    public void DownloadEntry_Exists_FalseForNonExistentFile()
    {
        var entry = new DownloadEntry
        {
            FilePath = "/nonexistent/path/to/file.mp4"
        };

        Assert.False(entry.Exists);
    }

    [Fact]
    public void DownloadEntry_Exists_TrueForExistingFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var entry = new DownloadEntry
            {
                FilePath = tempFile
            };

            Assert.True(entry.Exists);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion

    #region DownloadStats Tests

    [Fact]
    public void DownloadStats_RecordValues()
    {
        var stats = new DownloadStats(10, 20, 5, 8, 1024 * 1024 * 100);

        Assert.Equal(10, stats.TotalEpisodes);
        Assert.Equal(20, stats.TotalChapters);
        Assert.Equal(5, stats.UniqueAnime);
        Assert.Equal(8, stats.UniqueManga);
        Assert.Equal(1024 * 1024 * 100, stats.TotalSizeBytes);
    }

    #endregion

    #region AddAsync Tests

    [Fact]
    public async Task AddAsync_CreatesNewEntry()
    {
        var entry = await _store.AddAsync(
            DownloadType.Episode,
            "anime-123",
            "Test Anime",
            1,
            "1080p",
            "/path/to/ep1.mp4",
            1024 * 1024 * 500);

        Assert.True(entry.Id > 0);
        Assert.Equal(DownloadType.Episode, entry.Type);
        Assert.Equal("anime-123", entry.ContentId);
        Assert.Equal("Test Anime", entry.ContentTitle);
        Assert.Equal(1, entry.Number);
        Assert.Equal("1080p", entry.Quality);
        Assert.Equal("/path/to/ep1.mp4", entry.FilePath);
        Assert.Equal(1024 * 1024 * 500, entry.FileSizeBytes);
        Assert.True(entry.DownloadedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task AddAsync_Upserts_SameContentAndNumber()
    {
        // First add
        var entry1 = await _store.AddAsync(
            DownloadType.Episode,
            "anime-123",
            "Test Anime",
            1,
            "720p",
            "/path/to/ep1_720.mp4",
            1024 * 1024 * 300);

        // Second add with same content+number (should update)
        var entry2 = await _store.AddAsync(
            DownloadType.Episode,
            "anime-123",
            "Test Anime Updated",
            1,
            "1080p",
            "/path/to/ep1_1080.mp4",
            1024 * 1024 * 500);

        // Should have same ID (upserted)
        Assert.Equal(entry1.Id, entry2.Id);
        Assert.Equal("1080p", entry2.Quality);
        Assert.Equal("/path/to/ep1_1080.mp4", entry2.FilePath);
    }

    #endregion

    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_ReturnsEntry_WhenExists()
    {
        await _store.AddAsync(DownloadType.Episode, "anime-456", "Another Anime", 5, "720p", "/path.mp4", 1000);

        var result = await _store.GetAsync("anime-456", 5);

        Assert.NotNull(result);
        Assert.Equal("anime-456", result!.ContentId);
        Assert.Equal(5, result.Number);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotExists()
    {
        var result = await _store.GetAsync("nonexistent", 1);

        Assert.Null(result);
    }

    #endregion

    #region GetForContentAsync Tests

    [Fact]
    public async Task GetForContentAsync_ReturnsAllEpisodesForAnime()
    {
        await _store.AddAsync(DownloadType.Episode, "anime-789", "My Anime", 1, "1080p", "/ep1.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime-789", "My Anime", 2, "1080p", "/ep2.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime-789", "My Anime", 3, "1080p", "/ep3.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "other-anime", "Other", 1, "1080p", "/other.mp4", 1000);

        var results = await _store.GetForContentAsync("anime-789");

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("anime-789", r.ContentId));
    }

    [Fact]
    public async Task GetForContentAsync_ReturnsOrderedByNumber()
    {
        await _store.AddAsync(DownloadType.Episode, "anime-order", "Anime", 3, null, "/ep3.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime-order", "Anime", 1, null, "/ep1.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime-order", "Anime", 2, null, "/ep2.mp4", 1000);

        var results = await _store.GetForContentAsync("anime-order");

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].Number);
        Assert.Equal(2, results[1].Number);
        Assert.Equal(3, results[2].Number);
    }

    #endregion

    #region GetAllAsync Tests

    [Fact]
    public async Task GetAllAsync_ReturnsAllEntries()
    {
        await _store.AddAsync(DownloadType.Episode, "a1", "Anime 1", 1, null, "/a1.mp4", 1000);
        await _store.AddAsync(DownloadType.Chapter, "m1", "Manga 1", 1, null, "/m1", 2000);

        var all = await _store.GetAllAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAllAsync_FiltersByType_Episode()
    {
        await _store.AddAsync(DownloadType.Episode, "a1", "Anime", 1, null, "/a.mp4", 1000);
        await _store.AddAsync(DownloadType.Chapter, "m1", "Manga", 1, null, "/m", 2000);

        var episodes = await _store.GetAllAsync(DownloadType.Episode);

        Assert.Single(episodes);
        Assert.Equal(DownloadType.Episode, episodes[0].Type);
    }

    [Fact]
    public async Task GetAllAsync_FiltersByType_Chapter()
    {
        await _store.AddAsync(DownloadType.Episode, "a1", "Anime", 1, null, "/a.mp4", 1000);
        await _store.AddAsync(DownloadType.Chapter, "m1", "Manga", 1, null, "/m", 2000);

        var chapters = await _store.GetAllAsync(DownloadType.Chapter);

        Assert.Single(chapters);
        Assert.Equal(DownloadType.Chapter, chapters[0].Type);
    }

    #endregion

    #region RemoveAsync Tests

    [Fact]
    public async Task RemoveAsync_RemovesEntry()
    {
        var entry = await _store.AddAsync(DownloadType.Episode, "to-remove", "Anime", 1, null, "/r.mp4", 1000);

        var removed = await _store.RemoveAsync(entry.Id);

        Assert.True(removed);
        var check = await _store.GetAsync("to-remove", 1);
        Assert.Null(check);
    }

    [Fact]
    public async Task RemoveAsync_ReturnsFalse_WhenNotExists()
    {
        var removed = await _store.RemoveAsync(99999);

        Assert.False(removed);
    }

    #endregion

    #region GetStatsAsync Tests

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectCounts()
    {
        await _store.AddAsync(DownloadType.Episode, "anime1", "Anime 1", 1, null, "/a1e1.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime1", "Anime 1", 2, null, "/a1e2.mp4", 2000);
        await _store.AddAsync(DownloadType.Episode, "anime2", "Anime 2", 1, null, "/a2e1.mp4", 3000);
        await _store.AddAsync(DownloadType.Chapter, "manga1", "Manga 1", 1, null, "/m1c1", 500);
        await _store.AddAsync(DownloadType.Chapter, "manga1", "Manga 1", 2, null, "/m1c2", 500);

        var stats = await _store.GetStatsAsync();

        Assert.Equal(3, stats.TotalEpisodes);
        Assert.Equal(2, stats.TotalChapters);
        Assert.Equal(2, stats.UniqueAnime);
        Assert.Equal(1, stats.UniqueManga);
        Assert.Equal(7000, stats.TotalSizeBytes);
    }

    [Fact]
    public async Task GetStatsAsync_EmptyStore_ReturnsZeros()
    {
        var stats = await _store.GetStatsAsync();

        Assert.Equal(0, stats.TotalEpisodes);
        Assert.Equal(0, stats.TotalChapters);
        Assert.Equal(0, stats.UniqueAnime);
        Assert.Equal(0, stats.UniqueManga);
        Assert.Equal(0, stats.TotalSizeBytes);
    }

    #endregion

    #region GetDownloadedNumbersAsync Tests

    [Fact]
    public async Task GetDownloadedNumbersAsync_ReturnsNumbersForContent()
    {
        await _store.AddAsync(DownloadType.Episode, "anime-nums", "Anime", 1, null, "/e1.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime-nums", "Anime", 3, null, "/e3.mp4", 1000);
        await _store.AddAsync(DownloadType.Episode, "anime-nums", "Anime", 5, null, "/e5.mp4", 1000);

        var numbers = await _store.GetDownloadedNumbersAsync("anime-nums");

        Assert.Equal(3, numbers.Count);
        Assert.Contains(1, numbers);
        Assert.Contains(3, numbers);
        Assert.Contains(5, numbers);
        Assert.DoesNotContain(2, numbers);
        Assert.DoesNotContain(4, numbers);
    }

    [Fact]
    public async Task GetDownloadedNumbersAsync_EmptyForNonexistent()
    {
        var numbers = await _store.GetDownloadedNumbersAsync("nonexistent");

        Assert.Empty(numbers);
    }

    #endregion
}

// TestDownloadStore removed - SqliteDownloadStore is now sealed
