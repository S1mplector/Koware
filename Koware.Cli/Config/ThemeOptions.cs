// Author: Ilgaz Mehmetoğlu
// Theme configuration for TUI colors and styling.
using System;
using System.Collections.Generic;

namespace Koware.Cli.Config;

/// <summary>
/// Theme configuration options for the CLI interface.
/// </summary>
public sealed class ThemeOptions
{
    /// <summary>Theme preset name (default, dracula, nord, gruvbox, monokai, solarized, catppuccin).</summary>
    public string Preset { get; set; } = "default";

    /// <summary>Custom color overrides.</summary>
    public ThemeColors? Colors { get; set; }

    /// <summary>Get the effective theme colors based on preset and overrides.</summary>
    public ThemeColors GetEffectiveTheme()
    {
        var baseTheme = ThemePresets.Get(Preset);
        if (Colors is null) return baseTheme;

        // Apply overrides
        return new ThemeColors
        {
            Primary = ParseColor(Colors.Primary) ?? baseTheme.Primary,
            Secondary = ParseColor(Colors.Secondary) ?? baseTheme.Secondary,
            Accent = ParseColor(Colors.Accent) ?? baseTheme.Accent,
            Success = ParseColor(Colors.Success) ?? baseTheme.Success,
            Warning = ParseColor(Colors.Warning) ?? baseTheme.Warning,
            Error = ParseColor(Colors.Error) ?? baseTheme.Error,
            Muted = ParseColor(Colors.Muted) ?? baseTheme.Muted,
            Text = ParseColor(Colors.Text) ?? baseTheme.Text,
            Highlight = ParseColor(Colors.Highlight) ?? baseTheme.Highlight,
            Selection = ParseColor(Colors.Selection) ?? baseTheme.Selection
        };
    }

    private static ConsoleColor? ParseColor(ConsoleColor? color) => color;
}

/// <summary>
/// Individual theme color definitions.
/// </summary>
public sealed class ThemeColors
{
    /// <summary>Primary color for headers and prompts.</summary>
    public ConsoleColor Primary { get; set; } = ConsoleColor.Cyan;

    /// <summary>Secondary color for subtitles and labels.</summary>
    public ConsoleColor Secondary { get; set; } = ConsoleColor.Blue;

    /// <summary>Accent color for important elements.</summary>
    public ConsoleColor Accent { get; set; } = ConsoleColor.Magenta;

    /// <summary>Success color for completed items.</summary>
    public ConsoleColor Success { get; set; } = ConsoleColor.Green;

    /// <summary>Warning color for alerts.</summary>
    public ConsoleColor Warning { get; set; } = ConsoleColor.Yellow;

    /// <summary>Error color for failures.</summary>
    public ConsoleColor Error { get; set; } = ConsoleColor.Red;

    /// <summary>Muted color for secondary text.</summary>
    public ConsoleColor Muted { get; set; } = ConsoleColor.DarkGray;

    /// <summary>Default text color.</summary>
    public ConsoleColor Text { get; set; } = ConsoleColor.White;

    /// <summary>Highlight color for search matches.</summary>
    public ConsoleColor Highlight { get; set; } = ConsoleColor.Green;

    /// <summary>Selection color for selected items.</summary>
    public ConsoleColor Selection { get; set; } = ConsoleColor.Yellow;
}

/// <summary>
/// Built-in theme presets.
/// </summary>
public static class ThemePresets
{
    private static readonly Dictionary<string, ThemeColors> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = new ThemeColors
        {
            Primary = ConsoleColor.Cyan,
            Secondary = ConsoleColor.Blue,
            Accent = ConsoleColor.Magenta,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.White,
            Highlight = ConsoleColor.Green,
            Selection = ConsoleColor.Yellow
        },
        ["dracula"] = new ThemeColors
        {
            Primary = ConsoleColor.Magenta,
            Secondary = ConsoleColor.DarkMagenta,
            Accent = ConsoleColor.Cyan,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.White,
            Highlight = ConsoleColor.Cyan,
            Selection = ConsoleColor.Magenta
        },
        ["nord"] = new ThemeColors
        {
            Primary = ConsoleColor.Cyan,
            Secondary = ConsoleColor.DarkCyan,
            Accent = ConsoleColor.Blue,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.White,
            Highlight = ConsoleColor.Cyan,
            Selection = ConsoleColor.Blue
        },
        ["gruvbox"] = new ThemeColors
        {
            Primary = ConsoleColor.Yellow,
            Secondary = ConsoleColor.DarkYellow,
            Accent = ConsoleColor.Red,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.White,
            Highlight = ConsoleColor.Yellow,
            Selection = ConsoleColor.DarkYellow
        },
        ["monokai"] = new ThemeColors
        {
            Primary = ConsoleColor.Magenta,
            Secondary = ConsoleColor.DarkMagenta,
            Accent = ConsoleColor.Green,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.White,
            Highlight = ConsoleColor.Green,
            Selection = ConsoleColor.Magenta
        },
        ["solarized"] = new ThemeColors
        {
            Primary = ConsoleColor.Blue,
            Secondary = ConsoleColor.DarkBlue,
            Accent = ConsoleColor.Cyan,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.Gray,
            Highlight = ConsoleColor.Cyan,
            Selection = ConsoleColor.Blue
        },
        ["catppuccin"] = new ThemeColors
        {
            Primary = ConsoleColor.Magenta,
            Secondary = ConsoleColor.Blue,
            Accent = ConsoleColor.Cyan,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.Yellow,
            Error = ConsoleColor.Red,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.White,
            Highlight = ConsoleColor.Magenta,
            Selection = ConsoleColor.Cyan
        },
        ["hacker"] = new ThemeColors
        {
            Primary = ConsoleColor.Green,
            Secondary = ConsoleColor.DarkGreen,
            Accent = ConsoleColor.Green,
            Success = ConsoleColor.Green,
            Warning = ConsoleColor.DarkYellow,
            Error = ConsoleColor.DarkRed,
            Muted = ConsoleColor.DarkGray,
            Text = ConsoleColor.Green,
            Highlight = ConsoleColor.White,
            Selection = ConsoleColor.Green
        }
    };

    /// <summary>Get a theme preset by name.</summary>
    public static ThemeColors Get(string name)
    {
        return Presets.TryGetValue(name, out var theme) ? theme : Presets["default"];
    }

    /// <summary>Get all available preset names.</summary>
    public static IEnumerable<string> GetNames() => Presets.Keys;
}

/// <summary>
/// Global theme provider for the application.
/// </summary>
public static class Theme
{
    private static ThemeColors _current = ThemePresets.Get("default");

    /// <summary>Current active theme.</summary>
    public static ThemeColors Current => _current;

    /// <summary>Initialize theme from options.</summary>
    public static void Initialize(ThemeOptions? options)
    {
        _current = options?.GetEffectiveTheme() ?? ThemePresets.Get("default");
    }

    /// <summary>Set theme by preset name.</summary>
    public static void SetPreset(string name)
    {
        _current = ThemePresets.Get(name);
    }

    // Convenience accessors
    public static ConsoleColor Primary => _current.Primary;
    public static ConsoleColor Secondary => _current.Secondary;
    public static ConsoleColor Accent => _current.Accent;
    public static ConsoleColor Success => _current.Success;
    public static ConsoleColor Warning => _current.Warning;
    public static ConsoleColor Error => _current.Error;
    public static ConsoleColor Muted => _current.Muted;
    public static ConsoleColor Text => _current.Text;
    public static ConsoleColor Highlight => _current.Highlight;
    public static ConsoleColor Selection => _current.Selection;
}
