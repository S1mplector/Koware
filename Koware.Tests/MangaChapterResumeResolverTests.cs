using Koware.Cli.History;
using Xunit;

namespace Koware.Tests;

public class MangaChapterResumeResolverTests
{
    [Fact]
    public void Resolve_UsesHistoryChapterAndPage_WhenMidChapterResumeExists()
    {
        var entry = new MangaListEntry
        {
            MangaTitle = "Tokyo Ghoul:re",
            ChaptersRead = 100,
            TotalChapters = 186
        };

        var history = new ReadHistoryEntry
        {
            MangaTitle = entry.MangaTitle,
            ChapterNumber = 55f,
            LastPage = 18
        };

        var target = MangaChapterResumeResolver.Resolve(entry, history);

        Assert.Equal(55f, target.ChapterNumber);
        Assert.Equal(18, target.StartPage);
    }

    [Fact]
    public void Resolve_AdvancesToNextChapter_WhenHistoryHasNoPageOffset()
    {
        var entry = new MangaListEntry
        {
            MangaTitle = "Tokyo Ghoul:re",
            ChaptersRead = 55,
            TotalChapters = 186
        };

        var history = new ReadHistoryEntry
        {
            MangaTitle = entry.MangaTitle,
            ChapterNumber = 55f,
            LastPage = 1
        };

        var target = MangaChapterResumeResolver.Resolve(entry, history);

        Assert.Equal(56f, target.ChapterNumber);
        Assert.Equal(1, target.StartPage);
    }

    [Fact]
    public void Resolve_ClampsToKnownTotal_WhenProgressAlreadyAtEnd()
    {
        var entry = new MangaListEntry
        {
            MangaTitle = "Tokyo Ghoul: Redrawn",
            ChaptersRead = 1,
            TotalChapters = 1
        };

        var history = new ReadHistoryEntry
        {
            MangaTitle = entry.MangaTitle,
            ChapterNumber = 1f,
            LastPage = 1
        };

        var target = MangaChapterResumeResolver.Resolve(entry, history);

        Assert.Equal(1f, target.ChapterNumber);
        Assert.Equal(1, target.StartPage);
    }

    [Fact]
    public void Resolve_StartsFromChapterOne_WhenNoHistoryOrProgress()
    {
        var entry = new MangaListEntry
        {
            MangaTitle = "New Manga",
            ChaptersRead = 0,
            TotalChapters = null
        };

        var target = MangaChapterResumeResolver.Resolve(entry, historyEntry: null);

        Assert.Equal(1f, target.ChapterNumber);
        Assert.Equal(1, target.StartPage);
    }
}
