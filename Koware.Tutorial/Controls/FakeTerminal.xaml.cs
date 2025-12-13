// Author: Ilgaz Mehmetoğlu
// Terminal-style preview control for displaying example commands and output.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Koware.Tutorial.Controls;

/// <summary>
/// A fake terminal control that displays styled command examples.
/// </summary>
public partial class FakeTerminal : UserControl
{
    // Terminal colors matching CLI output
    private static readonly SolidColorBrush PromptBrush = new(Color.FromRgb(34, 211, 238));   // Cyan
    private static readonly SolidColorBrush CommandBrush = new(Color.FromRgb(226, 232, 240)); // White
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(74, 222, 128));    // Green
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(250, 204, 21));   // Yellow
    private static readonly SolidColorBrush MagentaBrush = new(Color.FromRgb(244, 114, 182)); // Magenta
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(107, 114, 128));    // Gray
    private static readonly SolidColorBrush CyanBrush = new(Color.FromRgb(34, 211, 238));     // Cyan
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(226, 232, 240)); // Default

    public FakeTerminal()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Clear all terminal content.
    /// </summary>
    public void Clear()
    {
        TerminalContent.Document.Blocks.Clear();
    }

    /// <summary>
    /// Add a command prompt line (PS C:\Users\You>).
    /// </summary>
    public void AddPrompt(string command)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        paragraph.Inlines.Add(new Run("PS C:\\Users\\You> ") { Foreground = PromptBrush });
        paragraph.Inlines.Add(new Run(command) { Foreground = CommandBrush });
        TerminalContent.Document.Blocks.Add(paragraph);
    }

    /// <summary>
    /// Add a line with optional color.
    /// </summary>
    public void AddLine(string text, TerminalColor color = TerminalColor.Default)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        paragraph.Inlines.Add(new Run(text) { Foreground = GetBrush(color) });
        TerminalContent.Document.Blocks.Add(paragraph);
    }

    /// <summary>
    /// Add a line with mixed colors using markup: {cyan}text{/} {green}text{/}
    /// </summary>
    public void AddColoredLine(string markup)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        ParseAndAddInlines(paragraph.Inlines, markup);
        TerminalContent.Document.Blocks.Add(paragraph);
    }

    /// <summary>
    /// Add an empty line.
    /// </summary>
    public void AddEmptyLine()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        paragraph.Inlines.Add(new Run(" "));
        TerminalContent.Document.Blocks.Add(paragraph);
    }

    /// <summary>
    /// Add a header line (cyan, bold-ish).
    /// </summary>
    public void AddHeader(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 2) };
        paragraph.Inlines.Add(new Run(text) { Foreground = CyanBrush, FontWeight = FontWeights.SemiBold });
        TerminalContent.Document.Blocks.Add(paragraph);
    }

    /// <summary>
    /// Add a separator line.
    /// </summary>
    public void AddSeparator(int width = 50)
    {
        AddLine(new string('─', width), TerminalColor.Gray);
    }

    /// <summary>
    /// Add a selection item line like in fuzzy selector.
    /// </summary>
    public void AddSelectionItem(string text, bool isSelected = false, string? badge = null)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        
        if (isSelected)
        {
            paragraph.Inlines.Add(new Run(" > ") { Foreground = CyanBrush });
        }
        else
        {
            paragraph.Inlines.Add(new Run("   ") { Foreground = DefaultBrush });
        }

        if (badge != null)
        {
            paragraph.Inlines.Add(new Run($"[{badge}] ") { Foreground = GreenBrush });
        }

        paragraph.Inlines.Add(new Run(text) { Foreground = isSelected ? CommandBrush : GrayBrush });
        TerminalContent.Document.Blocks.Add(paragraph);
    }

    private void ParseAndAddInlines(InlineCollection inlines, string markup)
    {
        var currentBrush = DefaultBrush;
        var i = 0;
        var textStart = 0;

        while (i < markup.Length)
        {
            if (markup[i] == '{')
            {
                // Add text before tag
                if (i > textStart)
                {
                    inlines.Add(new Run(markup[textStart..i]) { Foreground = currentBrush });
                }

                var tagEnd = markup.IndexOf('}', i);
                if (tagEnd > i)
                {
                    var tag = markup[(i + 1)..tagEnd].ToLowerInvariant();
                    currentBrush = tag switch
                    {
                        "cyan" => CyanBrush,
                        "green" => GreenBrush,
                        "yellow" => YellowBrush,
                        "magenta" => MagentaBrush,
                        "gray" => GrayBrush,
                        "/" or "reset" => DefaultBrush,
                        _ => DefaultBrush
                    };
                    i = tagEnd + 1;
                    textStart = i;
                    continue;
                }
            }
            i++;
        }

        // Add remaining text
        if (textStart < markup.Length)
        {
            inlines.Add(new Run(markup[textStart..]) { Foreground = currentBrush });
        }
    }

    private static SolidColorBrush GetBrush(TerminalColor color) => color switch
    {
        TerminalColor.Cyan => CyanBrush,
        TerminalColor.Green => GreenBrush,
        TerminalColor.Yellow => YellowBrush,
        TerminalColor.Magenta => MagentaBrush,
        TerminalColor.Gray => GrayBrush,
        _ => DefaultBrush
    };
}

/// <summary>
/// Terminal output colors.
/// </summary>
public enum TerminalColor
{
    Default,
    Cyan,
    Green,
    Yellow,
    Magenta,
    Gray
}
