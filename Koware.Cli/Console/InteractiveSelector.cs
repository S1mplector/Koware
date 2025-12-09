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
/// </summary>
/// <typeparam name="T">Type of items to select from.</typeparam>
public sealed class InteractiveSelector<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Func<T, string> _displayFunc;
    private readonly Func<T, string>? _previewFunc;
    private readonly Func<T, ItemStatus>? _statusFunc;
    private readonly string _prompt;
    private readonly int _maxVisible;
    private readonly bool _showSearch;
    private readonly bool _showPreview;
    private readonly ConsoleColor _highlightColor;
    private readonly ConsoleColor _selectedColor;
    private readonly string _emptyMessage;

    private List<(T Item, int OriginalIndex, int Score)> _filtered = new();
    private string _searchText = "";
    private int _selectedIndex;
    private int _scrollOffset;
    private int _lastRenderedLines;  // Track how many lines were rendered

    /// <summary>
    /// Create a new interactive selector.
    /// </summary>
    /// <param name="items">Items to select from.</param>
    /// <param name="displayFunc">Function to get display text for an item.</param>
    /// <param name="options">Optional configuration.</param>
    public InteractiveSelector(
        IReadOnlyList<T> items,
        Func<T, string> displayFunc,
        SelectorOptions<T>? options = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _displayFunc = displayFunc ?? throw new ArgumentNullException(nameof(displayFunc));
        _previewFunc = options?.PreviewFunc ?? options?.SecondaryDisplayFunc;
        _statusFunc = options?.StatusFunc;
        _prompt = options?.Prompt ?? "Select";
        _maxVisible = Math.Min(options?.MaxVisibleItems ?? 10, Math.Max(3, System.Console.WindowHeight - 8));
        _showSearch = options?.ShowSearch ?? true;
        _showPreview = options?.ShowPreview ?? (_previewFunc != null);
        _highlightColor = options?.GetHighlightColor() ?? Theme.Highlight;
        _selectedColor = options?.GetSelectionColor() ?? Theme.Selection;
        _emptyMessage = options?.EmptyMessage ?? "No items found";
    }

    /// <summary>
    /// Run the interactive selector and return the selection.
    /// </summary>
    /// <returns>Selection result with the chosen item or cancellation.</returns>
    public SelectionResult<T> Run()
    {
        if (_items.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine(_emptyMessage);
            System.Console.ResetColor();
            return SelectionResult<T>.Cancel();
        }

        // Initialize filtered list
        UpdateFilter();

        // Save cursor position and hide cursor
        var originalCursorVisible = true;
        try
        {
            originalCursorVisible = System.Console.CursorVisible;
            System.Console.CursorVisible = false;
        }
        catch { /* Ignore on non-interactive terminals */ }

        // Calculate total lines needed and reserve space to prevent scrolling glitches
        var totalLinesNeeded = _maxVisible + 4 + (_showPreview ? 4 : 0);
        var currentLine = System.Console.CursorTop;
        var availableLines = System.Console.WindowHeight - currentLine - 1;
        
        // If not enough space, scroll the terminal by printing blank lines
        if (availableLines < totalLinesNeeded)
        {
            var linesToScroll = totalLinesNeeded - availableLines;
            for (var i = 0; i < linesToScroll; i++)
            {
                System.Console.WriteLine();
            }
        }
        
        // Now set startLine - if we scrolled, we need to account for that
        var startLine = Math.Max(0, System.Console.CursorTop - (availableLines < totalLinesNeeded ? 0 : 0));
        if (availableLines < totalLinesNeeded)
        {
            startLine = System.Console.WindowHeight - totalLinesNeeded - 1;
            startLine = Math.Max(0, startLine);
        }
        
        var result = SelectionResult<T>.Cancel();

        try
        {
            Render(startLine);

            while (true)
            {
                var key = System.Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control:
                        MoveUp();
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J when key.Modifiers == ConsoleModifiers.Control:
                        MoveDown();
                        break;

                    case ConsoleKey.PageUp:
                        MoveUp(_maxVisible);
                        break;

                    case ConsoleKey.PageDown:
                        MoveDown(_maxVisible);
                        break;

                    case ConsoleKey.Home:
                        _selectedIndex = 0;
                        _scrollOffset = 0;
                        break;

                    case ConsoleKey.End:
                        _selectedIndex = Math.Max(0, _filtered.Count - 1);
                        _scrollOffset = Math.Max(0, _filtered.Count - _maxVisible);
                        break;

                    case ConsoleKey.Enter:
                        if (_filtered.Count > 0 && _selectedIndex >= 0 && _selectedIndex < _filtered.Count)
                        {
                            var selected = _filtered[_selectedIndex];
                            result = SelectionResult<T>.Success(selected.Item, selected.OriginalIndex);
                        }
                        goto done;

                    case ConsoleKey.Escape:
                    case ConsoleKey.C when key.Modifiers == ConsoleModifiers.Control:
                        result = SelectionResult<T>.Cancel();
                        goto done;

                    case ConsoleKey.Backspace:
                        if (_searchText.Length > 0)
                        {
                            _searchText = _searchText[..^1];
                            UpdateFilter();
                        }
                        break;

                    case ConsoleKey.Tab:
                        // Tab cycles through matches
                        if (key.Modifiers == ConsoleModifiers.Shift)
                            MoveUp();
                        else
                            MoveDown();
                        break;

                    // Quick number jump (1-9)
                    case ConsoleKey.D1:
                    case ConsoleKey.D2:
                    case ConsoleKey.D3:
                    case ConsoleKey.D4:
                    case ConsoleKey.D5:
                    case ConsoleKey.D6:
                    case ConsoleKey.D7:
                    case ConsoleKey.D8:
                    case ConsoleKey.D9:
                        if (key.Modifiers == 0 && string.IsNullOrEmpty(_searchText))
                        {
                            var jumpIndex = key.Key - ConsoleKey.D1;
                            if (jumpIndex < _filtered.Count)
                            {
                                var jumpItem = _filtered[jumpIndex];
                                result = SelectionResult<T>.Success(jumpItem.Item, jumpItem.OriginalIndex);
                                goto done;
                            }
                        }
                        else if (_showSearch && !char.IsControl(key.KeyChar))
                        {
                            _searchText += key.KeyChar;
                            UpdateFilter();
                        }
                        break;

                    default:
                        // Add character to search if printable
                        if (_showSearch && !char.IsControl(key.KeyChar))
                        {
                            _searchText += key.KeyChar;
                            UpdateFilter();
                        }
                        break;
                }

                Render(startLine);
            }

            done:;
        }
        finally
        {
            // Clear the selector UI (items + header + search + separator + footer + preview)
            var totalLines = _maxVisible + 4 + (_showPreview ? 4 : 0);
            ClearLines(startLine, totalLines);
            try { System.Console.CursorVisible = originalCursorVisible; } catch { }
        }

        return result;
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
        if (_selectedIndex >= _scrollOffset + _maxVisible)
        {
            _scrollOffset = _selectedIndex - _maxVisible + 1;
        }
    }

    private void UpdateFilter()
    {
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            _filtered = _items
                .Select((item, index) => (item, index, Score: 0))
                .ToList();
        }
        else
        {
            _filtered = _items
                .Select((item, index) => (item, index, Score: FuzzyScore(_displayFunc(item), _searchText)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();
        }

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

    private void Render(int startLine)
    {
        // Use ANSI escape to move cursor and clear properly
        // First, clear any previously rendered content
        if (_lastRenderedLines > 0)
        {
            // Move to start and clear each line explicitly
            for (int i = 0; i < _lastRenderedLines; i++)
            {
                System.Console.SetCursorPosition(0, startLine + i);
                System.Console.Write("\x1b[2K"); // ANSI: clear entire line
            }
        }
        
        System.Console.SetCursorPosition(0, startLine);
        var linesRendered = 0;

        // Header with prompt and count
        System.Console.ForegroundColor = Theme.Primary;
        System.Console.Write($"❯ {_prompt}");
        System.Console.ResetColor();

        System.Console.ForegroundColor = Theme.Text;
        System.Console.Write($" [{_filtered.Count}/{_items.Count}]");
        System.Console.ResetColor();

        // Scroll indicator
        if (_filtered.Count > _maxVisible)
        {
            System.Console.ForegroundColor = Theme.Muted;
            var scrollPct = _filtered.Count > 1 ? (_selectedIndex * 100) / (_filtered.Count - 1) : 0;
            System.Console.Write($" ↕{scrollPct}%");
            System.Console.ResetColor();
        }

        System.Console.Write("\x1b[K"); // ANSI: clear to end of line
        System.Console.WriteLine();
        linesRendered++;

        // Search box
        if (_showSearch)
        {
            System.Console.ForegroundColor = Theme.Muted;
            System.Console.Write("  🔍 ");
            System.Console.ForegroundColor = Theme.Text;
            System.Console.Write(_searchText);
            System.Console.ForegroundColor = Theme.Primary;
            System.Console.Write("▌");
            System.Console.ResetColor();
            System.Console.Write("\x1b[K");
            System.Console.WriteLine();
            linesRendered++;
        }

        // Separator
        System.Console.ForegroundColor = Theme.Muted;
        System.Console.Write(new string('─', Math.Min(60, System.Console.WindowWidth - 2)));
        System.Console.Write("\x1b[K");
        System.Console.WriteLine();
        System.Console.ResetColor();
        linesRendered++;

        // Items
        for (var i = 0; i < _maxVisible; i++)
        {
            var itemIndex = _scrollOffset + i;
            if (itemIndex < _filtered.Count)
            {
                var (item, originalIndex, _) = _filtered[itemIndex];
                var isSelected = itemIndex == _selectedIndex;
                var displayText = _displayFunc(item);
                var status = _statusFunc?.Invoke(item) ?? ItemStatus.None;

                // Selection indicator
                if (isSelected)
                {
                    System.Console.ForegroundColor = _selectedColor;
                    System.Console.Write(" ▶ ");
                }
                else
                {
                    System.Console.Write("   ");
                }

                // Quick jump number (1-9)
                var displayNum = i + 1;
                if (displayNum <= 9 && string.IsNullOrEmpty(_searchText))
                {
                    System.Console.ForegroundColor = Theme.Secondary;
                    System.Console.Write($"[{displayNum}] ");
                }
                else
                {
                    System.Console.ForegroundColor = Theme.Muted;
                    System.Console.Write($"{originalIndex + 1,3}. ");
                }

                // Status indicator
                var statusIcon = GetStatusIcon(status);
                if (!string.IsNullOrEmpty(statusIcon))
                {
                    System.Console.ForegroundColor = GetStatusColor(status);
                    System.Console.Write(statusIcon);
                    System.Console.ResetColor();
                    System.Console.Write(" ");
                }

                // Main text with search highlighting
                var maxWidth = System.Console.WindowWidth - 16;
                if (displayText.Length > maxWidth)
                {
                    displayText = displayText[..(maxWidth - 3)] + "...";
                }

                if (isSelected)
                {
                    System.Console.ForegroundColor = _selectedColor;
                }
                else
                {
                    System.Console.ForegroundColor = Theme.Text;
                }

                WriteHighlighted(displayText, _searchText, isSelected, _highlightColor);

                System.Console.ResetColor();
            }

            System.Console.Write("\x1b[K");
            System.Console.WriteLine();
            linesRendered++;
        }

        // Preview pane
        if (_showPreview && _previewFunc != null && _filtered.Count > 0 && _selectedIndex < _filtered.Count)
        {
            var selectedItem = _filtered[_selectedIndex].Item;
            var preview = _previewFunc(selectedItem);

            System.Console.ForegroundColor = Theme.Muted;
            System.Console.Write(new string('─', Math.Min(60, System.Console.WindowWidth - 2)));
            System.Console.Write("\x1b[K");
            System.Console.WriteLine();
            System.Console.ResetColor();
            linesRendered++;

            if (!string.IsNullOrWhiteSpace(preview))
            {
                System.Console.ForegroundColor = Theme.Muted;
                System.Console.Write("  📖 ");
                System.Console.ForegroundColor = Theme.Text;

                // Word-wrap preview text
                var maxPreviewWidth = System.Console.WindowWidth - 6;
                var lines = WordWrap(preview, maxPreviewWidth).Take(2).ToList();
                System.Console.Write(lines[0]);
                System.Console.Write("\x1b[K");
                System.Console.WriteLine();
                linesRendered++;

                if (lines.Count > 1)
                {
                    System.Console.Write("     ");
                    System.Console.Write(lines[1]);
                    if (preview.Length > maxPreviewWidth * 2)
                    {
                        System.Console.ForegroundColor = Theme.Muted;
                        System.Console.Write("...");
                    }
                }
                System.Console.Write("\x1b[K");
                System.Console.WriteLine();
                linesRendered++;
            }
            else
            {
                System.Console.Write("\x1b[K");
                System.Console.WriteLine();
                linesRendered++;
                System.Console.Write("\x1b[K");
                System.Console.WriteLine();
                linesRendered++;
            }

            System.Console.ResetColor();
        }

        // Footer with controls
        System.Console.ForegroundColor = Theme.Muted;
        System.Console.Write("  ↑↓ move • ");
        System.Console.ForegroundColor = Theme.Secondary;
        System.Console.Write("1-9");
        System.Console.ForegroundColor = Theme.Muted;
        System.Console.Write(" jump • Enter select • Esc cancel");
        if (_showSearch)
        {
            System.Console.Write(" • Type to search");
        }
        System.Console.ResetColor();
        System.Console.Write("\x1b[K");
        linesRendered++;
        
        _lastRenderedLines = linesRendered;
    }

    private static string GetStatusIcon(ItemStatus status) => status switch
    {
        ItemStatus.Watched => "✓",
        ItemStatus.Downloaded => "📥",
        ItemStatus.InProgress => "▶",
        ItemStatus.New => "✨",
        _ => ""
    };

    private static ConsoleColor GetStatusColor(ItemStatus status) => status switch
    {
        ItemStatus.Watched => Theme.Success,
        ItemStatus.Downloaded => Theme.Accent,
        ItemStatus.InProgress => Theme.Warning,
        ItemStatus.New => Theme.Primary,
        _ => Theme.Text
    };

    private static IEnumerable<string> WordWrap(string text, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0)
        {
            yield return "";
            yield break;
        }

        text = text.Replace("\n", " ").Replace("\r", "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxWidth)
            {
                if (currentLine.Length > 0)
                {
                    yield return currentLine.ToString();
                    currentLine.Clear();
                }
            }

            if (currentLine.Length > 0)
            {
                currentLine.Append(' ');
            }
            currentLine.Append(word);
        }

        if (currentLine.Length > 0)
        {
            yield return currentLine.ToString();
        }
    }

    private void WriteHighlighted(string text, string search, bool isSelected, ConsoleColor highlightColor)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            System.Console.Write(text);
            return;
        }

        var searchLower = search.ToLowerInvariant();
        var currentColor = System.Console.ForegroundColor;
        var searchIndex = 0;

        foreach (var ch in text)
        {
            var chLower = char.ToLowerInvariant(ch);
            if (searchIndex < searchLower.Length && chLower == searchLower[searchIndex])
            {
                // Highlight matched characters with underline effect (using brackets)
                System.Console.ForegroundColor = highlightColor;
                if (!isSelected)
                {
                    // Make matched chars stand out more
                    System.Console.ForegroundColor = ConsoleColor.Green;
                }
                System.Console.Write(ch);
                System.Console.ForegroundColor = currentColor;
                searchIndex++;
            }
            else
            {
                System.Console.Write(ch);
            }
        }
    }

    /// <summary>
    /// Calculate fuzzy match score. Higher is better, 0 means no match.
    /// </summary>
    private static int FuzzyScore(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 1;
        if (string.IsNullOrEmpty(text)) return 0;

        var textLower = text.ToLowerInvariant();
        var patternLower = pattern.ToLowerInvariant();

        // Exact match gets highest score
        if (textLower.Contains(patternLower))
        {
            // Bonus for match at start
            if (textLower.StartsWith(patternLower))
                return 1000 + patternLower.Length;
            return 500 + patternLower.Length;
        }

        // Fuzzy match: all pattern characters must appear in order
        var score = 0;
        var patternIndex = 0;
        var consecutiveBonus = 0;
        var lastMatchIndex = -1;

        for (var i = 0; i < textLower.Length && patternIndex < patternLower.Length; i++)
        {
            if (textLower[i] == patternLower[patternIndex])
            {
                score += 10;

                // Bonus for consecutive matches
                if (lastMatchIndex == i - 1)
                {
                    consecutiveBonus += 5;
                }

                // Bonus for matching at word boundaries
                if (i == 0 || !char.IsLetterOrDigit(textLower[i - 1]))
                {
                    score += 15;
                }

                lastMatchIndex = i;
                patternIndex++;
            }
        }

        // All pattern characters must match
        if (patternIndex < patternLower.Length)
            return 0;

        return score + consecutiveBonus;
    }

    private static void ClearToEndOfLine()
    {
        try
        {
            var remaining = System.Console.WindowWidth - System.Console.CursorLeft - 1;
            if (remaining > 0)
            {
                System.Console.Write(new string(' ', remaining));
            }
        }
        catch { }
    }

    private static void ClearLines(int startLine, int count)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                System.Console.SetCursorPosition(0, startLine + i);
                System.Console.Write(new string(' ', System.Console.WindowWidth - 1));
            }
            System.Console.SetCursorPosition(0, startLine);
        }
        catch { }
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
        System.Console.Write($"❯ {message} ");
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
        System.Console.Write($"❯ {prompt} ");
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
                Prompt = $"📖 {title}",
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
