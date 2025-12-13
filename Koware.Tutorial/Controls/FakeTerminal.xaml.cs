// Author: Ilgaz Mehmetoğlu
// Terminal-style preview control for displaying example commands and output.
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Koware.Tutorial.Controls;

/// <summary>
/// A fake terminal control that displays styled command examples with typing animation.
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

    // Commands list for copy functionality
    private readonly List<string> _commands = new();
    
    // Animation control
    private CancellationTokenSource? _animationCts;
    private bool _isAnimating;

    public FakeTerminal()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Start animation when control becomes visible
        if (AutoAnimate && !_isAnimating)
        {
            _ = StartAnimationAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Cancel any running animation
        _animationCts?.Cancel();
    }

    /// <summary>
    /// Whether to auto-animate when the control loads.
    /// </summary>
    public bool AutoAnimate { get; set; } = false;

    /// <summary>
    /// Typing speed in milliseconds per character.
    /// </summary>
    public int TypingSpeed { get; set; } = 35;

    /// <summary>
    /// Delay after typing a command before showing output.
    /// </summary>
    public int OutputDelay { get; set; } = 300;

    /// <summary>
    /// Copy button click handler.
    /// </summary>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commands.Count > 0)
        {
            var text = string.Join("\n", _commands);
            Clipboard.SetText(text);
            
            // Visual feedback - temporarily change button text
            if (CopyButton.Template.FindName("label", CopyButton) is TextBlock label)
            {
                var original = label.Text;
                label.Text = " Copied!";
                label.Foreground = GreenBrush;
                
                // Reset after delay
                Task.Delay(1500).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        label.Text = original;
                        label.Foreground = GrayBrush;
                    });
                });
            }
        }
    }

    /// <summary>
    /// Start the typing animation sequence.
    /// </summary>
    public async Task StartAnimationAsync()
    {
        _animationCts?.Cancel();
        _animationCts = new CancellationTokenSource();
        _isAnimating = true;
        
        try
        {
            await AnimateContentAsync(_animationCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Animation was cancelled, that's fine
        }
        finally
        {
            _isAnimating = false;
        }
    }

    /// <summary>
    /// Override in derived classes or set via delegate to provide animation content.
    /// </summary>
    protected virtual Task AnimateContentAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Animation action to queue for playback.
    /// </summary>
    public Func<CancellationToken, Task>? AnimationSequence { get; set; }

    /// <summary>
    /// Type a command with animation.
    /// </summary>
    public async Task TypePromptAsync(string command, CancellationToken ct = default)
    {
        _commands.Add(command);
        
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        paragraph.Inlines.Add(new Run("PS C:\\Users\\You> ") { Foreground = PromptBrush });
        
        var commandRun = new Run { Foreground = CommandBrush };
        paragraph.Inlines.Add(commandRun);
        
        // Add blinking cursor
        var cursorRun = new Run("▌") { Foreground = CyanBrush };
        paragraph.Inlines.Add(cursorRun);
        
        TerminalContent.Document.Blocks.Add(paragraph);
        
        // Type each character
        foreach (var c in command)
        {
            ct.ThrowIfCancellationRequested();
            commandRun.Text += c;
            await Task.Delay(TypingSpeed, ct);
        }
        
        // Remove cursor after typing
        paragraph.Inlines.Remove(cursorRun);
        
        await Task.Delay(OutputDelay, ct);
    }

    /// <summary>
    /// Add output line with optional delay for animation effect.
    /// </summary>
    public async Task AddLineAsync(string text, TerminalColor color = TerminalColor.Default, int delay = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AddLine(text, color);
        if (delay > 0)
            await Task.Delay(delay, ct);
    }

    /// <summary>
    /// Add colored line with animation delay.
    /// </summary>
    public async Task AddColoredLineAsync(string markup, int delay = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AddColoredLine(markup);
        if (delay > 0)
            await Task.Delay(delay, ct);
    }

    /// <summary>
    /// Clear all terminal content and commands list.
    /// </summary>
    public void Clear()
    {
        TerminalContent.Document.Blocks.Clear();
        _commands.Clear();
    }

    /// <summary>
    /// Add a command prompt line (PS C:\Users\You>).
    /// </summary>
    public void AddPrompt(string command)
    {
        _commands.Add(command); // Track for copy
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
