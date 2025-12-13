// Author: Ilgaz Mehmetoğlu
// Mode switching tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ModePage : Page
{
    public ModePage()
    {
        InitializeComponent();
        Terminal1.Loaded += async (s, e) => await AnimateTerminal1Async();
        Terminal2.Loaded += async (s, e) => await AnimateTerminal2Async();
        Terminal3.Loaded += async (s, e) => await AnimateTerminal3Async();
    }

    private async Task AnimateTerminal1Async()
    {
        try
        {
            Terminal1.Clear();
            await Terminal1.TypePromptAsync("koware mode");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}Current mode:{/} anime", 100);
            await Terminal1.AddColoredLineAsync("{gray}Use 'koware mode manga' to switch{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware -m read \"one punch man\"");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{cyan}[Manga Mode]{/} Searching...", 200);
            await Terminal2.AddColoredLineAsync("{green}✓{/} Found: One Punch Man", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal3Async()
    {
        try
        {
            Terminal3.Clear();
            await Terminal3.TypePromptAsync("koware mode manga");
            Terminal3.AddEmptyLine();
            await Terminal3.AddColoredLineAsync("{green}✓{/} Default mode set to: manga", 200);
            Terminal3.AddEmptyLine();
            await Terminal3.AddColoredLineAsync("{gray}# Now all commands default to manga mode:{/}", 150);
            await Terminal3.TypePromptAsync("koware read \"solo leveling\"");
            await Terminal3.AddColoredLineAsync("{cyan}Searching...{/}", 200);
            await Terminal3.AddColoredLineAsync("{green}✓{/} Found: Solo Leveling", 0);
        }
        catch (TaskCanceledException) { }
    }
}
