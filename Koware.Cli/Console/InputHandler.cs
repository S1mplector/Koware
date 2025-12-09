// Author: Ilgaz Mehmetoğlu
// Input handling for interactive TUI components.
using System;

namespace Koware.Cli.Console;

/// <summary>
/// Result of processing an input action.
/// </summary>
public enum InputAction
{
    None,
    MoveUp,
    MoveDown,
    PageUp,
    PageDown,
    JumpToStart,
    JumpToEnd,
    Select,
    Cancel,
    SearchCharacter,
    SearchBackspace,
    QuickJump
}

/// <summary>
/// Processed input result with action and optional data.
/// </summary>
public readonly struct InputResult
{
    public InputAction Action { get; init; }
    public char? Character { get; init; }
    public int? JumpIndex { get; init; }

    public static InputResult None => new() { Action = InputAction.None };
    public static InputResult Up => new() { Action = InputAction.MoveUp };
    public static InputResult Down => new() { Action = InputAction.MoveDown };
    public static InputResult PgUp => new() { Action = InputAction.PageUp };
    public static InputResult PgDown => new() { Action = InputAction.PageDown };
    public static InputResult Home => new() { Action = InputAction.JumpToStart };
    public static InputResult End => new() { Action = InputAction.JumpToEnd };
    public static InputResult Confirm => new() { Action = InputAction.Select };
    public static InputResult Escape => new() { Action = InputAction.Cancel };
    public static InputResult Backspace => new() { Action = InputAction.SearchBackspace };
    
    public static InputResult Search(char ch) => new() 
    { 
        Action = InputAction.SearchCharacter, 
        Character = ch 
    };
    
    public static InputResult Jump(int index) => new() 
    { 
        Action = InputAction.QuickJump, 
        JumpIndex = index 
    };
}

/// <summary>
/// Handles keyboard input for interactive selectors.
/// Responsible only for reading and categorizing input.
/// </summary>
public sealed class InputHandler
{
    private readonly bool _searchEnabled;

    public InputHandler(bool searchEnabled = true)
    {
        _searchEnabled = searchEnabled;
    }

    /// <summary>
    /// Read and process a single key press.
    /// </summary>
    /// <param name="searchActive">Whether search mode is currently active (has text).</param>
    /// <returns>Processed input result.</returns>
    public InputResult ReadKey(bool searchActive = false)
    {
        var key = System.Console.ReadKey(intercept: true);

        return key.Key switch
        {
            ConsoleKey.UpArrow => InputResult.Up,
            ConsoleKey.K when key.Modifiers == ConsoleModifiers.Control => InputResult.Up,
            
            ConsoleKey.DownArrow => InputResult.Down,
            ConsoleKey.J when key.Modifiers == ConsoleModifiers.Control => InputResult.Down,
            
            ConsoleKey.PageUp => InputResult.PgUp,
            ConsoleKey.PageDown => InputResult.PgDown,
            
            ConsoleKey.Home => InputResult.Home,
            ConsoleKey.End => InputResult.End,
            
            ConsoleKey.Enter => InputResult.Confirm,
            
            ConsoleKey.Escape => InputResult.Escape,
            ConsoleKey.C when key.Modifiers == ConsoleModifiers.Control => InputResult.Escape,
            
            ConsoleKey.Backspace => InputResult.Backspace,
            
            ConsoleKey.Tab => key.Modifiers == ConsoleModifiers.Shift 
                ? InputResult.Up 
                : InputResult.Down,

            // Quick number jump (1-9)
            ConsoleKey.D1 or ConsoleKey.D2 or ConsoleKey.D3 or 
            ConsoleKey.D4 or ConsoleKey.D5 or ConsoleKey.D6 or 
            ConsoleKey.D7 or ConsoleKey.D8 or ConsoleKey.D9 
                => HandleNumberKey(key, searchActive),

            _ => HandleCharacterKey(key)
        };
    }

    private InputResult HandleNumberKey(ConsoleKeyInfo key, bool searchActive)
    {
        // If search is active or modifiers present, treat as search character
        if (searchActive || key.Modifiers != 0)
        {
            if (_searchEnabled && !char.IsControl(key.KeyChar))
            {
                return InputResult.Search(key.KeyChar);
            }
            return InputResult.None;
        }

        // Otherwise, quick jump
        var jumpIndex = key.Key - ConsoleKey.D1;
        return InputResult.Jump(jumpIndex);
    }

    private InputResult HandleCharacterKey(ConsoleKeyInfo key)
    {
        if (_searchEnabled && !char.IsControl(key.KeyChar))
        {
            return InputResult.Search(key.KeyChar);
        }
        return InputResult.None;
    }
}
