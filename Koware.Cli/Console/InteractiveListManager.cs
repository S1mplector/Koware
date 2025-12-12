// Author: Ilgaz Mehmetoğlu
// Interactive list manager with fuzzy search and action menus.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koware.Cli.History;

namespace Koware.Cli.Console;

/// <summary>
/// Action result from the interactive list manager.
/// </summary>
public enum ListAction
{
    None,
    Exit,
    Play,
    UpdateStatus,
    EditProgress,
    Delete,
    Add,
    Refresh
}

/// <summary>
/// Result of an action in the list manager.
/// </summary>
public sealed class ListActionResult
{
    public ListAction Action { get; init; }
    public object? Entry { get; init; }
    public bool Cancelled { get; init; }

    public static ListActionResult Cancel() => new() { Cancelled = true, Action = ListAction.None };
    public static ListActionResult ExitList() => new() { Action = ListAction.Exit };
}

/// <summary>
/// Interactive fuzzy list manager for anime/manga tracking.
/// Provides fzf-like navigation with action menus.
/// </summary>
public sealed class InteractiveListManager
{
    private readonly IAnimeListStore _store;
    private readonly Func<AnimeListEntry, Task>? _onPlay;
    private List<AnimeListEntry> _entries = new();
    private readonly CancellationToken _cancellationToken;

    public InteractiveListManager(
        IAnimeListStore store,
        Func<AnimeListEntry, Task>? onPlay = null,
        CancellationToken cancellationToken = default)
    {
        _store = store;
        _onPlay = onPlay;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Run the interactive list manager.
    /// </summary>
    public async Task<int> RunAsync()
    {
        while (true)
        {
            // Refresh entries
            _entries = (await _store.GetAllAsync(null, _cancellationToken)).ToList();

            if (_entries.Count == 0)
            {
                var addNew = ShowEmptyListPrompt();
                if (addNew)
                {
                    // Return to caller to handle add
                    return 0;
                }
                return 0;
            }

            // Show main list selector
            var result = ShowListSelector();

            if (result.Cancelled)
            {
                return 0;
            }

            if (result.Action == ListAction.Exit)
            {
                return 0;
            }

            if (result.Action == ListAction.Add)
            {
                System.Console.WriteLine();
                System.Console.ForegroundColor = ConsoleColor.Cyan;
                System.Console.WriteLine("To add an anime, use: koware list add \"<title>\"");
                System.Console.ResetColor();
                continue;
            }

            if (result.Entry is not AnimeListEntry entry)
            {
                continue;
            }

            // Handle action
            switch (result.Action)
            {
                case ListAction.Play:
                    if (_onPlay is not null)
                    {
                        await _onPlay(entry);
                    }
                    break;

                case ListAction.UpdateStatus:
                    await HandleStatusChangeAsync(entry);
                    break;

                case ListAction.EditProgress:
                    await HandleProgressEditAsync(entry);
                    break;

                case ListAction.Delete:
                    await HandleDeleteAsync(entry);
                    break;
            }
        }
    }

    private bool ShowEmptyListPrompt()
    {
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.WriteLine("Your anime list is empty.");
        System.Console.ResetColor();
        System.Console.WriteLine();
        System.Console.WriteLine("To add anime to your list:");
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("  koware list add \"<anime title>\"");
        System.Console.ResetColor();
        System.Console.WriteLine();
        return false;
    }

    private ListActionResult ShowListSelector()
    {
        // Build display items with status indicators
        var displayItems = _entries.Select(e => new ListDisplayItem(e)).ToList();

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        var state = new ListSelectorState(displayItems, buffer);

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
                    var actionResult = ShowActionMenu(state.SelectedEntry);
                    if (actionResult.Action != ListAction.None)
                    {
                        return actionResult;
                    }
                    // Re-init buffer and continue
                    buffer.Initialize();
                    state.Render();
                    continue;

                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    buffer.Restore();
                    return ListActionResult.ExitList();

                case ConsoleKey.A:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.Add };

                case ConsoleKey.D:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.Delete, Entry = state.SelectedEntry };

                case ConsoleKey.S:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.UpdateStatus, Entry = state.SelectedEntry };

                case ConsoleKey.P:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.Play, Entry = state.SelectedEntry };

                case ConsoleKey.E:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.EditProgress, Entry = state.SelectedEntry };

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

    private ListActionResult ShowActionMenu(AnimeListEntry entry)
    {
        var actions = new List<(string Label, ListAction Action)>
        {
            ($"{Icons.Play} Play / Continue", ListAction.Play),
            ($"[~] Change Status", ListAction.UpdateStatus),
            ($"[#] Edit Progress / Score", ListAction.EditProgress),
            ($"{Icons.Error} Remove from List", ListAction.Delete)
        };

        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"  {entry.AnimeTitle}");
        System.Console.ResetColor();
        System.Console.ForegroundColor = entry.Status.ToColor();
        System.Console.WriteLine($"  [{entry.Status.ToDisplayString()}] {FormatProgress(entry)}");
        System.Console.ResetColor();
        System.Console.WriteLine();

        var result = InteractiveSelect.Show(
            actions,
            a => a.Label,
            new SelectorOptions<(string Label, ListAction Action)>
            {
                Prompt = "Select action",
                MaxVisibleItems = 6,
                ShowSearch = false,
                ShowPreview = false
            });

        if (result.Cancelled)
        {
            return ListActionResult.Cancel();
        }

        return new ListActionResult { Action = result.Selected.Action, Entry = entry };
    }

    private async Task HandleStatusChangeAsync(AnimeListEntry entry)
    {
        var statuses = new List<(AnimeWatchStatus Status, string Label)>
        {
            (AnimeWatchStatus.Watching, $"{GetStatusIcon(AnimeWatchStatus.Watching)} Watching"),
            (AnimeWatchStatus.Completed, $"{GetStatusIcon(AnimeWatchStatus.Completed)} Completed"),
            (AnimeWatchStatus.PlanToWatch, $"{GetStatusIcon(AnimeWatchStatus.PlanToWatch)} Plan to Watch"),
            (AnimeWatchStatus.OnHold, $"{GetStatusIcon(AnimeWatchStatus.OnHold)} On Hold"),
            (AnimeWatchStatus.Dropped, $"{GetStatusIcon(AnimeWatchStatus.Dropped)} Dropped")
        };

        System.Console.WriteLine();
        System.Console.WriteLine($"  Current status: {entry.Status.ToDisplayString()}");
        System.Console.WriteLine();

        var result = InteractiveSelect.Show(
            statuses,
            s => s.Label,
            new SelectorOptions<(AnimeWatchStatus Status, string Label)>
            {
                Prompt = "Select new status",
                MaxVisibleItems = 6,
                ShowSearch = false,
                ShowPreview = false
            });

        if (result.Cancelled)
        {
            return;
        }

        var newStatus = result.Selected.Status;
        if (newStatus == entry.Status)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("Status unchanged.");
            System.Console.ResetColor();
            return;
        }

        var success = await _store.UpdateAsync(entry.AnimeTitle, status: newStatus, cancellationToken: _cancellationToken);
        if (success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Success} Updated '{entry.AnimeTitle}' to '{newStatus.ToDisplayString()}'");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"{Icons.Error} Failed to update status.");
            System.Console.ResetColor();
        }

        await Task.Delay(500, _cancellationToken);
    }

    private async Task HandleProgressEditAsync(AnimeListEntry entry)
    {
        System.Console.WriteLine();
        System.Console.WriteLine($"  {entry.AnimeTitle}");
        System.Console.WriteLine($"  Current progress: {FormatProgress(entry)}");
        if (entry.Score.HasValue)
        {
            System.Console.WriteLine($"  Current score: {entry.Score}/10");
        }
        System.Console.WriteLine();

        // Episodes watched
        System.Console.Write("  Episodes watched (blank to skip): ");
        var epInput = System.Console.ReadLine()?.Trim();
        int? newEpisodes = null;
        if (!string.IsNullOrEmpty(epInput) && int.TryParse(epInput, out var ep) && ep >= 0)
        {
            newEpisodes = ep;
        }

        // Score
        System.Console.Write("  Score 1-10 (blank to skip): ");
        var scoreInput = System.Console.ReadLine()?.Trim();
        int? newScore = null;
        if (!string.IsNullOrEmpty(scoreInput) && int.TryParse(scoreInput, out var sc) && sc >= 1 && sc <= 10)
        {
            newScore = sc;
        }

        if (newEpisodes is null && newScore is null)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("No changes made.");
            System.Console.ResetColor();
            return;
        }

        var success = await _store.UpdateAsync(
            entry.AnimeTitle,
            episodesWatched: newEpisodes,
            score: newScore,
            cancellationToken: _cancellationToken);

        if (success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Success} Updated '{entry.AnimeTitle}'");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"{Icons.Error} Failed to update.");
            System.Console.ResetColor();
        }

        await Task.Delay(500, _cancellationToken);
    }

    private async Task HandleDeleteAsync(AnimeListEntry entry)
    {
        System.Console.WriteLine();
        var confirm = InteractiveSelect.Confirm($"Remove '{entry.AnimeTitle}' from your list?");

        if (!confirm)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("Cancelled.");
            System.Console.ResetColor();
            return;
        }

        var success = await _store.RemoveAsync(entry.AnimeTitle, _cancellationToken);
        if (success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Success} Removed '{entry.AnimeTitle}' from your list.");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"{Icons.Error} Failed to remove.");
            System.Console.ResetColor();
        }

        await Task.Delay(500, _cancellationToken);
    }

    private static string FormatProgress(AnimeListEntry entry)
    {
        var progress = entry.TotalEpisodes.HasValue
            ? $"{entry.EpisodesWatched}/{entry.TotalEpisodes}"
            : $"{entry.EpisodesWatched}/?";

        var score = entry.Score.HasValue ? $" ★{entry.Score}" : "";
        return progress + score;
    }

    private static string GetStatusIcon(AnimeWatchStatus status) => status switch
    {
        AnimeWatchStatus.Watching => "▶",
        AnimeWatchStatus.Completed => "✓",
        AnimeWatchStatus.PlanToWatch => "○",
        AnimeWatchStatus.OnHold => "⏸",
        AnimeWatchStatus.Dropped => "✗",
        _ => "·"
    };

    /// <summary>
    /// Display item wrapper for list entries.
    /// </summary>
    private sealed class ListDisplayItem
    {
        public AnimeListEntry Entry { get; }
        public string DisplayText { get; }
        public string SearchText { get; }

        public ListDisplayItem(AnimeListEntry entry)
        {
            Entry = entry;
            var progress = FormatProgress(entry);
            DisplayText = $"[{entry.Status.ToDisplayString(),-13}] {progress,-12} {entry.AnimeTitle}";
            SearchText = entry.AnimeTitle.ToLowerInvariant();
        }
    }

    /// <summary>
    /// State manager for the list selector UI.
    /// </summary>
    private sealed class ListSelectorState
    {
        private readonly List<ListDisplayItem> _allItems;
        private readonly TerminalBuffer _buffer;
        private List<ListDisplayItem> _filtered;
        private string _searchText = "";
        private int _selectedIndex;
        private int _scrollOffset;

        public int MaxVisible { get; } = 12;

        public AnimeListEntry SelectedEntry => _filtered.Count > 0 && _selectedIndex < _filtered.Count
            ? _filtered[_selectedIndex].Entry
            : _allItems[0].Entry;

        public ListSelectorState(List<ListDisplayItem> items, TerminalBuffer buffer)
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
            if (_selectedIndex < _scrollOffset)
            {
                _scrollOffset = _selectedIndex;
            }
        }

        public void MoveDown(int count = 1)
        {
            _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + count);
            if (_selectedIndex >= _scrollOffset + MaxVisible)
            {
                _scrollOffset = _selectedIndex - MaxVisible + 1;
            }
        }

        public void JumpToStart()
        {
            _selectedIndex = 0;
            _scrollOffset = 0;
        }

        public void JumpToEnd()
        {
            _selectedIndex = Math.Max(0, _filtered.Count - 1);
            _scrollOffset = Math.Max(0, _filtered.Count - MaxVisible);
        }

        public void SearchAppend(char c)
        {
            _searchText += c;
            UpdateFilter();
        }

        public void SearchBackspace()
        {
            if (_searchText.Length > 0)
            {
                _searchText = _searchText[..^1];
                UpdateFilter();
            }
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

            if (_selectedIndex >= _filtered.Count)
            {
                _selectedIndex = Math.Max(0, _filtered.Count - 1);
            }
            if (_scrollOffset > _selectedIndex)
            {
                _scrollOffset = _selectedIndex;
            }
        }

        public void Render()
        {
            _buffer.ClearFullScreen();
            _buffer.MoveTo(0, 0);

            var width = GetTerminalWidth();

            // Header
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine($" Anime List ({_allItems.Count} entries)");
            System.Console.ResetColor();
            System.Console.WriteLine(new string('─', Math.Min(width, 80)));

            // Search box
            System.Console.Write(" Filter: ");
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine(_searchText + "_");
            System.Console.ResetColor();
            System.Console.WriteLine(new string('─', Math.Min(width, 80)));

            // Items
            var visibleItems = _filtered.Skip(_scrollOffset).Take(MaxVisible).ToList();
            for (var i = 0; i < MaxVisible; i++)
            {
                if (i < visibleItems.Count)
                {
                    var item = visibleItems[i];
                    var isSelected = (_scrollOffset + i) == _selectedIndex;

                    if (isSelected)
                    {
                        System.Console.ForegroundColor = ConsoleColor.Black;
                        System.Console.BackgroundColor = ConsoleColor.White;
                        System.Console.Write(" ▶ ");
                    }
                    else
                    {
                        System.Console.Write("   ");
                    }

                    // Status color
                    if (!isSelected)
                    {
                        System.Console.ForegroundColor = item.Entry.Status.ToColor();
                    }

                    var displayText = item.DisplayText;
                    if (displayText.Length > width - 4)
                    {
                        displayText = displayText[..(width - 7)] + "...";
                    }
                    System.Console.Write(displayText);

                    if (isSelected)
                    {
                        // Pad to clear background
                        var padding = Math.Max(0, width - 3 - displayText.Length);
                        System.Console.Write(new string(' ', padding));
                    }

                    System.Console.ResetColor();
                    System.Console.WriteLine();
                }
                else
                {
                    System.Console.WriteLine();
                }
            }

            // Scroll indicator
            if (_filtered.Count > MaxVisible)
            {
                var scrollPercent = _filtered.Count > MaxVisible
                    ? (int)((_scrollOffset / (float)(_filtered.Count - MaxVisible)) * 100)
                    : 0;
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.WriteLine($" [{_selectedIndex + 1}/{_filtered.Count}] {scrollPercent}%");
                System.Console.ResetColor();
            }
            else
            {
                System.Console.WriteLine();
            }

            // Footer
            System.Console.WriteLine(new string('─', Math.Min(width, 80)));
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine(" [Enter] Actions  [s] Status  [e] Edit  [d] Delete  [p] Play  [a] Add  [Esc] Exit");
            System.Console.ResetColor();
        }

        private static int GetTerminalWidth()
        {
            try { return System.Console.WindowWidth; }
            catch { return 80; }
        }
    }
}

/// <summary>
/// Interactive fuzzy list manager for manga tracking.
/// </summary>
public sealed class InteractiveMangaListManager
{
    private readonly IMangaListStore _store;
    private List<MangaListEntry> _entries = new();
    private readonly CancellationToken _cancellationToken;

    public InteractiveMangaListManager(
        IMangaListStore store,
        CancellationToken cancellationToken = default)
    {
        _store = store;
        _cancellationToken = cancellationToken;
    }

    public async Task<int> RunAsync()
    {
        while (true)
        {
            _entries = (await _store.GetAllAsync(null, _cancellationToken)).ToList();

            if (_entries.Count == 0)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("Your manga list is empty.");
                System.Console.ResetColor();
                System.Console.WriteLine();
                System.Console.WriteLine("To add manga to your list:");
                System.Console.ForegroundColor = ConsoleColor.Magenta;
                System.Console.WriteLine("  koware -m list add \"<manga title>\"");
                System.Console.ResetColor();
                return 0;
            }

            var result = ShowListSelector();

            if (result.Cancelled || result.Action == ListAction.Exit)
            {
                return 0;
            }

            if (result.Action == ListAction.Add)
            {
                System.Console.WriteLine();
                System.Console.ForegroundColor = ConsoleColor.Magenta;
                System.Console.WriteLine("To add manga, use: koware -m list add \"<title>\"");
                System.Console.ResetColor();
                continue;
            }

            if (result.Entry is null)
            {
                continue;
            }

            var entry = _entries.FirstOrDefault(e => e.MangaTitle == (result.Entry as MangaListEntry)?.MangaTitle);
            if (entry is null) continue;

            switch (result.Action)
            {
                case ListAction.UpdateStatus:
                    await HandleStatusChangeAsync(entry);
                    break;
                case ListAction.EditProgress:
                    await HandleProgressEditAsync(entry);
                    break;
                case ListAction.Delete:
                    await HandleDeleteAsync(entry);
                    break;
            }
        }
    }

    private ListActionResult ShowListSelector()
    {
        var displayItems = _entries.Select(e => new MangaDisplayItem(e)).ToList();

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        var state = new MangaSelectorState(displayItems, buffer);

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
                    var actionResult = ShowActionMenu(state.SelectedEntry);
                    if (actionResult.Action != ListAction.None)
                    {
                        return new ListActionResult { Action = actionResult.Action, Entry = state.SelectedEntry };
                    }
                    buffer.Initialize();
                    state.Render();
                    continue;

                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    buffer.Restore();
                    return ListActionResult.ExitList();

                case ConsoleKey.A:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.Add };

                case ConsoleKey.D:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.Delete, Entry = state.SelectedEntry };

                case ConsoleKey.S:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.UpdateStatus, Entry = state.SelectedEntry };

                case ConsoleKey.E:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.EditProgress, Entry = state.SelectedEntry };

                case ConsoleKey.R:
                    buffer.Restore();
                    return new ListActionResult { Action = ListAction.Play, Entry = state.SelectedEntry };

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

    private ListActionResult ShowActionMenu(MangaListEntry entry)
    {
        var actions = new List<(string Label, ListAction Action)>
        {
            ($"{Icons.Play} Read / Continue", ListAction.Play),
            ($"[~] Change Status", ListAction.UpdateStatus),
            ($"[#] Edit Progress / Score", ListAction.EditProgress),
            ($"{Icons.Error} Remove from List", ListAction.Delete)
        };

        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Magenta;
        System.Console.WriteLine($"  {entry.MangaTitle}");
        System.Console.ResetColor();
        System.Console.ForegroundColor = entry.Status.ToColor();
        System.Console.WriteLine($"  [{entry.Status.ToDisplayString()}] {FormatProgress(entry)}");
        System.Console.ResetColor();
        System.Console.WriteLine();

        var result = InteractiveSelect.Show(
            actions,
            a => a.Label,
            new SelectorOptions<(string Label, ListAction Action)>
            {
                Prompt = "Select action",
                MaxVisibleItems = 6,
                ShowSearch = false,
                ShowPreview = false
            });

        if (result.Cancelled)
        {
            return ListActionResult.Cancel();
        }

        return new ListActionResult { Action = result.Selected.Action };
    }

    private async Task HandleStatusChangeAsync(MangaListEntry entry)
    {
        var statuses = new List<(MangaReadStatus Status, string Label)>
        {
            (MangaReadStatus.Reading, "▶ Reading"),
            (MangaReadStatus.Completed, "✓ Completed"),
            (MangaReadStatus.PlanToRead, "○ Plan to Read"),
            (MangaReadStatus.OnHold, "⏸ On Hold"),
            (MangaReadStatus.Dropped, "✗ Dropped")
        };

        System.Console.WriteLine();
        System.Console.WriteLine($"  Current status: {entry.Status.ToDisplayString()}");
        System.Console.WriteLine();

        var result = InteractiveSelect.Show(
            statuses,
            s => s.Label,
            new SelectorOptions<(MangaReadStatus Status, string Label)>
            {
                Prompt = "Select new status",
                MaxVisibleItems = 6,
                ShowSearch = false,
                ShowPreview = false
            });

        if (result.Cancelled) return;

        var newStatus = result.Selected.Status;
        if (newStatus == entry.Status)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("Status unchanged.");
            System.Console.ResetColor();
            return;
        }

        var success = await _store.UpdateAsync(entry.MangaTitle, status: newStatus, cancellationToken: _cancellationToken);
        if (success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Success} Updated '{entry.MangaTitle}' to '{newStatus.ToDisplayString()}'");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"{Icons.Error} Failed to update status.");
            System.Console.ResetColor();
        }

        await Task.Delay(500, _cancellationToken);
    }

    private async Task HandleProgressEditAsync(MangaListEntry entry)
    {
        System.Console.WriteLine();
        System.Console.WriteLine($"  {entry.MangaTitle}");
        System.Console.WriteLine($"  Current progress: {FormatProgress(entry)}");
        if (entry.Score.HasValue)
        {
            System.Console.WriteLine($"  Current score: {entry.Score}/10");
        }
        System.Console.WriteLine();

        System.Console.Write("  Chapters read (blank to skip): ");
        var chInput = System.Console.ReadLine()?.Trim();
        int? newChapters = null;
        if (!string.IsNullOrEmpty(chInput) && int.TryParse(chInput, out var ch) && ch >= 0)
        {
            newChapters = ch;
        }

        System.Console.Write("  Score 1-10 (blank to skip): ");
        var scoreInput = System.Console.ReadLine()?.Trim();
        int? newScore = null;
        if (!string.IsNullOrEmpty(scoreInput) && int.TryParse(scoreInput, out var sc) && sc >= 1 && sc <= 10)
        {
            newScore = sc;
        }

        if (newChapters is null && newScore is null)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("No changes made.");
            System.Console.ResetColor();
            return;
        }

        var success = await _store.UpdateAsync(
            entry.MangaTitle,
            chaptersRead: newChapters,
            score: newScore,
            cancellationToken: _cancellationToken);

        if (success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Success} Updated '{entry.MangaTitle}'");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"{Icons.Error} Failed to update.");
            System.Console.ResetColor();
        }

        await Task.Delay(500, _cancellationToken);
    }

    private async Task HandleDeleteAsync(MangaListEntry entry)
    {
        System.Console.WriteLine();
        var confirm = InteractiveSelect.Confirm($"Remove '{entry.MangaTitle}' from your list?");

        if (!confirm)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("Cancelled.");
            System.Console.ResetColor();
            return;
        }

        var success = await _store.RemoveAsync(entry.MangaTitle, _cancellationToken);
        if (success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"{Icons.Success} Removed '{entry.MangaTitle}' from your list.");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"{Icons.Error} Failed to remove.");
            System.Console.ResetColor();
        }

        await Task.Delay(500, _cancellationToken);
    }

    private static string FormatProgress(MangaListEntry entry)
    {
        var progress = entry.TotalChapters.HasValue
            ? $"{entry.ChaptersRead}/{entry.TotalChapters}"
            : $"{entry.ChaptersRead}/?";
        var score = entry.Score.HasValue ? $" ★{entry.Score}" : "";
        return progress + score;
    }

    private sealed class MangaDisplayItem
    {
        public MangaListEntry Entry { get; }
        public string DisplayText { get; }
        public string SearchText { get; }

        public MangaDisplayItem(MangaListEntry entry)
        {
            Entry = entry;
            var progress = FormatProgress(entry);
            DisplayText = $"[{entry.Status.ToDisplayString(),-13}] {progress,-12} {entry.MangaTitle}";
            SearchText = entry.MangaTitle.ToLowerInvariant();
        }
    }

    private sealed class MangaSelectorState
    {
        private readonly List<MangaDisplayItem> _allItems;
        private readonly TerminalBuffer _buffer;
        private List<MangaDisplayItem> _filtered;
        private string _searchText = "";
        private int _selectedIndex;
        private int _scrollOffset;

        public int MaxVisible { get; }
        public MangaListEntry SelectedEntry => _filtered.Count > 0 && _selectedIndex < _filtered.Count
            ? _filtered[_selectedIndex].Entry
            : _allItems[0].Entry;

        public MangaSelectorState(List<MangaDisplayItem> items, TerminalBuffer buffer)
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
            _buffer.ClearFullScreen();
            _buffer.MoveTo(0, 0);

            var width = GetTerminalWidth();

            System.Console.ForegroundColor = ConsoleColor.Magenta;
            System.Console.WriteLine($" Manga List ({_allItems.Count} entries)");
            System.Console.ResetColor();
            System.Console.WriteLine(new string('─', Math.Min(width, 80)));

            System.Console.Write(" Filter: ");
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine(_searchText + "_");
            System.Console.ResetColor();
            System.Console.WriteLine(new string('─', Math.Min(width, 80)));

            var visibleItems = _filtered.Skip(_scrollOffset).Take(MaxVisible).ToList();
            for (var i = 0; i < MaxVisible; i++)
            {
                if (i < visibleItems.Count)
                {
                    var item = visibleItems[i];
                    var isSelected = (_scrollOffset + i) == _selectedIndex;

                    if (isSelected)
                    {
                        System.Console.ForegroundColor = ConsoleColor.Black;
                        System.Console.BackgroundColor = ConsoleColor.White;
                        System.Console.Write(" ▶ ");
                    }
                    else
                    {
                        System.Console.Write("   ");
                    }

                    if (!isSelected) System.Console.ForegroundColor = item.Entry.Status.ToColor();

                    var displayText = item.DisplayText;
                    if (displayText.Length > width - 4) displayText = displayText[..(width - 7)] + "...";
                    System.Console.Write(displayText);

                    if (isSelected)
                    {
                        var padding = Math.Max(0, width - 3 - displayText.Length);
                        System.Console.Write(new string(' ', padding));
                    }

                    System.Console.ResetColor();
                    System.Console.WriteLine();
                }
                else
                {
                    System.Console.WriteLine();
                }
            }

            if (_filtered.Count > MaxVisible)
            {
                var scrollPercent = _filtered.Count > MaxVisible
                    ? (int)((_scrollOffset / (float)(_filtered.Count - MaxVisible)) * 100)
                    : 0;
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.WriteLine($" [{_selectedIndex + 1}/{_filtered.Count}] {scrollPercent}%");
                System.Console.ResetColor();
            }
            else
            {
                System.Console.WriteLine();
            }

            System.Console.WriteLine(new string('─', Math.Min(width, 80)));
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine(" [Enter] Actions  [s] Status  [e] Edit  [d] Delete  [r] Read  [a] Add  [Esc] Exit");
            System.Console.ResetColor();
        }

        private static int GetTerminalWidth()
        {
            try { return System.Console.WindowWidth; }
            catch { return 80; }
        }
    }
}
