// Author: Ilgaz Mehmetoğlu
// Rendering logic for the interactive selector, separated for SRP.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Koware.Cli.Config;

namespace Koware.Cli.Console;

/// <summary>
/// Provides cross-platform icons that work on both Windows and macOS terminals.
/// Windows terminals often lack proper emoji font support, so we use ASCII fallbacks.
/// </summary>
public static class Icons
{
    // UI elements - ASCII-only for maximum terminal compatibility
    public static string Prompt => ">";
    public static string Search => "[?]";
    public static string Book => "[#]";
    public static string Selection => ">";
    public static string Scroll => "^v";
    public static string Play => "[>]";

    // Status indicators  
    public static string Success => "[+]";
    public static string Warning => "[!]";
    public static string Error => "[x]";
    public static string Download => "[v]";
    public static string New => "[*]";
    
    // Menu/Action icons
    public static string Provider => "[P]";
    public static string Add => "[+]";
    public static string Edit => "[E]";
    public static string Back => "<-";
    public static string Delete => "[D]";
    
    // Aliases for backward compatibility
    public static string Preview => Book;
    public static string Watched => Success;
    public static string Downloaded => Download;
    public static string InProgress => Play;
}

/// <summary>
/// Configuration for how items should be rendered in the selector.
/// </summary>
public sealed class RenderConfig
{
    public string Prompt { get; init; } = "Select";
    public int MaxVisibleItems { get; init; } = 10;
    public bool ShowSearch { get; init; } = true;
    public bool ShowPreview { get; init; }
    public bool ShowFooter { get; init; } = true;
    public ConsoleColor HighlightColor { get; init; } = Theme.Highlight;
    public ConsoleColor SelectionColor { get; init; } = Theme.Selection;
    public bool DisableQuickJump { get; init; }
}

/// <summary>
/// State data needed for rendering.
/// </summary>
public sealed class RenderState
{
    public required IReadOnlyList<(string Display, int OriginalIndex, ItemStatus Status, string? Preview)> Items { get; init; }
    public int TotalCount { get; init; }
    public int FilteredCount { get; init; }  // Actual number of filtered items (not padded)
    public int SelectedIndex { get; init; }
    public int ScrollOffset { get; init; }
    public string SearchText { get; init; } = "";
}

/// <summary>
/// Handles rendering of the interactive selector UI.
/// Responsible only for drawing - no state management or input handling.
/// </summary>
public sealed class SelectorRenderer
{
    private readonly TerminalBuffer _buffer;
    private readonly RenderConfig _config;

    public SelectorRenderer(TerminalBuffer buffer, RenderConfig config)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Calculate total lines needed for rendering.
    /// </summary>
    public int CalculateTotalLines()
    {
        var lines = 1; // Header
        if (_config.ShowSearch) lines++;
        lines++; // Separator
        lines += _config.MaxVisibleItems;
        if (_config.ShowPreview) lines += 4; // Preview separator + 2 lines + padding
        if (_config.ShowFooter) lines++;
        return lines;
    }

    /// <summary>
    /// Render a frame of the selector UI.
    /// </summary>
    /// <param name="state">Current state to render.</param>
    /// <param name="startRow">Starting row in terminal.</param>
    public void Render(RenderState state, int startRow)
    {
        _buffer.BeginFrame();
        var linesRendered = 0;

        // Header with prompt and count
        RenderHeader(state, ref linesRendered);

        // Search box
        if (_config.ShowSearch)
        {
            RenderSearchBox(state.SearchText, ref linesRendered);
        }

        // Separator
        RenderSeparator(ref linesRendered);

        // Items
        RenderItems(state, ref linesRendered);

        // Preview pane
        if (_config.ShowPreview && state.Items.Count > 0)
        {
            RenderPreview(state, ref linesRendered);
        }

        // Footer
        if (_config.ShowFooter)
        {
            RenderFooter(ref linesRendered);
        }

        _buffer.EndFrame(startRow, linesRendered);
    }

    private void RenderHeader(RenderState state, ref int lines)
    {
        _buffer.SetColor(Theme.Primary);
        _buffer.Write($"{Icons.Prompt} {_config.Prompt}");
        _buffer.ResetColor();

        _buffer.SetColor(Theme.Text);
        _buffer.Write($" [{state.FilteredCount}/{state.TotalCount}]");
        _buffer.ResetColor();

        // Scroll indicator
        if (state.FilteredCount > _config.MaxVisibleItems)
        {
            _buffer.SetColor(Theme.Muted);
            var scrollPct = state.FilteredCount > 1 
                ? ((state.ScrollOffset + state.SelectedIndex) * 100) / (state.FilteredCount - 1) 
                : 0;
            _buffer.Write($" {Icons.Scroll}{scrollPct}%");
            _buffer.ResetColor();
        }

        _buffer.WriteLine();
        lines++;
    }

    private void RenderSearchBox(string searchText, ref int lines)
    {
        _buffer.SetColor(Theme.Muted);
        _buffer.Write($"  {Icons.Search} ");
        _buffer.SetColor(Theme.Text);
        _buffer.Write(searchText);
        _buffer.SetColor(Theme.Primary);
        _buffer.Write("|");
        _buffer.ResetColor();
        _buffer.WriteLine();
        lines++;
    }

    private void RenderSeparator(ref int lines)
    {
        var width = Math.Min(60, _buffer.Width - 2);
        _buffer.WriteLine(new string('─', width), Theme.Muted);
        lines++;
    }

    private void RenderItems(RenderState state, ref int lines)
    {
        for (var i = 0; i < _config.MaxVisibleItems; i++)
        {
            var itemIndex = state.ScrollOffset + i;
            
            if (itemIndex < state.Items.Count)
            {
                var item = state.Items[itemIndex];
                var isSelected = itemIndex == state.SelectedIndex;
                
                RenderItem(item, isSelected, i, state.SearchText);
            }
            
            _buffer.WriteLine();
            lines++;
        }
    }

    private void RenderItem(
        (string Display, int OriginalIndex, ItemStatus Status, string? Preview) item,
        bool isSelected,
        int displayIndex,
        string searchText)
    {
        // Empty item - just render blank line
        if (string.IsNullOrEmpty(item.Display) && item.OriginalIndex < 0)
        {
            return; // Will still get WriteLine() from RenderItems
        }

        // Selection indicator
        if (isSelected)
        {
            _buffer.SetColor(_config.SelectionColor);
            _buffer.Write($" {Icons.Selection} ");
        }
        else
        {
            _buffer.Write("   ");
        }

        // Quick jump number (1-9) - only show if not disabled
        var displayNum = displayIndex + 1;
        if (!_config.DisableQuickJump && displayNum <= 9 && string.IsNullOrEmpty(searchText))
        {
            _buffer.SetColor(Theme.Secondary);
            _buffer.Write($"[{displayNum}] ");
        }
        else
        {
            _buffer.SetColor(Theme.Muted);
            _buffer.Write($"{item.OriginalIndex + 1,3}. ");
        }

        // Status indicator
        var statusIcon = GetStatusIcon(item.Status);
        if (!string.IsNullOrEmpty(statusIcon))
        {
            _buffer.SetColor(GetStatusColor(item.Status));
            _buffer.Write(statusIcon + " ");
        }

        // Main text with search highlighting
        var displayText = item.Display;
        var maxWidth = _buffer.Width - 16;
        if (displayText.Length > maxWidth && maxWidth > 3)
        {
            displayText = displayText[..(maxWidth - 3)] + "...";
        }

        if (isSelected)
        {
            _buffer.SetColor(_config.SelectionColor);
        }
        else
        {
            _buffer.SetColor(Theme.Text);
        }

        WriteHighlighted(displayText, searchText, isSelected);
        _buffer.ResetColor();
    }

    private void RenderPreview(RenderState state, ref int lines)
    {
        if (state.SelectedIndex >= state.Items.Count)
            return;

        var preview = state.Items[state.SelectedIndex].Preview;

        // Preview separator
        var width = Math.Min(60, _buffer.Width - 2);
        _buffer.WriteLine(new string('─', width), Theme.Muted);
        lines++;

        if (!string.IsNullOrWhiteSpace(preview))
        {
            _buffer.SetColor(Theme.Muted);
        _buffer.Write($"  {Icons.Preview} ");
            _buffer.SetColor(Theme.Text);

            var maxWidth = _buffer.Width - 6;
            var previewLines = WordWrap(preview, maxWidth).Take(2).ToList();
            
            _buffer.Write(previewLines[0]);
            _buffer.WriteLine();
            lines++;

            if (previewLines.Count > 1)
            {
                _buffer.Write("     " + previewLines[1]);
                if (preview.Length > maxWidth * 2)
                {
                    _buffer.SetColor(Theme.Muted);
                    _buffer.Write("...");
                }
            }
            _buffer.WriteLine();
            lines++;
        }
        else
        {
            _buffer.WriteLine();
            lines++;
            _buffer.WriteLine();
            lines++;
        }

        _buffer.ResetColor();
    }

    private void RenderFooter(ref int lines)
    {
        _buffer.SetColor(Theme.Muted);
        _buffer.Write("  Up/Down move | ");
        _buffer.SetColor(Theme.Secondary);
        _buffer.Write("1-9");
        _buffer.SetColor(Theme.Muted);
        _buffer.Write(" jump • Enter select • Esc cancel");
        if (_config.ShowSearch)
        {
            _buffer.Write(" • Type to search");
        }
        _buffer.ResetColor();
        _buffer.ClearToEndOfLine();
        lines++;
    }

    private void WriteHighlighted(string text, string search, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            _buffer.Write(text);
            return;
        }

        var searchLower = search.ToLowerInvariant();
        var searchIndex = 0;

        foreach (var ch in text)
        {
            var chLower = char.ToLowerInvariant(ch);
            if (searchIndex < searchLower.Length && chLower == searchLower[searchIndex])
            {
                // Highlight matched character
                _buffer.SetColor(isSelected ? _config.HighlightColor : ConsoleColor.Green);
                _buffer.Write(ch.ToString());
                _buffer.SetColor(isSelected ? _config.SelectionColor : Theme.Text);
                searchIndex++;
            }
            else
            {
                _buffer.Write(ch.ToString());
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
