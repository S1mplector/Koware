// Author: Ilgaz Mehmetoğlu
// Interactive fuzzy selector for browsing watch/read history.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koware.Cli.History;

namespace Koware.Cli.Console;

/// <summary>
/// Interactive fuzzy manager for watch/read history.
/// </summary>
public sealed class InteractiveHistoryManager
{
    private readonly IWatchHistoryStore _watchStore;
    private readonly Func<WatchHistoryEntry, int, Task>? _onPlayAnime;
    private readonly CancellationToken _cancellationToken;

    public InteractiveHistoryManager(
        IWatchHistoryStore watchStore,
        Func<WatchHistoryEntry, int, Task>? onPlayAnime = null,
        CancellationToken cancellationToken = default)
    {
        _watchStore = watchStore;
        _onPlayAnime = onPlayAnime;
        _cancellationToken = cancellationToken;
    }

    public async Task<int> RunAsync()
    {
        var query = new HistoryQuery(null, null, null, null, null, 100);
        var entries = await _watchStore.QueryAsync(query, _cancellationToken);

        if (entries.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine("No watch history yet.");
            System.Console.ResetColor();
            System.Console.WriteLine();
            System.Console.WriteLine("Watch something with: koware watch \"<title>\"");
            return 0;
        }

        // Group by anime and get latest entry per anime
        var grouped = entries
            .GroupBy(e => e.AnimeTitle)
            .Select(g => new AnimeHistoryGroup
            {
                Title = g.Key,
                AnimeId = g.First().AnimeId,
                Provider = g.First().Provider,
                LastEpisode = g.Max(e => e.EpisodeNumber),
                Quality = g.First().Quality,
                LastWatched = g.Max(e => e.WatchedAt),
                EpisodeCount = g.Select(e => e.EpisodeNumber).Distinct().Count()
            })
            .OrderByDescending(g => g.LastWatched)
            .ToList();

        var result = ShowHistorySelector(grouped);

        if (result.SelectedGroup is null)
        {
            return 0;
        }

        // Resume from next episode
        var nextEpisode = result.SelectedGroup.LastEpisode + 1;

        if (_onPlayAnime is not null)
        {
            var entry = entries.First(e => e.AnimeTitle == result.SelectedGroup.Title);
            await _onPlayAnime(entry, nextEpisode);
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Play} Resume: {result.SelectedGroup.Title}");
            System.Console.ResetColor();
            System.Console.WriteLine($"  Episode {nextEpisode} (last watched: {result.SelectedGroup.LastEpisode})");
        }

        return 0;
    }

    private (AnimeHistoryGroup? SelectedGroup, bool Cancelled) ShowHistorySelector(List<AnimeHistoryGroup> groups)
    {
        var displayItems = groups.Select(g => new HistoryDisplayItem(g)).ToList();

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        var state = new HistorySelectorState(displayItems, buffer);

        buffer.Initialize();
        state.Render();

        while (true)
        {
            var key = System.Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    state.MoveUp();
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.J when key.Modifiers == ConsoleModifiers.Control:
                    state.MoveDown();
                    break;

                case ConsoleKey.PageUp:
                    state.MoveUp(state.MaxVisible);
                    break;

                case ConsoleKey.PageDown:
                    state.MoveDown(state.MaxVisible);
                    break;

                case ConsoleKey.Home:
                    state.JumpToStart();
                    break;

                case ConsoleKey.End:
                    state.JumpToEnd();
                    break;

                case ConsoleKey.Enter:
                    buffer.Restore();
                    return (state.SelectedGroup, false);

                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    buffer.Restore();
                    return (null, true);

                case ConsoleKey.Backspace:
                    state.SearchBackspace();
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        state.SearchAppend(key.KeyChar);
                    }
                    break;
            }

            state.Render();
        }
    }

    private sealed class AnimeHistoryGroup
    {
        public string Title { get; init; } = string.Empty;
        public string AnimeId { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public int LastEpisode { get; init; }
        public string? Quality { get; init; }
        public DateTimeOffset LastWatched { get; init; }
        public int EpisodeCount { get; init; }
    }

    private sealed class HistoryDisplayItem
    {
        public AnimeHistoryGroup Group { get; }
        public string SearchText { get; }

        public HistoryDisplayItem(AnimeHistoryGroup group)
        {
            Group = group;
            SearchText = group.Title.ToLowerInvariant();
        }
    }

    private sealed class HistorySelectorState
    {
        private readonly List<HistoryDisplayItem> _allItems;
        private readonly TerminalBuffer _buffer;
        private List<HistoryDisplayItem> _filtered;
        private string _searchText = "";
        private int _selectedIndex;
        private int _scrollOffset;

        public int MaxVisible { get; }
        public AnimeHistoryGroup SelectedGroup => _filtered.Count > 0 && _selectedIndex < _filtered.Count
            ? _filtered[_selectedIndex].Group
            : _allItems[0].Group;

        public HistorySelectorState(List<HistoryDisplayItem> items, TerminalBuffer buffer)
        {
            _allItems = items;
            _buffer = buffer;
            _filtered = items;
            MaxVisible = Math.Min(12, Math.Max(3, GetTerminalHeight() - 8));
        }

        private static int GetTerminalHeight()
        {
            try { return System.Console.WindowHeight; }
            catch { return 24; }
        }

        public void MoveUp(int count = 1)
        {
            _selectedIndex = Math.Max(0, _selectedIndex - count);
            if (_selectedIndex < _scrollOffset) _scrollOffset = _selectedIndex;
        }

        public void MoveDown(int count = 1)
        {
            _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + count);
            if (_selectedIndex >= _scrollOffset + MaxVisible) _scrollOffset = _selectedIndex - MaxVisible + 1;
        }

        public void JumpToStart() { _selectedIndex = 0; _scrollOffset = 0; }
        public void JumpToEnd()
        {
            _selectedIndex = Math.Max(0, _filtered.Count - 1);
            _scrollOffset = Math.Max(0, _filtered.Count - MaxVisible);
        }

        public void SearchAppend(char c) { _searchText += c; UpdateFilter(); }
        public void SearchBackspace()
        {
            if (_searchText.Length > 0) { _searchText = _searchText[..^1]; UpdateFilter(); }
        }

        private void UpdateFilter()
        {
            if (string.IsNullOrEmpty(_searchText))
            {
                _filtered = _allItems;
            }
            else
            {
                var pattern = _searchText.ToLowerInvariant();
                _filtered = _allItems
                    .Select(item => (item, score: FuzzyMatcher.Score(item.SearchText, pattern)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.item)
                    .ToList();
            }

            if (_selectedIndex >= _filtered.Count) _selectedIndex = Math.Max(0, _filtered.Count - 1);
            if (_scrollOffset > _selectedIndex) _scrollOffset = _selectedIndex;
        }

        public void Render()
        {
            _buffer.BeginFrame();
            var lines = 0;
            var width = _buffer.Width;

            // Header
            _buffer.SetColor(ConsoleColor.Cyan);
            _buffer.Write($" {Icons.Prompt} Watch History");
            _buffer.ResetColor();
            _buffer.SetColor(ConsoleColor.DarkGray);
            _buffer.Write($" [{_filtered.Count}/{_allItems.Count}]");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            // Separator
            _buffer.WriteLine(new string('─', Math.Min(width - 2, 70)), ConsoleColor.DarkGray);
            lines++;

            // Search box
            _buffer.SetColor(ConsoleColor.DarkGray);
            _buffer.Write($"  {Icons.Search} ");
            _buffer.SetColor(ConsoleColor.White);
            _buffer.Write(_searchText);
            _buffer.SetColor(ConsoleColor.Cyan);
            _buffer.Write("▌");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            // Separator
            _buffer.WriteLine(new string('─', Math.Min(width - 2, 70)), ConsoleColor.DarkGray);
            lines++;

            // Items
            var visibleItems = _filtered.Skip(_scrollOffset).Take(MaxVisible).ToList();
            for (var i = 0; i < MaxVisible; i++)
            {
                if (i < visibleItems.Count)
                {
                    var item = visibleItems[i];
                    var isSelected = (_scrollOffset + i) == _selectedIndex;

                    // Selection indicator
                    if (isSelected)
                    {
                        _buffer.SetColor(ConsoleColor.Cyan);
                        _buffer.Write($" {Icons.Selection} ");
                    }
                    else
                    {
                        _buffer.Write("   ");
                    }

                    // Episode info
                    _buffer.SetColor(ConsoleColor.Green);
                    _buffer.Write($"Ep {item.Group.LastEpisode,-4} ");

                    // Time ago
                    _buffer.SetColor(ConsoleColor.DarkGray);
                    var timeAgo = FormatTimeAgo(item.Group.LastWatched);
                    _buffer.Write($"{timeAgo,-12} ");

                    // Title
                    if (isSelected)
                    {
                        _buffer.SetColor(ConsoleColor.White);
                    }
                    else
                    {
                        _buffer.SetColor(ConsoleColor.Gray);
                    }

                    var maxTitleLen = width - 26;
                    var title = item.Group.Title;
                    if (title.Length > maxTitleLen && maxTitleLen > 3)
                    {
                        title = title[..(maxTitleLen - 3)] + "...";
                    }
                    _buffer.Write(title);
                    _buffer.ResetColor();
                }
                _buffer.WriteLine();
                lines++;
            }

            // Scroll indicator
            if (_filtered.Count > MaxVisible)
            {
                var scrollPercent = (int)((_scrollOffset / (float)Math.Max(1, _filtered.Count - MaxVisible)) * 100);
                _buffer.SetColor(ConsoleColor.DarkGray);
                _buffer.Write($" [{_selectedIndex + 1}/{_filtered.Count}] {scrollPercent}%");
                _buffer.ResetColor();
            }
            _buffer.WriteLine();
            lines++;

            // Footer
            _buffer.WriteLine(new string('─', Math.Min(width - 2, 70)), ConsoleColor.DarkGray);
            lines++;
            _buffer.SetColor(ConsoleColor.DarkGray);
            _buffer.Write(" [Enter] Resume next episode  [Esc] Exit");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            _buffer.EndFrame(0, lines);
        }

        private static string FormatTimeAgo(DateTimeOffset time)
        {
            var diff = DateTimeOffset.Now - time;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}w ago";
            return time.ToString("MMM d");
        }
    }
}

/// <summary>
/// Interactive fuzzy manager for manga read history.
/// </summary>
public sealed class InteractiveMangaHistoryManager
{
    private readonly IReadHistoryStore _readStore;
    private readonly Func<ReadHistoryEntry, float, Task>? _onReadManga;
    private readonly CancellationToken _cancellationToken;

    public InteractiveMangaHistoryManager(
        IReadHistoryStore readStore,
        Func<ReadHistoryEntry, float, Task>? onReadManga = null,
        CancellationToken cancellationToken = default)
    {
        _readStore = readStore;
        _onReadManga = onReadManga;
        _cancellationToken = cancellationToken;
    }

    public async Task<int> RunAsync()
    {
        var query = new ReadHistoryQuery(null, null, null, null, null, 100);
        var entries = await _readStore.QueryAsync(query, _cancellationToken);

        if (entries.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine("No reading history yet.");
            System.Console.ResetColor();
            System.Console.WriteLine();
            System.Console.WriteLine("Read something with: koware -m read \"<title>\"");
            return 0;
        }

        // Group by manga and get latest entry per manga
        var grouped = entries
            .GroupBy(e => e.MangaTitle)
            .Select(g => new MangaHistoryGroup
            {
                Title = g.Key,
                MangaId = g.First().MangaId,
                Provider = g.First().Provider,
                LastChapter = g.Max(e => e.ChapterNumber),
                LastRead = g.Max(e => e.ReadAt),
                ChapterCount = g.Select(e => e.ChapterNumber).Distinct().Count()
            })
            .OrderByDescending(g => g.LastRead)
            .ToList();

        var result = ShowHistorySelector(grouped);

        if (result.SelectedGroup is null)
        {
            return 0;
        }

        // Resume from next chapter
        var nextChapter = result.SelectedGroup.LastChapter + 1;

        if (_onReadManga is not null)
        {
            var entry = entries.First(e => e.MangaTitle == result.SelectedGroup.Title);
            await _onReadManga(entry, nextChapter);
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Play} Resume: {result.SelectedGroup.Title}");
            System.Console.ResetColor();
            System.Console.WriteLine($"  Chapter {nextChapter} (last read: {result.SelectedGroup.LastChapter})");
        }

        return 0;
    }

    private (MangaHistoryGroup? SelectedGroup, bool Cancelled) ShowHistorySelector(List<MangaHistoryGroup> groups)
    {
        var displayItems = groups.Select(g => new MangaHistoryDisplayItem(g)).ToList();

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        var state = new MangaHistorySelectorState(displayItems, buffer);

        buffer.Initialize();
        state.Render();

        while (true)
        {
            var key = System.Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                    state.MoveUp();
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.J when key.Modifiers == ConsoleModifiers.Control:
                    state.MoveDown();
                    break;

                case ConsoleKey.PageUp:
                    state.MoveUp(state.MaxVisible);
                    break;

                case ConsoleKey.PageDown:
                    state.MoveDown(state.MaxVisible);
                    break;

                case ConsoleKey.Home:
                    state.JumpToStart();
                    break;

                case ConsoleKey.End:
                    state.JumpToEnd();
                    break;

                case ConsoleKey.Enter:
                    buffer.Restore();
                    return (state.SelectedGroup, false);

                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    buffer.Restore();
                    return (null, true);

                case ConsoleKey.Backspace:
                    state.SearchBackspace();
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        state.SearchAppend(key.KeyChar);
                    }
                    break;
            }

            state.Render();
        }
    }

    private sealed class MangaHistoryGroup
    {
        public string Title { get; init; } = string.Empty;
        public string MangaId { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public float LastChapter { get; init; }
        public DateTimeOffset LastRead { get; init; }
        public int ChapterCount { get; init; }
    }

    private sealed class MangaHistoryDisplayItem
    {
        public MangaHistoryGroup Group { get; }
        public string SearchText { get; }

        public MangaHistoryDisplayItem(MangaHistoryGroup group)
        {
            Group = group;
            SearchText = group.Title.ToLowerInvariant();
        }
    }

    private sealed class MangaHistorySelectorState
    {
        private readonly List<MangaHistoryDisplayItem> _allItems;
        private readonly TerminalBuffer _buffer;
        private List<MangaHistoryDisplayItem> _filtered;
        private string _searchText = "";
        private int _selectedIndex;
        private int _scrollOffset;

        public int MaxVisible { get; }
        public MangaHistoryGroup SelectedGroup => _filtered.Count > 0 && _selectedIndex < _filtered.Count
            ? _filtered[_selectedIndex].Group
            : _allItems[0].Group;

        public MangaHistorySelectorState(List<MangaHistoryDisplayItem> items, TerminalBuffer buffer)
        {
            _allItems = items;
            _buffer = buffer;
            _filtered = items;
            MaxVisible = Math.Min(12, Math.Max(3, GetTerminalHeight() - 8));
        }

        private static int GetTerminalHeight()
        {
            try { return System.Console.WindowHeight; }
            catch { return 24; }
        }

        public void MoveUp(int count = 1)
        {
            _selectedIndex = Math.Max(0, _selectedIndex - count);
            if (_selectedIndex < _scrollOffset) _scrollOffset = _selectedIndex;
        }

        public void MoveDown(int count = 1)
        {
            _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + count);
            if (_selectedIndex >= _scrollOffset + MaxVisible) _scrollOffset = _selectedIndex - MaxVisible + 1;
        }

        public void JumpToStart() { _selectedIndex = 0; _scrollOffset = 0; }
        public void JumpToEnd()
        {
            _selectedIndex = Math.Max(0, _filtered.Count - 1);
            _scrollOffset = Math.Max(0, _filtered.Count - MaxVisible);
        }

        public void SearchAppend(char c) { _searchText += c; UpdateFilter(); }
        public void SearchBackspace()
        {
            if (_searchText.Length > 0) { _searchText = _searchText[..^1]; UpdateFilter(); }
        }

        private void UpdateFilter()
        {
            if (string.IsNullOrEmpty(_searchText))
            {
                _filtered = _allItems;
            }
            else
            {
                var pattern = _searchText.ToLowerInvariant();
                _filtered = _allItems
                    .Select(item => (item, score: FuzzyMatcher.Score(item.SearchText, pattern)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.item)
                    .ToList();
            }

            if (_selectedIndex >= _filtered.Count) _selectedIndex = Math.Max(0, _filtered.Count - 1);
            if (_scrollOffset > _selectedIndex) _scrollOffset = _selectedIndex;
        }

        public void Render()
        {
            _buffer.BeginFrame();
            var lines = 0;
            var width = _buffer.Width;

            // Header
            _buffer.SetColor(ConsoleColor.Magenta);
            _buffer.Write($" {Icons.Prompt} Reading History");
            _buffer.ResetColor();
            _buffer.SetColor(ConsoleColor.DarkGray);
            _buffer.Write($" [{_filtered.Count}/{_allItems.Count}]");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            // Separator
            _buffer.WriteLine(new string('─', Math.Min(width - 2, 70)), ConsoleColor.DarkGray);
            lines++;

            // Search box
            _buffer.SetColor(ConsoleColor.DarkGray);
            _buffer.Write($"  {Icons.Search} ");
            _buffer.SetColor(ConsoleColor.White);
            _buffer.Write(_searchText);
            _buffer.SetColor(ConsoleColor.Magenta);
            _buffer.Write("▌");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            // Separator
            _buffer.WriteLine(new string('─', Math.Min(width - 2, 70)), ConsoleColor.DarkGray);
            lines++;

            // Items
            var visibleItems = _filtered.Skip(_scrollOffset).Take(MaxVisible).ToList();
            for (var i = 0; i < MaxVisible; i++)
            {
                if (i < visibleItems.Count)
                {
                    var item = visibleItems[i];
                    var isSelected = (_scrollOffset + i) == _selectedIndex;

                    // Selection indicator
                    if (isSelected)
                    {
                        _buffer.SetColor(ConsoleColor.Magenta);
                        _buffer.Write($" {Icons.Selection} ");
                    }
                    else
                    {
                        _buffer.Write("   ");
                    }

                    // Chapter info
                    _buffer.SetColor(ConsoleColor.Green);
                    var chapterStr = item.Group.LastChapter % 1 == 0
                        ? $"Ch {(int)item.Group.LastChapter}"
                        : $"Ch {item.Group.LastChapter:0.#}";
                    _buffer.Write($"{chapterStr,-8} ");

                    // Time ago
                    _buffer.SetColor(ConsoleColor.DarkGray);
                    var timeAgo = FormatTimeAgo(item.Group.LastRead);
                    _buffer.Write($"{timeAgo,-12} ");

                    // Title
                    if (isSelected)
                    {
                        _buffer.SetColor(ConsoleColor.White);
                    }
                    else
                    {
                        _buffer.SetColor(ConsoleColor.Gray);
                    }

                    var maxTitleLen = width - 28;
                    var title = item.Group.Title;
                    if (title.Length > maxTitleLen && maxTitleLen > 3)
                    {
                        title = title[..(maxTitleLen - 3)] + "...";
                    }
                    _buffer.Write(title);
                    _buffer.ResetColor();
                }
                _buffer.WriteLine();
                lines++;
            }

            // Scroll indicator
            if (_filtered.Count > MaxVisible)
            {
                var scrollPercent = (int)((_scrollOffset / (float)Math.Max(1, _filtered.Count - MaxVisible)) * 100);
                _buffer.SetColor(ConsoleColor.DarkGray);
                _buffer.Write($" [{_selectedIndex + 1}/{_filtered.Count}] {scrollPercent}%");
                _buffer.ResetColor();
            }
            _buffer.WriteLine();
            lines++;

            // Footer
            _buffer.WriteLine(new string('─', Math.Min(width - 2, 70)), ConsoleColor.DarkGray);
            lines++;
            _buffer.SetColor(ConsoleColor.DarkGray);
            _buffer.Write(" [Enter] Resume next chapter  [Esc] Exit");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            _buffer.EndFrame(0, lines);
        }

        private static string FormatTimeAgo(DateTimeOffset time)
        {
            var diff = DateTimeOffset.Now - time;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}w ago";
            return time.ToString("MMM d");
        }
    }
}
