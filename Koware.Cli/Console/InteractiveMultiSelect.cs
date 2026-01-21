// Author: Ilgaz Mehmetoğlu
// Interactive multi-select TUI with fuzzy search.
using System;
using System.Collections.Generic;
using System.Linq;
using Koware.Cli.Config;

namespace Koware.Cli.Console;

/// <summary>
/// Result of a multi-select interaction.
/// </summary>
public sealed class MultiSelectResult<T>
{
    public bool Cancelled { get; init; }
    public IReadOnlyList<T> Selected { get; init; } = Array.Empty<T>();
    public IReadOnlyList<int> SelectedIndices { get; init; } = Array.Empty<int>();

    public static MultiSelectResult<T> Cancel() => new() { Cancelled = true };

    public static MultiSelectResult<T> Success(IReadOnlyList<T> selected, IReadOnlyList<int> indices) => new()
    {
        Selected = selected,
        SelectedIndices = indices
    };
}

/// <summary>
/// Configuration options for multi-select UI.
/// </summary>
public sealed class MultiSelectOptions<T>
{
    public string? Prompt { get; init; }
    public int MaxVisibleItems { get; init; } = 10;
    public Func<T, string>? PreviewFunc { get; init; }
    public Func<T, bool>? IsSelected { get; init; }
    public string? EmptyMessage { get; init; }
}

/// <summary>
/// Interactive multi-select list with fuzzy filtering.
/// </summary>
public sealed class InteractiveMultiSelect<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Func<T, string> _displayFunc;
    private readonly Func<T, string>? _previewFunc;
    private readonly string _prompt;
    private readonly string _emptyMessage;
    private readonly HashSet<int> _selectedIndices = new();

    private List<(T Item, int OriginalIndex, int Score)> _filtered = new();
    private string _searchText = "";
    private int _selectedIndex;
    private int _scrollOffset;
    private readonly int _maxVisibleCap;
    private int _maxVisibleItems;

    public InteractiveMultiSelect(
        IReadOnlyList<T> items,
        Func<T, string> displayFunc,
        MultiSelectOptions<T>? options = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _displayFunc = displayFunc ?? throw new ArgumentNullException(nameof(displayFunc));
        _previewFunc = options?.PreviewFunc;
        _prompt = options?.Prompt ?? "Select items";
        _emptyMessage = options?.EmptyMessage ?? "No items found";
        _maxVisibleCap = options?.MaxVisibleItems ?? 10;
        _maxVisibleItems = Math.Min(_maxVisibleCap, Math.Max(3, GetTerminalHeight() - 8));

        if (options?.IsSelected is not null)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (options.IsSelected(_items[i]))
                {
                    _selectedIndices.Add(i);
                }
            }
        }
    }

    private static int GetTerminalHeight()
    {
        try { return System.Console.WindowHeight; }
        catch { return 24; }
    }

    public MultiSelectResult<T> Run()
    {
        if (_items.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine(_emptyMessage);
            System.Console.ResetColor();
            return MultiSelectResult<T>.Cancel();
        }

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        buffer.Initialize();

        UpdateFilter();
        var totalLines = CalculateTotalLines();
        var startLine = buffer.ReserveSpace(totalLines);

        try
        {
            RenderFrame(buffer, startLine);

            while (true)
            {
                if (buffer.CheckResize())
                {
                    _maxVisibleItems = Math.Min(_maxVisibleCap, Math.Max(3, buffer.Height - 8));
                    totalLines = CalculateTotalLines();
                    EnsureVisible();
                    if (buffer.IsAlternateScreen)
                    {
                        buffer.ClearFullScreen();
                        startLine = 0;
                    }
                    else
                    {
                        startLine = buffer.ReserveSpace(totalLines);
                    }
                    RenderFrame(buffer, startLine);
                }

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
                        MoveUp(_maxVisibleItems);
                        break;

                    case ConsoleKey.PageDown:
                        MoveDown(_maxVisibleItems);
                        break;

                    case ConsoleKey.Home:
                        _selectedIndex = 0;
                        _scrollOffset = 0;
                        break;

                    case ConsoleKey.End:
                        _selectedIndex = Math.Max(0, _filtered.Count - 1);
                        _scrollOffset = Math.Max(0, _filtered.Count - _maxVisibleItems);
                        break;

                    case ConsoleKey.Enter:
                        return BuildResult();

                    case ConsoleKey.Escape:
                    case ConsoleKey.C when key.Modifiers == ConsoleModifiers.Control:
                        return MultiSelectResult<T>.Cancel();

                    case ConsoleKey.Spacebar:
                        ToggleCurrent();
                        break;

                    case ConsoleKey.Backspace:
                        if (_searchText.Length > 0)
                        {
                            _searchText = _searchText[..^1];
                            UpdateFilter();
                        }
                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            _searchText += key.KeyChar;
                            UpdateFilter();
                        }
                        break;
                }

                RenderFrame(buffer, startLine);
            }
        }
        finally
        {
            buffer.ClearArea(startLine, totalLines);
            buffer.Restore();
        }
    }

    private MultiSelectResult<T> BuildResult()
    {
        var selected = _selectedIndices
            .Where(i => i >= 0 && i < _items.Count)
            .OrderBy(i => i)
            .ToList();

        var items = selected.Select(i => _items[i]).ToList();
        return MultiSelectResult<T>.Success(items, selected);
    }

    private void ToggleCurrent()
    {
        if (_filtered.Count == 0) return;
        var current = _filtered[_selectedIndex];
        if (_selectedIndices.Contains(current.OriginalIndex))
        {
            _selectedIndices.Remove(current.OriginalIndex);
        }
        else
        {
            _selectedIndices.Add(current.OriginalIndex);
        }
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
        if (_selectedIndex >= _scrollOffset + _maxVisibleItems)
        {
            _scrollOffset = _selectedIndex - _maxVisibleItems + 1;
        }
    }

    private void UpdateFilter()
    {
        _filtered = FuzzyMatcher.Filter(_items, _displayFunc, _searchText).ToList();

        if (_selectedIndex >= _filtered.Count)
        {
            _selectedIndex = Math.Max(0, _filtered.Count - 1);
        }
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        if (_scrollOffset > _selectedIndex)
        {
            _scrollOffset = _selectedIndex;
        }

        if (_selectedIndex >= _scrollOffset + _maxVisibleItems)
        {
            _scrollOffset = Math.Max(0, _selectedIndex - _maxVisibleItems + 1);
        }
    }

    private int CalculateTotalLines()
    {
        var lines = 1; // header
        lines++; // search
        lines++; // separator
        lines += _maxVisibleItems;
        if (_previewFunc != null) lines += 3; // preview separator + 2 lines
        lines++; // footer
        return lines;
    }

    private void RenderFrame(TerminalBuffer buffer, int startLine)
    {
        buffer.BeginFrame();
        var lines = 0;

        RenderHeader(buffer, ref lines);
        RenderSearch(buffer, ref lines);
        RenderSeparator(buffer, ref lines);
        RenderItems(buffer, ref lines);
        if (_previewFunc != null)
        {
            RenderPreview(buffer, ref lines);
        }
        RenderFooter(buffer, ref lines);

        buffer.EndFrame(startLine, lines);
    }

    private void RenderHeader(TerminalBuffer buffer, ref int lines)
    {
        buffer.SetColor(Theme.Primary);
        buffer.Write($"{Icons.Prompt} {_prompt}");
        buffer.ResetColor();

        buffer.SetColor(Theme.Text);
        buffer.Write($" [{_selectedIndices.Count}/{_items.Count}]");
        buffer.ResetColor();

        buffer.WriteLine();
        lines++;
    }

    private void RenderSearch(TerminalBuffer buffer, ref int lines)
    {
        buffer.SetColor(Theme.Muted);
        buffer.Write($"  {Icons.Search} ");
        buffer.SetColor(Theme.Text);
        buffer.Write(_searchText);
        buffer.SetColor(Theme.Primary);
        buffer.Write("|");
        buffer.ResetColor();
        buffer.WriteLine();
        lines++;
    }

    private void RenderSeparator(TerminalBuffer buffer, ref int lines)
    {
        var width = Math.Min(60, buffer.Width - 2);
        buffer.WriteLine(new string('─', width), Theme.Muted);
        lines++;
    }

    private void RenderItems(TerminalBuffer buffer, ref int lines)
    {
        var visible = _filtered.Skip(_scrollOffset).Take(_maxVisibleItems).ToList();

        for (var i = 0; i < _maxVisibleItems; i++)
        {
            if (i < visible.Count)
            {
                var item = visible[i];
                var isSelected = (_scrollOffset + i) == _selectedIndex;
                var isChecked = _selectedIndices.Contains(item.OriginalIndex);
                RenderItem(buffer, item.Item, isSelected, isChecked, i, _searchText);
            }
            buffer.WriteLine();
            lines++;
        }
    }

    private void RenderItem(TerminalBuffer buffer, T item, bool isSelected, bool isChecked, int displayIndex, string searchText)
    {
        if (isSelected)
        {
            buffer.SetColor(Theme.Selection);
            buffer.Write($" {Icons.Selection} ");
        }
        else
        {
            buffer.Write("   ");
        }

        buffer.SetColor(isChecked ? Theme.Success : Theme.Muted);
        buffer.Write(isChecked ? "[x] " : "[ ] ");

        var displayNum = displayIndex + 1;
        buffer.SetColor(Theme.Muted);
        buffer.Write($"{displayNum,2}. ");

        var text = _displayFunc(item);
        var maxWidth = buffer.Width - 12;
        if (text.Length > maxWidth && maxWidth > 3)
        {
            text = text[..(maxWidth - 3)] + "...";
        }

        buffer.SetColor(isSelected ? Theme.Selection : Theme.Text);
        WriteHighlighted(buffer, text, searchText, isSelected);
        buffer.ResetColor();
    }

    private void RenderPreview(TerminalBuffer buffer, ref int lines)
    {
        var width = Math.Min(60, buffer.Width - 2);
        buffer.WriteLine(new string('─', width), Theme.Muted);
        lines++;

        if (_filtered.Count == 0)
        {
            buffer.WriteLine();
            buffer.WriteLine();
            lines += 2;
            return;
        }

        var preview = _previewFunc?.Invoke(_filtered[_selectedIndex].Item);
        if (string.IsNullOrWhiteSpace(preview))
        {
            buffer.WriteLine();
            buffer.WriteLine();
            lines += 2;
            return;
        }

        buffer.SetColor(Theme.Muted);
        buffer.Write($"  {Icons.Preview} ");
        buffer.SetColor(Theme.Text);

        var maxWidth = buffer.Width - 6;
        var previewLines = WordWrap(preview, maxWidth).Take(2).ToList();

        buffer.Write(previewLines[0]);
        buffer.WriteLine();
        lines++;

        if (previewLines.Count > 1)
        {
            buffer.Write("     " + previewLines[1]);
            if (preview.Length > maxWidth * 2)
            {
                buffer.SetColor(Theme.Muted);
                buffer.Write("...");
            }
        }
        buffer.WriteLine();
        lines++;
        buffer.ResetColor();
    }

    private void RenderFooter(TerminalBuffer buffer, ref int lines)
    {
        buffer.SetColor(Theme.Muted);
        buffer.Write("  Up/Down move | Space toggle | Enter confirm | Esc cancel");
        buffer.ResetColor();
        buffer.ClearToEndOfLine();
        lines++;
    }

    private static void WriteHighlighted(TerminalBuffer buffer, string text, string search, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            buffer.Write(text);
            return;
        }

        var searchLower = search.ToLowerInvariant();
        var searchIndex = 0;

        foreach (var ch in text)
        {
            var chLower = char.ToLowerInvariant(ch);
            if (searchIndex < searchLower.Length && chLower == searchLower[searchIndex])
            {
                buffer.SetColor(isSelected ? Theme.Highlight : ConsoleColor.Green);
                buffer.Write(ch.ToString());
                buffer.SetColor(isSelected ? Theme.Selection : Theme.Text);
                searchIndex++;
            }
            else
            {
                buffer.Write(ch.ToString());
            }
        }
    }

    private static IEnumerable<string> WordWrap(string text, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0)
        {
            yield return "";
            yield break;
        }

        text = text.Replace("\n", " ").Replace("\r", "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new System.Text.StringBuilder();

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
                currentLine.Append(' ');
            currentLine.Append(word);
        }

        if (currentLine.Length > 0)
            yield return currentLine.ToString();
    }
}
