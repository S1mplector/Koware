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
            await Terminal2.AddColoredLineAsync("{gray}# Browse watch history interactively:{/}", 80);
            await Terminal2.TypePromptAsync("koware history");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{gray}# View downloaded content:{/}", 80);
            await Terminal2.TypePromptAsync("koware offline");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{gray}# Get recommendations based on history:{/}", 80);
            await Terminal2.TypePromptAsync("koware recommend");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{gray}# Check for updates:{/}", 80);
            await Terminal2.TypePromptAsync("koware --update");
        }
        catch (TaskCanceledException) { }
    }
}
