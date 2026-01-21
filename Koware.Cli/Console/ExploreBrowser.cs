// Author: Ilgaz Mehmetoğlu
// Explore browser with multi-line header and preview pane.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Koware.Cli.Config;
using Koware.Domain.Models;

namespace Koware.Cli.Console;

/// <summary>
/// Entry shown in the explore browser.
/// </summary>
public sealed class ExploreEntry
{
    public string Title { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public string ProviderSlug { get; init; } = "";
    public string? Synopsis { get; init; }
    public string? DetailUrl { get; init; }
    public int? Count { get; init; }
    public ItemStatus Status { get; init; }
    public Anime? Anime { get; init; }
    public Manga? Manga { get; init; }
}

/// <summary>
/// Options for explore browser rendering.
/// </summary>
public sealed class ExploreBrowserOptions
{
    public string Prompt { get; init; } = "Explore";
    public string ProviderLine { get; init; } = "";
    public string ViewLine { get; init; } = "";
    public int MaxVisibleItems { get; init; } = 10;
    public bool ShowPreview { get; init; } = true;
    public string EmptyMessage { get; init; } = "No entries found";
}

/// <summary>
/// Explore browser with fuzzy search and preview.
/// </summary>
public sealed class ExploreBrowser
{
    private readonly IReadOnlyList<ExploreEntry> _items;
    private readonly Func<ExploreEntry, string> _displayFunc;
    private readonly Func<ExploreEntry, string?> _previewFunc;
    private readonly ExploreBrowserOptions _options;
    private readonly int _maxVisibleCap;

    private List<(ExploreEntry Item, int OriginalIndex, int Score)> _filtered = new();
    private string _searchText = "";
    private int _selectedIndex;
    private int _scrollOffset;
    private int _maxVisibleItems;

    public ExploreBrowser(
        IReadOnlyList<ExploreEntry> items,
        Func<ExploreEntry, string> displayFunc,
        Func<ExploreEntry, string?> previewFunc,
        ExploreBrowserOptions options)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _displayFunc = displayFunc ?? throw new ArgumentNullException(nameof(displayFunc));
        _previewFunc = previewFunc ?? throw new ArgumentNullException(nameof(previewFunc));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _maxVisibleCap = Math.Max(3, _options.MaxVisibleItems);
        _maxVisibleItems = Math.Min(_maxVisibleCap, Math.Max(3, GetTerminalHeight() - 10));
    }

    private static int GetTerminalHeight()
    {
        try { return System.Console.WindowHeight; }
        catch { return 24; }
    }

    public SelectionResult<ExploreEntry> Run()
    {
        if (_items.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine(_options.EmptyMessage);
            System.Console.ResetColor();
            return SelectionResult<ExploreEntry>.Cancel();
        }

        using var buffer = new TerminalBuffer(useAlternateScreen: true);
        buffer.Initialize();

        UpdateFilter();
        var totalLines = CalculateTotalLines();
        var startLine = buffer.ReserveSpace(totalLines);
        var inputHandler = new InputHandler(searchEnabled: true);

        try
        {
            RenderFrame(buffer, startLine);

            while (true)
            {
                if (buffer.CheckResize())
                {
                    _maxVisibleItems = Math.Min(_maxVisibleCap, Math.Max(3, buffer.Height - 10));
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

                var input = inputHandler.ReadKey(!string.IsNullOrEmpty(_searchText));

                switch (input.Action)
                {
                    case InputAction.MoveUp:
                        MoveUp();
                        break;

                    case InputAction.MoveDown:
                        MoveDown();
                        break;

                    case InputAction.PageUp:
                        MoveUp(_maxVisibleItems);
                        break;

                    case InputAction.PageDown:
                        MoveDown(_maxVisibleItems);
                        break;

                    case InputAction.JumpToStart:
                        _selectedIndex = 0;
                        _scrollOffset = 0;
                        break;

                    case InputAction.JumpToEnd:
                        _selectedIndex = Math.Max(0, _filtered.Count - 1);
                        _scrollOffset = Math.Max(0, _filtered.Count - _maxVisibleItems);
                        break;

                    case InputAction.Select:
                        if (_filtered.Count > 0 && _selectedIndex >= 0 && _selectedIndex < _filtered.Count)
                        {
                            var selected = _filtered[_selectedIndex];
                            return SelectionResult<ExploreEntry>.Success(selected.Item, selected.OriginalIndex);
                        }
                        return SelectionResult<ExploreEntry>.Cancel();

                    case InputAction.Cancel:
                        return SelectionResult<ExploreEntry>.Cancel();

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
                            return SelectionResult<ExploreEntry>.Success(jumpItem.Item, jumpItem.OriginalIndex);
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
        var lines = 3; // header + provider + view
        lines++; // search
        lines++; // separator
        lines += _maxVisibleItems;
        if (_options.ShowPreview) lines += 3; // preview separator + 2 lines
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
        if (_options.ShowPreview)
        {
            RenderPreview(buffer, ref lines);
        }
        RenderFooter(buffer, ref lines);

        buffer.EndFrame(startLine, lines);
    }

    private void RenderHeader(TerminalBuffer buffer, ref int lines)
    {
        buffer.SetColor(Theme.Primary);
        buffer.Write($"{Icons.Prompt} {_options.Prompt}");
        buffer.ResetColor();

        buffer.SetColor(Theme.Text);
        buffer.Write($" [{_filtered.Count}/{_items.Count}]");
        buffer.ResetColor();
        buffer.WriteLine();
        lines++;

        buffer.SetColor(Theme.Secondary);
        buffer.Write("  Providers: ");
        buffer.SetColor(Theme.Text);
        buffer.WriteLine(TrimToWidth(_options.ProviderLine, buffer.Width - 13));
        buffer.ResetColor();
        lines++;

        buffer.SetColor(Theme.Secondary);
        buffer.Write("  View: ");
        buffer.SetColor(Theme.Text);
        buffer.WriteLine(TrimToWidth(_options.ViewLine, buffer.Width - 8));
        buffer.ResetColor();
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
        var renderItems = _filtered
            .Skip(_scrollOffset)
            .Take(_maxVisibleItems)
            .ToList();

        for (var i = 0; i < _maxVisibleItems; i++)
        {
            if (i < renderItems.Count)
            {
                var itemIndex = _scrollOffset + i;
                var entry = renderItems[i];
                var isSelected = itemIndex == _selectedIndex;
                RenderItem(buffer, entry.Item, isSelected, i, _searchText);
            }
            buffer.WriteLine();
            lines++;
        }
    }

    private void RenderItem(TerminalBuffer buffer, ExploreEntry entry, bool isSelected, int displayIndex, string searchText)
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

        var displayNum = displayIndex + 1;
        if (string.IsNullOrEmpty(searchText))
        {
            buffer.SetColor(Theme.Secondary);
            buffer.Write($"[{displayNum}] ");
        }
        else
        {
            buffer.Write("    ");
        }

        var statusIcon = GetStatusIcon(entry.Status);
        if (!string.IsNullOrEmpty(statusIcon))
        {
            buffer.SetColor(GetStatusColor(entry.Status));
            buffer.Write(statusIcon + " ");
        }

        var text = _displayFunc(entry);
        var maxWidth = buffer.Width - 16;
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

        var preview = _previewFunc.Invoke(_filtered[_selectedIndex].Item);
        if (string.IsNullOrWhiteSpace(preview))
        {
            buffer.WriteLine();
            buffer.WriteLine();
            lines += 2;
            return;
        }

        var maxWidth = buffer.Width - 6;
        var previewLines = WordWrap(preview, maxWidth).Take(2).ToList();

        buffer.SetColor(Theme.Muted);
        buffer.Write($"  {Icons.Preview} ");
        buffer.SetColor(Theme.Text);
        buffer.Write(previewLines[0]);
        buffer.ResetColor();
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
        buffer.Write("  Up/Down move | Enter select | Esc back | Type to filter");
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

    private static string GetStatusIcon(ItemStatus status) => status switch
    {
        ItemStatus.Watched => Icons.Watched,
        ItemStatus.Downloaded => Icons.Downloaded,
        ItemStatus.InProgress => Icons.InProgress,
        ItemStatus.New => Icons.New,
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

    private static string TrimToWidth(string text, int maxWidth)
    {
        if (maxWidth <= 0 || string.IsNullOrEmpty(text))
        {
            return "";
        }

        if (text.Length <= maxWidth)
        {
            return text;
        }

        return maxWidth > 3 ? text[..(maxWidth - 3)] + "..." : text[..maxWidth];
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
                currentLine.Append(' ');
            currentLine.Append(word);
        }

        if (currentLine.Length > 0)
            yield return currentLine.ToString();
    }
}
