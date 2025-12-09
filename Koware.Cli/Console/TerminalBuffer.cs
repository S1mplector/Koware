// Author: Ilgaz Mehmetoğlu
// Low-level terminal operations with ANSI escape codes and double-buffering.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Koware.Cli.Console;

/// <summary>
/// Handles low-level terminal operations including ANSI escape sequences,
/// alternate screen buffer, and double-buffering for flicker-free rendering.
/// </summary>
public sealed class TerminalBuffer : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private readonly bool _useAlternateScreen;
    private bool _inAlternateScreen;
    private int _lastRenderedHeight;
    
    // ANSI escape sequences
    private const string Esc = "\x1b";
    private const string ClearLine = $"{Esc}[2K";
    private const string ClearToEnd = $"{Esc}[K";
    private const string HideCursor = $"{Esc}[?25l";
    private const string ShowCursor = $"{Esc}[?25h";
    private const string SaveCursor = $"{Esc}[s";
    private const string RestoreCursor = $"{Esc}[u";
    private const string EnterAltScreen = $"{Esc}[?1049h";
    private const string ExitAltScreen = $"{Esc}[?1049l";
    private const string ResetAttributes = $"{Esc}[0m";

    /// <summary>
    /// Create a terminal buffer.
    /// </summary>
    /// <param name="useAlternateScreen">Whether to use the alternate screen buffer.</param>
    public TerminalBuffer(bool useAlternateScreen = false)
    {
        _useAlternateScreen = useAlternateScreen && !IsOutputRedirected();
    }

    /// <summary>
    /// Current terminal width.
    /// </summary>
    public int Width => GetTerminalWidth();

    /// <summary>
    /// Current terminal height.
    /// </summary>
    public int Height => GetTerminalHeight();

    /// <summary>
    /// Initialize the terminal for TUI rendering.
    /// </summary>
    public void Initialize()
    {
        if (_useAlternateScreen && !_inAlternateScreen)
        {
            System.Console.Write(EnterAltScreen);
            _inAlternateScreen = true;
        }
        
        System.Console.Write(HideCursor);
        System.Console.Out.Flush();
    }

    /// <summary>
    /// Restore terminal to normal state.
    /// </summary>
    public void Restore()
    {
        System.Console.Write(ShowCursor);
        System.Console.Write(ResetAttributes);
        
        if (_inAlternateScreen)
        {
            System.Console.Write(ExitAltScreen);
            _inAlternateScreen = false;
        }
        
        System.Console.Out.Flush();
    }

    /// <summary>
    /// Begin a new frame. Clears the internal buffer.
    /// </summary>
    public void BeginFrame()
    {
        _buffer.Clear();
    }

    /// <summary>
    /// Write text to the buffer.
    /// </summary>
    public void Write(string text)
    {
        _buffer.Append(text);
    }

    /// <summary>
    /// Write text with a specific color.
    /// </summary>
    public void Write(string text, ConsoleColor color)
    {
        _buffer.Append(GetAnsiColor(color));
        _buffer.Append(text);
        _buffer.Append(ResetAttributes);
    }

    /// <summary>
    /// Write a line to the buffer.
    /// </summary>
    public void WriteLine(string text = "")
    {
        _buffer.Append(text);
        _buffer.Append(ClearToEnd);
        _buffer.AppendLine();
    }

    /// <summary>
    /// Write a line with a specific color.
    /// </summary>
    public void WriteLine(string text, ConsoleColor color)
    {
        _buffer.Append(GetAnsiColor(color));
        _buffer.Append(text);
        _buffer.Append(ResetAttributes);
        _buffer.Append(ClearToEnd);
        _buffer.AppendLine();
    }

    /// <summary>
    /// Set foreground color for subsequent writes.
    /// </summary>
    public void SetColor(ConsoleColor color)
    {
        _buffer.Append(GetAnsiColor(color));
    }

    /// <summary>
    /// Reset text attributes.
    /// </summary>
    public void ResetColor()
    {
        _buffer.Append(ResetAttributes);
    }

    /// <summary>
    /// Clear from cursor to end of line.
    /// </summary>
    public void ClearToEndOfLine()
    {
        _buffer.Append(ClearToEnd);
    }

    /// <summary>
    /// Clear the entire current line.
    /// </summary>
    public void ClearEntireLine()
    {
        _buffer.Append(ClearLine);
    }

    /// <summary>
    /// Move cursor to specific position.
    /// </summary>
    public void MoveTo(int row, int col)
    {
        _buffer.Append($"{Esc}[{row + 1};{col + 1}H");
    }

    /// <summary>
    /// Move cursor up by n lines.
    /// </summary>
    public void MoveUp(int lines = 1)
    {
        if (lines > 0)
            _buffer.Append($"{Esc}[{lines}A");
    }

    /// <summary>
    /// Move cursor down by n lines.
    /// </summary>
    public void MoveDown(int lines = 1)
    {
        if (lines > 0)
            _buffer.Append($"{Esc}[{lines}B");
    }

    /// <summary>
    /// Move cursor to beginning of line.
    /// </summary>
    public void MoveToLineStart()
    {
        _buffer.Append('\r');
    }

    /// <summary>
    /// End the frame and flush all buffered content to the terminal.
    /// This provides atomic, flicker-free updates.
    /// </summary>
    /// <param name="startRow">Starting row for rendering (0-based).</param>
    /// <param name="totalLines">Total lines rendered in this frame.</param>
    public void EndFrame(int startRow, int totalLines)
    {
        // Move to start position
        System.Console.SetCursorPosition(0, startRow);
        
        // Clear any extra lines from previous render
        if (_lastRenderedHeight > totalLines)
        {
            var output = new StringBuilder(_buffer.ToString());
            for (var i = totalLines; i < _lastRenderedHeight; i++)
            {
                output.Append(ClearLine);
                output.AppendLine();
            }
            System.Console.Write(output.ToString());
        }
        else
        {
            System.Console.Write(_buffer.ToString());
        }
        
        System.Console.Out.Flush();
        _lastRenderedHeight = totalLines;
    }

    /// <summary>
    /// Clear the rendering area.
    /// </summary>
    /// <param name="startRow">Starting row.</param>
    /// <param name="lineCount">Number of lines to clear.</param>
    public void ClearArea(int startRow, int lineCount)
    {
        try
        {
            for (var i = 0; i < lineCount; i++)
            {
                System.Console.SetCursorPosition(0, startRow + i);
                System.Console.Write(ClearLine);
            }
            System.Console.SetCursorPosition(0, startRow);
            System.Console.Out.Flush();
        }
        catch
        {
            // Ignore errors on non-standard terminals
        }
    }

    /// <summary>
    /// Reserve space in the terminal to prevent scroll glitches.
    /// </summary>
    /// <param name="linesNeeded">Number of lines needed for the TUI.</param>
    /// <returns>The starting row for rendering.</returns>
    public int ReserveSpace(int linesNeeded)
    {
        if (_inAlternateScreen)
        {
            // In alternate screen, always start from top
            return 0;
        }

        try
        {
            var currentRow = System.Console.CursorTop;
            var availableLines = Height - currentRow;

            if (availableLines < linesNeeded)
            {
                // Scroll by printing blank lines
                var linesToScroll = linesNeeded - availableLines;
                for (var i = 0; i < linesToScroll; i++)
                {
                    System.Console.WriteLine();
                }
                return Math.Max(0, Height - linesNeeded);
            }

            return currentRow;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        Restore();
    }

    private static string GetAnsiColor(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => $"{Esc}[30m",
        ConsoleColor.DarkBlue => $"{Esc}[34m",
        ConsoleColor.DarkGreen => $"{Esc}[32m",
        ConsoleColor.DarkCyan => $"{Esc}[36m",
        ConsoleColor.DarkRed => $"{Esc}[31m",
        ConsoleColor.DarkMagenta => $"{Esc}[35m",
        ConsoleColor.DarkYellow => $"{Esc}[33m",
        ConsoleColor.Gray => $"{Esc}[37m",
        ConsoleColor.DarkGray => $"{Esc}[90m",
        ConsoleColor.Blue => $"{Esc}[94m",
        ConsoleColor.Green => $"{Esc}[92m",
        ConsoleColor.Cyan => $"{Esc}[96m",
        ConsoleColor.Red => $"{Esc}[91m",
        ConsoleColor.Magenta => $"{Esc}[95m",
        ConsoleColor.Yellow => $"{Esc}[93m",
        ConsoleColor.White => $"{Esc}[97m",
        _ => ResetAttributes
    };

    private static int GetTerminalWidth()
    {
        try { return System.Console.WindowWidth; }
        catch { return 80; }
    }

    private static int GetTerminalHeight()
    {
        try { return System.Console.WindowHeight; }
        catch { return 24; }
    }

    private static bool IsOutputRedirected()
    {
        try { return System.Console.IsOutputRedirected; }
        catch { return true; }
    }
}
