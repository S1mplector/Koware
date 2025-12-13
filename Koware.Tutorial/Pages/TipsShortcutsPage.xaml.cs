// Author: Ilgaz Mehmetoğlu
// Tips & Shortcuts tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class TipsShortcutsPage : Page
{
    public TipsShortcutsPage()
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
            await Terminal1.AddColoredLineAsync("{gray}# Resume your last watched anime:{/}", 100);
            await Terminal1.TypePromptAsync("koware continue");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}Resuming:{/} Frieren: Beyond Journey's End", 150);
            await Terminal1.AddColoredLineAsync("{green}▶{/} Episode 13", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware history");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync(" {cyan}>{/} Watch History [5/5]", 100);
            Terminal2.AddSeparator(70);
            await Terminal2.AddColoredLineAsync("  {gray}[?]{/} {cyan}▌{/}", 80);
            Terminal2.AddSeparator(70);
            await Task.Delay(100);
            await Terminal2.AddColoredLineAsync(" {cyan}>{/} Ep 1    9h ago       Frieren: Beyond Journey's End", 60);
            await Terminal2.AddColoredLineAsync("   Ep 11   1d ago       Solo Leveling", 60);
            await Terminal2.AddColoredLineAsync("   Ep 1    3d ago       Bocchi the Rock!", 60);
            await Terminal2.AddColoredLineAsync("   Ep 1    1w ago       Spy x Family", 60);
            await Terminal2.AddColoredLineAsync("   Ep 1    2w ago       One Punch Man", 60);
            Terminal2.AddSeparator(70);
            await Terminal2.AddColoredLineAsync(" {gray}[Enter] Resume next episode  [Esc] Exit{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal3Async()
    {
        try
        {
            Terminal3.Clear();
            await Terminal3.AddColoredLineAsync("{gray}# View downloaded content:{/}", 80);
            await Terminal3.TypePromptAsync("koware offline");
            Terminal3.AddEmptyLine();
            await Terminal3.AddColoredLineAsync("{gray}# Get recommendations based on history:{/}", 80);
            await Terminal3.TypePromptAsync("koware recommend");
            Terminal3.AddEmptyLine();
            await Terminal3.AddColoredLineAsync("{gray}# Check for updates:{/}", 80);
            await Terminal3.TypePromptAsync("koware update");
        }
        catch (TaskCanceledException) { }
    }
}
