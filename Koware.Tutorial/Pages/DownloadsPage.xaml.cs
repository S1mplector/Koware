// Author: Ilgaz Mehmetoğlu
// Downloads & Offline tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class DownloadsPage : Page
{
    public DownloadsPage()
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
            await Terminal1.TypePromptAsync("koware download \"demon slayer\" -e 1-3 -q 1080p");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}Searching...{/}", 150);
            await Terminal1.AddColoredLineAsync("{green}✓{/} Found: Demon Slayer: Kimetsu no Yaiba", 100);
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}Downloading episode 1/3...{/}", 200);
            await Terminal1.AddColoredLineAsync("{green}✓{/} Episode 1 saved (1.2 GB)", 150);
            await Terminal1.AddColoredLineAsync("{cyan}Downloading episode 2/3...{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware download \"one piece\" -c 1-5 --cbz");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{cyan}Searching...{/}", 150);
            await Terminal2.AddColoredLineAsync("{green}✓{/} Found: One Piece", 100);
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{green}✓{/} Downloaded 5 chapters as CBZ archives", 200);
            await Terminal2.AddColoredLineAsync("{gray}  ~/Downloads/Koware/One Piece/{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal3Async()
    {
        try
        {
            Terminal3.Clear();
            await Terminal3.TypePromptAsync("koware config get downloads.path");
            await Terminal3.AddColoredLineAsync("{cyan}downloads.path:{/} ~/Downloads/Koware", 0);
        }
        catch (TaskCanceledException) { }
    }
}
