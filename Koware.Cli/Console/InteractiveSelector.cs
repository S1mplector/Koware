// Author: Ilgaz Mehmetoğlu
// Interactive TUI selector with arrow-key navigation and fuzzy search.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Koware.Cli.Config;

namespace Koware.Cli.Console;

/// <summary>
/// Result of an interactive selection.
/// </summary>
/// <typeparam name="T">Type of the selected item.</typeparam>
public sealed class SelectionResult<T>
{
    public bool Cancelled { get; init; }
    public T? Selected { get; init; }
    public int SelectedIndex { get; init; } = -1;

    public static SelectionResult<T> Cancel() => new() { Cancelled = true };
    public static SelectionResult<T> Success(T item, int index) => new() { Selected = item, SelectedIndex = index };
}


/// <summary>
/// Status indicator for list items (watched, downloaded, etc.)
/// </summary>
public enum ItemStatus
{
    None,
    Watched,
    Downloaded,
    InProgress,
    New
}

/// <summary>
/// Interactive fuzzy selector with arrow-key navigation, similar to fzf.
/// Uses component-based architecture: TerminalBuffer for rendering, 
/// InputHandler for input, FuzzyMatcher for search, SelectorRenderer for display.
/// </summary>
/// <typeparam name="T">Type of items to select from.</typeparam>
public sealed class InteractiveSelector<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Func<T, string> _displayFunc;
    private readonly Func<T, string>? _previewFunc;
    private readonly Func<T, ItemStatus>? _statusFunc;
    private readonly RenderConfig _renderConfig;
    private readonly string _emptyMessage;

    // State
    private List<(T Item, int OriginalIndex, int Score)> _filtered = new();
    private string _searchText = "";
    private int _selectedIndex;
    private int _scrollOffset;

    /// <summary>
    /// Create a new interactive selector.
    /// </summary>
    public InteractiveSelector(
        IReadOnlyList<T> items,
        Func<T, string> displayFunc,
        SelectorOptions<T>? options = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _displayFunc = displayFunc ?? throw new ArgumentNullException(nameof(displayFunc));
        _previewFunc = options?.PreviewFunc ?? options?.SecondaryDisplayFunc;
        _statusFunc = options?.StatusFunc;
        _emptyMessage = options?.EmptyMessage ?? "No items found";

        _renderConfig = new RenderConfig
        {
            Prompt = options?.Prompt ?? "Select",
            MaxVisibleItems = Math.Min(options?.MaxVisibleItems ?? 10, Math.Max(3, GetTerminalHeight() - 8)),
            ShowSearch = options?.ShowSearch ?? true,
            ShowPreview = options?.ShowPreview ?? (_previewFunc != null),
            ShowFooter = false,  // No footer - fzf-style minimal UI
            HighlightColor = options?.GetHighlightColor() ?? Theme.Highlight,
            SelectionColor = options?.GetSelectionColor() ?? Theme.Selection
        };
    }

    private static int GetTerminalHeight()
    {
        try { return System.Console.WindowHeight; }
        catch { return 24; }
    }

    /// <summary>
    /// Run the interactive selector and return the selection.
    /// </summary>
    public SelectionResult<T> Run()
    {
        if (_items.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine(_emptyMessage);
            System.Console.ResetColor();
            return SelectionResult<T>.Cancel();
        }

        // Initialize components - use alternate screen for clean TUI
        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        var renderer = new SelectorRenderer(buffer, _renderConfig);
        var inputHandler = new InputHandler(_renderConfig.ShowSearch);

        // Initialize filtered list
        UpdateFilter();

        // Setup terminal and reserve space
        buffer.Initialize();
        var totalLinesNeeded = renderer.CalculateTotalLines();
        var startLine = buffer.ReserveSpace(totalLinesNeeded);

        var result = SelectionResult<T>.Cancel();

        try
        {
            RenderFrame(renderer, startLine);

            while (true)
            {
                // Check for terminal resize before blocking on input
                if (buffer.CheckResize())
                {
                    if (buffer.IsAlternateScreen)
                    {
                        buffer.ClearFullScreen();
                        startLine = 0;
                    }
                    else
                    {
                        startLine = buffer.ReserveSpace(totalLinesNeeded);
                    }
                    RenderFrame(renderer, startLine);
                }

                var input = inputHandler.ReadKey(!string.IsNullOrEmpty(_searchText));

                // Check for resize after input (terminal might have resized while waiting)
                if (buffer.CheckResize())
                {
                    if (buffer.IsAlternateScreen)
                    {
                        buffer.ClearFullScreen();
                        startLine = 0;
                    }
                    else
                    {
                        startLine = buffer.ReserveSpace(totalLinesNeeded);
                    }
                }

                switch (input.Action)
                {
                    case InputAction.MoveUp:
                        MoveUp();
                        break;

                    case InputAction.MoveDown:
                        MoveDown();
                        break;

                    case InputAction.PageUp:
                        MoveUp(_renderConfig.MaxVisibleItems);
                        break;

                    case InputAction.PageDown:
                        MoveDown(_renderConfig.MaxVisibleItems);
                        break;

                    case InputAction.JumpToStart:
                        _selectedIndex = 0;
                        _scrollOffset = 0;
                        break;

                    case InputAction.JumpToEnd:
                        _selectedIndex = Math.Max(0, _filtered.Count - 1);
                        _scrollOffset = Math.Max(0, _filtered.Count - _renderConfig.MaxVisibleItems);
                        break;

                    case InputAction.Select:
                        if (_filtered.Count > 0 && _selectedIndex >= 0 && _selectedIndex < _filtered.Count)
                        {
                            var selected = _filtered[_selectedIndex];
                            result = SelectionResult<T>.Success(selected.Item, selected.OriginalIndex);
                        }
                        return result;

                    case InputAction.Cancel:
                        return SelectionResult<T>.Cancel();

                    case InputAction.SearchBackspace:
                        if (_searchText.Length > 0)
                        {
                            _searchText = _searchText[..^1];
                            UpdateFilter();
                        }
                        break;

                    case InputAction.SearchCharacter:
                        if (input.Character.HasValue)
                        {
                            _searchText += input.Character.Value;
                            UpdateFilter();
                        }
                        break;

                    case InputAction.QuickJump:
                        if (input.JumpIndex.HasValue && input.JumpIndex.Value < _filtered.Count)
                        {
                            var jumpItem = _filtered[input.JumpIndex.Value];
                            return SelectionResult<T>.Success(jumpItem.Item, jumpItem.OriginalIndex);
                        }
                        break;
                }

                RenderFrame(renderer, startLine);
            }
        }
        finally
        {
            buffer.ClearArea(startLine, totalLinesNeeded);
            buffer.Restore();
        }
    }

    private void RenderFrame(SelectorRenderer renderer, int startLine)
    {
        var renderItems = _filtered
            .Skip(_scrollOffset)
            .Take(_renderConfig.MaxVisibleItems)
            .Select(f => (
                Display: _displayFunc(f.Item),
                OriginalIndex: f.OriginalIndex,
                Status: _statusFunc?.Invoke(f.Item) ?? ItemStatus.None,
                Preview: _previewFunc?.Invoke(f.Item)
            ))
            .ToList();

        // Pad to max visible if we have fewer items
        while (renderItems.Count < _renderConfig.MaxVisibleItems)
        {
            renderItems.Add(("", -1, ItemStatus.None, null));
        }

        var state = new RenderState
        {
            Items = renderItems!,
            TotalCount = _items.Count,
            FilteredCount = _filtered.Count,  // Actual filtered count, not padded
            SelectedIndex = _selectedIndex - _scrollOffset,
            ScrollOffset = _scrollOffset,
            SearchText = _searchText
        };

        renderer.Render(state, startLine);
    }

    private void MoveUp(int count = 1)
    {
        _selectedIndex = Math.Max(0, _selectedIndex - count);
        if (_selectedIndex < _scrollOffset)
        {
            _scrollOffset = _selectedIndex;
        }
    }

    private void MoveDown(int count = 1)
    {
        _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + count);
        if (_selectedIndex >= _scrollOffset + _renderConfig.MaxVisibleItems)
        {
            _scrollOffset = _selectedIndex - _renderConfig.MaxVisibleItems + 1;
        }
    }

    private void UpdateFilter()
    {
        // Use FuzzyMatcher for filtering
        _filtered = FuzzyMatcher.Filter(_items, _displayFunc, _searchText).ToList();

        // Reset selection if needed
        if (_selectedIndex >= _filtered.Count)
        {
            _selectedIndex = Math.Max(0, _filtered.Count - 1);
        }
        if (_scrollOffset > _selectedIndex)
        {
            _scrollOffset = _selectedIndex;
        }
    }
}

/// <summary>
/// Configuration options for InteractiveSelector.
/// </summary>
/// <typeparam name="T">Type of items.</typeparam>
public sealed class SelectorOptions<T>
{
    /// <summary>Prompt text shown at the top.</summary>
    public string? Prompt { get; init; }

    /// <summary>Maximum number of items to show at once.</summary>
    public int MaxVisibleItems { get; init; } = 10;

    /// <summary>Whether to show the search/filter box.</summary>
    public bool ShowSearch { get; init; } = true;

    /// <summary>Whether to show the preview pane.</summary>
    public bool ShowPreview { get; init; } = true;

    /// <summary>Color for highlighted/matching text.</summary>
    public ConsoleColor? HighlightColor { get; init; }

    /// <summary>Color for the selected item.</summary>
    public ConsoleColor? SelectedColor { get; init; }

    /// <summary>Get effective highlight color (theme or override).</summary>
    public ConsoleColor GetHighlightColor() => HighlightColor ?? Theme.Highlight;

    /// <summary>Get effective selection color (theme or override).</summary>
    public ConsoleColor GetSelectionColor() => SelectedColor ?? Theme.Selection;

    /// <summary>Message shown when no items match.</summary>
    public string? EmptyMessage { get; init; }

    /// <summary>Optional function for secondary display text (legacy, use PreviewFunc).</summary>
    public Func<T, string>? SecondaryDisplayFunc { get; init; }

    /// <summary>Optional function for preview pane content.</summary>
    public Func<T, string>? PreviewFunc { get; init; }

    /// <summary>Optional function to get item status (watched, downloaded, etc.).</summary>
    public Func<T, ItemStatus>? StatusFunc { get; init; }
}

/// <summary>
/// Helper methods for interactive selection.
/// </summary>
public static class InteractiveSelect
{
    /// <summary>
    /// Show an interactive selector for a list of items.
    /// </summary>
    /// <typeparam name="T">Type of items.</typeparam>
    /// <param name="items">Items to select from.</param>
    /// <param name="displayFunc">Function to get display text.</param>
    /// <param name="prompt">Optional prompt text.</param>
    /// <returns>Selected item or null if cancelled.</returns>
    public static T? Show<T>(IReadOnlyList<T> items, Func<T, string> displayFunc, string? prompt = null) where T : class
    {
        var selector = new InteractiveSelector<T>(items, displayFunc, new SelectorOptions<T>
        {
            Prompt = prompt ?? "Select an item"
        });
        var result = selector.Run();
        return result.Cancelled ? null : result.Selected;
    }

    /// <summary>
    /// Show an interactive selector with custom options.
    /// </summary>
    public static SelectionResult<T> Show<T>(IReadOnlyList<T> items, Func<T, string> displayFunc, SelectorOptions<T> options)
    {
        var selector = new InteractiveSelector<T>(items, displayFunc, options);
        return selector.Run();
    }

    /// <summary>
    /// Show a simple yes/no confirmation prompt.
    /// </summary>
    /// <param name="message">Question to ask.</param>
    /// <param name="defaultYes">Default to yes if true.</param>
    /// <returns>True if user selected yes.</returns>
    public static bool Confirm(string message, bool defaultYes = false)
    {
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.Write($"{Icons.Prompt} {message} ");
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.Write(defaultYes ? "[Y/n] " : "[y/N] ");
        System.Console.ResetColor();

        var key = System.Console.ReadKey(intercept: true);
        System.Console.WriteLine();

        return key.Key switch
        {
            ConsoleKey.Y => true,
            ConsoleKey.N => false,
            ConsoleKey.Enter => defaultYes,
            _ => defaultYes
        };
    }

    /// <summary>
    /// Show an interactive number range selector.
    /// </summary>
    /// <param name="prompt">Prompt text.</param>
    /// <param name="min">Minimum allowed value.</param>
    /// <param name="max">Maximum allowed value.</param>
    /// <param name="defaultValue">Default value.</param>
    /// <returns>Selected number or null if cancelled.</returns>
    public static int? SelectNumber(string prompt, int min, int max, int? defaultValue = null)
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.Write($"{Icons.Prompt} {prompt} ");
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.Write($"[{min}-{max}]");
        if (defaultValue.HasValue)
        {
            System.Console.Write($" (default: {defaultValue})");
        }
        System.Console.Write(": ");
        System.Console.ResetColor();

        var input = System.Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultValue;
        }

        if (int.TryParse(input, out var value) && value >= min && value <= max)
        {
            return value;
        }

        System.Console.ForegroundColor = ConsoleColor.Red;
        System.Console.WriteLine($"Invalid input. Please enter a number between {min} and {max}.");
        System.Console.ResetColor();
        return null;
    }
}

/// <summary>
/// Episode information for the episode browser.
/// </summary>
public sealed class EpisodeItem
{
    public int Number { get; init; }
    public string? Title { get; init; }
    public bool IsWatched { get; init; }
    public bool IsDownloaded { get; init; }
    public string? FilePath { get; init; }
}

/// <summary>
/// History entry for the history browser.
/// </summary>
public sealed class HistoryItem
{
    public string Title { get; init; } = "";
    public int LastEpisode { get; init; }
    public int? TotalEpisodes { get; init; }
    public DateTimeOffset WatchedAt { get; init; }
    public string? Provider { get; init; }
    public string? Quality { get; init; }
}

/// <summary>
/// Specialized interactive browsers for episodes and history.
/// </summary>
public static class InteractiveBrowser
{
    /// <summary>
    /// Show an episode browser with watched/downloaded indicators.
    /// </summary>
    /// <param name="episodes">List of episodes.</param>
    /// <param name="title">Anime/manga title for header.</param>
    /// <returns>Selected episode number or null if cancelled.</returns>
    public static int? BrowseEpisodes(IReadOnlyList<EpisodeItem> episodes, string title)
    {
        if (episodes.Count == 0) return null;

        var result = InteractiveSelect.Show(
            episodes,
            ep => $"Episode {ep.Number}" + (string.IsNullOrWhiteSpace(ep.Title) ? "" : $" - {ep.Title}"),
            new SelectorOptions<EpisodeItem>
            {
                Prompt = $"📺 {title}",
                MaxVisibleItems = 15,
                ShowSearch = true,
                ShowPreview = false,
                StatusFunc = ep =>
                {
                    if (ep.IsDownloaded) return ItemStatus.Downloaded;
                    if (ep.IsWatched) return ItemStatus.Watched;
                    return ItemStatus.None;
                }
            });

        return result.Cancelled ? null : result.Selected?.Number;
    }

    /// <summary>
    /// Show a chapter browser with read/downloaded indicators.
    /// </summary>
    /// <param name="chapters">List of chapters (as EpisodeItem with Number as float-compatible int).</param>
    /// <param name="title">Manga title for header.</param>
    /// <returns>Selected chapter number or null if cancelled.</returns>
    public static int? BrowseChapters(IReadOnlyList<EpisodeItem> chapters, string title)
    {
        if (chapters.Count == 0) return null;

        var result = InteractiveSelect.Show(
            chapters,
            ch => $"Chapter {ch.Number}" + (string.IsNullOrWhiteSpace(ch.Title) ? "" : $" - {ch.Title}"),
            new SelectorOptions<EpisodeItem>
            {
                Prompt = $"{Icons.Book} {title}",
                MaxVisibleItems = 15,
                ShowSearch = true,
                ShowPreview = false,
                StatusFunc = ch =>
                {
                    if (ch.IsDownloaded) return ItemStatus.Downloaded;
                    if (ch.IsWatched) return ItemStatus.Watched;
                    return ItemStatus.None;
                }
            });

        return result.Cancelled ? null : result.Selected?.Number;
    }

    /// <summary>
    /// Show a history browser with quick replay.
    /// </summary>
    /// <param name="history">List of history entries.</param>
    /// <returns>Selected history entry or null if cancelled.</returns>
    public static HistoryItem? BrowseHistory(IReadOnlyList<HistoryItem> history)
    {
        if (history.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine("No watch history found.");
            System.Console.ResetColor();
            return null;
        }

        var result = InteractiveSelect.Show(
            history,
            h =>
            {
                var progress = h.TotalEpisodes.HasValue
                    ? $"Ep {h.LastEpisode}/{h.TotalEpisodes}"
                    : $"Ep {h.LastEpisode}";
                return $"{h.Title} ({progress})";
            },
            new SelectorOptions<HistoryItem>
            {
                Prompt = "📜 Watch History",
                MaxVisibleItems = 12,
                ShowSearch = true,
                ShowPreview = true,
                PreviewFunc = h =>
                {
                    var ago = GetTimeAgo(h.WatchedAt);
                    var details = $"Last watched: {ago}";
                    if (!string.IsNullOrWhiteSpace(h.Provider))
                        details += $" • Provider: {h.Provider}";
                    if (!string.IsNullOrWhiteSpace(h.Quality))
                        details += $" • Quality: {h.Quality}";
                    return details;
                },
                StatusFunc = h =>
                {
                    if (h.TotalEpisodes.HasValue && h.LastEpisode >= h.TotalEpisodes)
                        return ItemStatus.Watched;
                    return ItemStatus.InProgress;
                }
            });

        return result.Cancelled ? null : result.Selected;
    }

    private static string GetTimeAgo(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;

        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
