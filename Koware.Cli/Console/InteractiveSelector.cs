// Author: Ilgaz Mehmetoğlu
// Interactive TUI selector with arrow-key navigation and fuzzy search.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
/// Interactive fuzzy selector with arrow-key navigation, similar to fzf.
/// </summary>
/// <typeparam name="T">Type of items to select from.</typeparam>
public sealed class InteractiveSelector<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Func<T, string> _displayFunc;
    private readonly Func<T, string>? _secondaryFunc;
    private readonly string _prompt;
    private readonly int _maxVisible;
    private readonly bool _showSearch;
    private readonly ConsoleColor _highlightColor;
    private readonly ConsoleColor _selectedColor;
    private readonly string _emptyMessage;

    private List<(T Item, int OriginalIndex, int Score)> _filtered = new();
    private string _searchText = "";
    private int _selectedIndex;
    private int _scrollOffset;

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
        _secondaryFunc = options?.SecondaryDisplayFunc;
        _prompt = options?.Prompt ?? "Select";
        _maxVisible = Math.Min(options?.MaxVisibleItems ?? 10, Math.Max(3, System.Console.WindowHeight - 5));
        _showSearch = options?.ShowSearch ?? true;
        _highlightColor = options?.HighlightColor ?? ConsoleColor.Cyan;
        _selectedColor = options?.SelectedColor ?? ConsoleColor.Yellow;
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

        var startLine = System.Console.CursorTop;
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
            // Clear the selector UI
            ClearLines(startLine, _maxVisible + 3);
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
        System.Console.SetCursorPosition(0, startLine);

        // Header with prompt and search
        System.Console.ForegroundColor = _highlightColor;
        System.Console.Write($"❯ {_prompt}");
        System.Console.ResetColor();

        if (_showSearch)
        {
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write($" [{_filtered.Count}/{_items.Count}]");
            System.Console.ResetColor();
        }
        ClearToEndOfLine();
        System.Console.WriteLine();

        // Search box
        if (_showSearch)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.Write("  Search: ");
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write(_searchText);
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.Write("▌"); // Cursor indicator
            System.Console.ResetColor();
            ClearToEndOfLine();
            System.Console.WriteLine();
        }

        // Separator
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine(new string('─', Math.Min(50, System.Console.WindowWidth - 2)));
        System.Console.ResetColor();

        // Items
        var visibleCount = Math.Min(_maxVisible, _filtered.Count);
        for (var i = 0; i < _maxVisible; i++)
        {
            var itemIndex = _scrollOffset + i;
            if (itemIndex < _filtered.Count)
            {
                var (item, originalIndex, _) = _filtered[itemIndex];
                var isSelected = itemIndex == _selectedIndex;
                var displayText = _displayFunc(item);
                var secondary = _secondaryFunc?.Invoke(item);

                // Selection indicator
                if (isSelected)
                {
                    System.Console.ForegroundColor = _selectedColor;
                    System.Console.Write(" ❯ ");
                }
                else
                {
                    System.Console.ForegroundColor = ConsoleColor.DarkGray;
                    System.Console.Write("   ");
                }

                // Index number
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.Write($"{originalIndex + 1,2}. ");

                // Main text with search highlighting
                if (isSelected)
                {
                    System.Console.ForegroundColor = _selectedColor;
                }
                else
                {
                    System.Console.ForegroundColor = ConsoleColor.White;
                }

                // Truncate if needed
                var maxWidth = System.Console.WindowWidth - 10;
                if (displayText.Length > maxWidth)
                {
                    displayText = displayText[..(maxWidth - 3)] + "...";
                }

                WriteHighlighted(displayText, _searchText, isSelected ? _selectedColor : ConsoleColor.Cyan);

                // Secondary text (e.g., synopsis preview)
                if (!string.IsNullOrWhiteSpace(secondary) && isSelected)
                {
                    System.Console.ForegroundColor = ConsoleColor.DarkGray;
                    var preview = secondary.Length > 40 ? secondary[..40] + "..." : secondary;
                    System.Console.Write($" - {preview}");
                }

                System.Console.ResetColor();
            }

            ClearToEndOfLine();
            System.Console.WriteLine();
        }

        // Footer with controls
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.Write("  ↑↓ navigate • Enter select • Esc cancel");
        if (_showSearch)
        {
            System.Console.Write(" • Type to filter");
        }
        System.Console.ResetColor();
        ClearToEndOfLine();
    }

    private void WriteHighlighted(string text, string search, ConsoleColor highlightColor)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            System.Console.Write(text);
            return;
        }

        var searchLower = search.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();
        var currentColor = System.Console.ForegroundColor;
        var searchIndex = 0;

        foreach (var ch in text)
        {
            var chLower = char.ToLowerInvariant(ch);
            if (searchIndex < searchLower.Length && chLower == searchLower[searchIndex])
            {
                System.Console.ForegroundColor = highlightColor;
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

    /// <summary>Color for highlighted/matching text.</summary>
    public ConsoleColor HighlightColor { get; init; } = ConsoleColor.Cyan;

    /// <summary>Color for the selected item.</summary>
    public ConsoleColor SelectedColor { get; init; } = ConsoleColor.Yellow;

    /// <summary>Message shown when no items match.</summary>
    public string? EmptyMessage { get; init; }

    /// <summary>Optional function for secondary display text (shown for selected item).</summary>
    public Func<T, string>? SecondaryDisplayFunc { get; init; }
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
