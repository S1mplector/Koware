// Author: Ilgaz Mehmetoğlu
// Interactive fuzzy selector for managing offline/downloaded content.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koware.Cli.History;

namespace Koware.Cli.Console;

/// <summary>
/// Actions available for offline content.
/// </summary>
public enum OfflineAction
{
    None,
    Play,
    Delete,
    DeleteAll,
    Cleanup,
    Exit
}

/// <summary>
/// Interactive fuzzy manager for offline/downloaded content.
/// </summary>
public sealed class InteractiveOfflineManager
{
    private readonly IDownloadStore _store;
    private readonly DownloadType _type;
    private readonly Func<DownloadEntry, Task>? _onPlay;
    private readonly CancellationToken _cancellationToken;

    public InteractiveOfflineManager(
        IDownloadStore store,
        DownloadType type,
        Func<DownloadEntry, Task>? onPlay = null,
        CancellationToken cancellationToken = default)
    {
        _store = store;
        _type = type;
        _onPlay = onPlay;
        _cancellationToken = cancellationToken;
    }

    public async Task<int> RunAsync()
    {
        while (true)
        {
            var downloads = await _store.GetAllAsync(_type, _cancellationToken);

            if (downloads.Count == 0)
            {
                ShowEmptyPrompt();
                return 0;
            }

            // Group by content
            var grouped = downloads
                .GroupBy(d => d.ContentId)
                .Select(g => new ContentGroup
                {
                    ContentId = g.Key,
                    Title = g.First().ContentTitle,
                    Items = g.OrderBy(d => d.Number).ToList(),
                    TotalSize = g.Sum(d => d.FileSizeBytes),
                    AvailableCount = g.Count(d => d.Exists),
                    MissingCount = g.Count(d => !d.Exists)
                })
                .OrderByDescending(g => g.Items.Max(d => d.DownloadedAt))
                .ToList();

            var result = ShowContentSelector(grouped);

            if (result.Action == OfflineAction.Exit)
            {
                return 0;
            }

            if (result.Action == OfflineAction.Cleanup)
            {
                await HandleCleanupAsync();
                continue;
            }

            if (result.SelectedGroup is null)
            {
                continue;
            }

            switch (result.Action)
            {
                case OfflineAction.Play:
                    await ShowEpisodeSelector(result.SelectedGroup);
                    break;
                case OfflineAction.DeleteAll:
                    await HandleDeleteAllAsync(result.SelectedGroup);
                    break;
            }
        }
    }

    private void ShowEmptyPrompt()
    {
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.WriteLine($"No {(_type == DownloadType.Chapter ? "chapters" : "episodes")} downloaded yet.");
        System.Console.ResetColor();
        System.Console.WriteLine();
        System.Console.WriteLine($"Download with: koware download \"<title>\" --{(_type == DownloadType.Chapter ? "chapter" : "episode")} <n>");
    }

    private (OfflineAction Action, ContentGroup? SelectedGroup) ShowContentSelector(List<ContentGroup> groups)
    {
        var displayItems = groups.Select(g => new ContentDisplayItem(g, _type)).ToList();

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        var state = new OfflineSelectorState(displayItems, buffer, _type);

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
                    return (OfflineAction.Play, state.SelectedGroup);

                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    buffer.Restore();
                    return (OfflineAction.Exit, null);

                case ConsoleKey.D:
                    buffer.Restore();
                    return (OfflineAction.DeleteAll, state.SelectedGroup);

                case ConsoleKey.C:
                    buffer.Restore();
                    return (OfflineAction.Cleanup, null);

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

    private async Task ShowEpisodeSelector(ContentGroup group)
    {
        var availableItems = group.Items.Where(d => d.Exists).ToList();

        if (availableItems.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine("No available files. Run cleanup to remove stale entries.");
            System.Console.ResetColor();
            await Task.Delay(1000, _cancellationToken);
            return;
        }

        var itemLabel = _type == DownloadType.Chapter ? "Chapter" : "Episode";
        var options = availableItems.Select(d => (
            Entry: d,
            Label: $"{itemLabel} {d.Number}" + (d.Quality != null ? $" [{d.Quality}]" : "") + $"  ({FormatFileSize(d.FileSizeBytes)})"
        )).ToList();

        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"  {group.Title}");
        System.Console.ResetColor();
        System.Console.WriteLine();

        var result = InteractiveSelect.Show(
            options,
            o => o.Label,
            new SelectorOptions<(DownloadEntry Entry, string Label)>
            {
                Prompt = $"Select {itemLabel.ToLowerInvariant()} to play",
                MaxVisibleItems = 12,
                ShowSearch = true,
                ShowPreview = false
            });

        if (result.Cancelled)
        {
            return;
        }

        if (_onPlay is not null)
        {
            await _onPlay(result.Selected.Entry);
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Play} Playing: {group.Title} - {itemLabel} {result.Selected.Entry.Number}");
            System.Console.ResetColor();
            System.Console.WriteLine($"  Path: {result.Selected.Entry.FilePath}");
        }
    }

    private async Task HandleDeleteAllAsync(ContentGroup group)
    {
        System.Console.WriteLine();
        var confirm = InteractiveSelect.Confirm(
            $"Delete all {group.Items.Count} downloaded {(_type == DownloadType.Chapter ? "chapters" : "episodes")} of '{group.Title}'?");

        if (!confirm)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("Cancelled.");
            System.Console.ResetColor();
            return;
        }

        var deleted = 0;
        foreach (var item in group.Items)
        {
            try
            {
                if (System.IO.File.Exists(item.FilePath))
                {
                    System.IO.File.Delete(item.FilePath);
                }
                await _store.RemoveAsync(item.Id, _cancellationToken);
                deleted++;
            }
            catch
            {
                // Continue with others
            }
        }

        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($"{Icons.Success} Deleted {deleted} {(_type == DownloadType.Chapter ? "chapters" : "episodes")} from '{group.Title}'");
        System.Console.ResetColor();

        await Task.Delay(500, _cancellationToken);
    }

    private async Task HandleCleanupAsync()
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.Write("Cleaning up missing files...");
        System.Console.ResetColor();

        var removed = await _store.CleanupMissingAsync(_cancellationToken);

        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($" {Icons.Success} Removed {removed} stale entries");
        System.Console.ResetColor();

        await Task.Delay(500, _cancellationToken);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {suffixes[order]}";
    }

    private sealed class ContentGroup
    {
        public string ContentId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public List<DownloadEntry> Items { get; init; } = new();
        public long TotalSize { get; init; }
        public int AvailableCount { get; init; }
        public int MissingCount { get; init; }
    }

    private sealed class ContentDisplayItem
    {
        public ContentGroup Group { get; }
        public string DisplayText { get; }
        public string SearchText { get; }

        public ContentDisplayItem(ContentGroup group, DownloadType type)
        {
            Group = group;
            var itemLabel = type == DownloadType.Chapter ? "ch" : "ep";
            var count = group.Items.Count;
            var ranges = FormatNumberRanges(group.Items.Select(d => d.Number).ToList());
            DisplayText = $"{group.Title}";
            SearchText = group.Title.ToLowerInvariant();
        }

        private static string FormatNumberRanges(List<int> numbers)
        {
            if (numbers.Count == 0) return "";
            numbers = numbers.OrderBy(n => n).ToList();

            var ranges = new List<string>();
            var start = numbers[0];
            var end = start;

            for (var i = 1; i < numbers.Count; i++)
            {
                if (numbers[i] == end + 1)
                {
                    end = numbers[i];
                }
                else
                {
                    ranges.Add(start == end ? $"{start}" : $"{start}-{end}");
                    start = numbers[i];
                    end = start;
                }
            }
            ranges.Add(start == end ? $"{start}" : $"{start}-{end}");

            return string.Join(", ", ranges);
        }
    }

    private sealed class OfflineSelectorState
    {
        private readonly List<ContentDisplayItem> _allItems;
        private readonly TerminalBuffer _buffer;
        private readonly DownloadType _type;
        private List<ContentDisplayItem> _filtered;
        private string _searchText = "";
        private int _selectedIndex;
        private int _scrollOffset;

        public int MaxVisible { get; }
        public ContentGroup SelectedGroup => _filtered.Count > 0 && _selectedIndex < _filtered.Count
            ? _filtered[_selectedIndex].Group
            : _allItems[0].Group;

        public OfflineSelectorState(List<ContentDisplayItem> items, TerminalBuffer buffer, DownloadType type)
        {
            _allItems = items;
            _buffer = buffer;
            _type = type;
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
            var itemLabel = _type == DownloadType.Chapter ? "Manga" : "Anime";
            var unitLabel = _type == DownloadType.Chapter ? "ch" : "ep";

            // Header
            _buffer.SetColor(ConsoleColor.Cyan);
            _buffer.Write($" {Icons.Download} Downloaded {itemLabel}");
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
            _buffer.Write("|");
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

                    // Count badge
                    _buffer.SetColor(ConsoleColor.Green);
                    var count = item.Group.Items.Count;
                    _buffer.Write($"[{count} {unitLabel}] ");

                    // Size
                    _buffer.SetColor(ConsoleColor.DarkGray);
                    _buffer.Write($"{FormatFileSize(item.Group.TotalSize),-10} ");

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

                    // Missing indicator
                    if (item.Group.MissingCount > 0)
                    {
                        _buffer.SetColor(ConsoleColor.Yellow);
                        _buffer.Write($" {Icons.Warning}");
                    }

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
            _buffer.Write(" [Enter] Browse  [d] Delete All  [c] Cleanup  [Esc] Exit");
            _buffer.ResetColor();
            _buffer.WriteLine();
            lines++;

            _buffer.EndFrame(0, lines);
        }

        private static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            var order = 0;
            double size = bytes;
            while (size >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.#} {suffixes[order]}";
        }
    }
}
