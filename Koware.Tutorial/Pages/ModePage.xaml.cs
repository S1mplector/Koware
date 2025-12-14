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
            await Terminal1.AddColoredLineAsync("{cyan}Select Mode (current: anime){/}", 100);
            await Terminal1.AddColoredLineAsync("{cyan}>{/} 📺 Anime Mode", 80);
            await Terminal1.AddColoredLineAsync("  📖 Manga Mode", 80);
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{gray}Search, watch, and track anime series{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware mode");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{cyan}Select Mode (current: anime){/}", 100);
            await Terminal2.AddColoredLineAsync("  📺 Anime Mode", 80);
            await Terminal2.AddColoredLineAsync("{cyan}>{/} 📖 Manga Mode", 80);
            Terminal2.AddEmptyLine();
            await Task.Delay(300);
            await Terminal2.AddColoredLineAsync("{magenta}Switched to MANGA mode.{/}", 0);
        }
        catch (TaskCanceledException) { }
    }
}
