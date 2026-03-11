// Author: Ilgaz Mehmetoğlu
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Koware.Cli.History;

internal sealed record MangaResumeTarget(float? ChapterNumber, int StartPage);

internal static class MangaChapterResumeResolver
{
    internal static MangaResumeTarget Resolve(MangaListEntry entry, ReadHistoryEntry? historyEntry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (historyEntry is not null && historyEntry.ChapterNumber > 0)
        {
            if (historyEntry.LastPage > 1)
            {
                return new MangaResumeTarget(historyEntry.ChapterNumber, historyEntry.LastPage);
            }

            var nextChapter = historyEntry.ChapterNumber + 1f;
            if (entry.TotalChapters is > 0 && historyEntry.ChapterNumber >= entry.TotalChapters.Value)
            {
                nextChapter = entry.TotalChapters.Value;
            }

            return new MangaResumeTarget(Math.Max(1f, nextChapter), 1);
        }

        if (entry.ChaptersRead > 0)
        {
            var nextChapter = entry.ChaptersRead + 1f;
            if (entry.TotalChapters is > 0 && entry.ChaptersRead >= entry.TotalChapters.Value)
            {
                nextChapter = entry.TotalChapters.Value;
            }

            return new MangaResumeTarget(Math.Max(1f, nextChapter), 1);
        }

        return new MangaResumeTarget(1f, 1);
    }

    internal static async Task<MangaResumeTarget> ResolveAsync(
        MangaListEntry entry,
        IReadHistoryStore readHistory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readHistory);

        var historyEntry = await readHistory.GetLastForMangaAsync(entry.MangaTitle, cancellationToken);
        historyEntry ??= await readHistory.SearchLastAsync(entry.MangaTitle, cancellationToken);
        return Resolve(entry, historyEntry);
    }
}
