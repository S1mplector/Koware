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
            await Terminal2.TypePromptAsync("koware mode manga");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{green}✓{/} Default mode set to: manga", 200);
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{gray}# Now all commands default to manga mode:{/}", 150);
            await Terminal2.TypePromptAsync("koware read \"solo leveling\"");
            await Terminal2.AddColoredLineAsync("{cyan}Searching...{/}", 200);
            await Terminal2.AddColoredLineAsync("{green}✓{/} Found: Solo Leveling", 0);
        }
        catch (TaskCanceledException) { }
    }
}
