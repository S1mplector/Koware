// Author: Ilgaz Mehmetoğlu
// Watching Anime tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class WatchingAnimePage : Page
{
    public WatchingAnimePage()
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
            await Terminal1.TypePromptAsync("koware watch \"frieren\"");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}Searching...{/}", 300);
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{green}✓{/} Found 3 results", 200);
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{gray}You can also specify episode:{/}", 100);
            await Terminal1.TypePromptAsync("koware watch \"frieren\" --episode 5");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{gray}Or quality:{/}", 100);
            await Terminal1.TypePromptAsync("koware watch \"frieren\" --quality 720p");
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.AddColoredLineAsync("{cyan}> Search Results [3/3] ^v0%{/}", 100);
            await Terminal2.AddColoredLineAsync("  {gray}[?]{/} frier{cyan}▌{/}", 80);
            Terminal2.AddSeparator(55);
            await Task.Delay(100);
            await Terminal2.AddColoredLineAsync(" {cyan}>{/} {green}[1]{/} Frieren: Beyond Journey's End", 80);
            await Terminal2.AddColoredLineAsync("   {green}[2]{/} Frieren: Beyond Journey's End (Dub)", 80);
            await Terminal2.AddColoredLineAsync("   {green}[3]{/} Frieren Specials", 80);
            Terminal2.AddSeparator(55);
            await Terminal2.AddColoredLineAsync("  {gray}[#] 28 episodes | Sub | 1080p{/}", 0);
        }
        catch (TaskCanceledException) { }
    }
}
