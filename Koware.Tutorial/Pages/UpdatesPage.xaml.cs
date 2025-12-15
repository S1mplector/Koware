// Author: Ilgaz Mehmetoğlu
// Updates tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class UpdatesPage : Page
{
    public UpdatesPage()
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
            await Terminal1.TypePromptAsync("koware update --check");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}Checking for updates...{/}", 200);
            await Terminal1.AddColoredLineAsync("{green}✓{/} New version available: v0.9.0", 150);
            await Terminal1.AddColoredLineAsync("{gray}  Current: v0.9.0-beta → Latest: v0.9.1{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware update");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{cyan}Downloading v0.9.0...{/}", 200);
            await Terminal2.AddColoredLineAsync("{gray}  [████████████████████] 100%{/}", 300);
            await Terminal2.AddColoredLineAsync("{cyan}Installing update...{/}", 200);
            await Terminal2.AddColoredLineAsync("{green}✓{/} Update complete! Restart Koware to use v0.9.0", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal3Async()
    {
        try
        {
            Terminal3.Clear();
            await Terminal3.TypePromptAsync("koware --version");
            await Terminal3.AddColoredLineAsync("{cyan}Koware{/} v0.9.0-beta (net8.0)", 0);
        }
        catch (TaskCanceledException) { }
    }
}
