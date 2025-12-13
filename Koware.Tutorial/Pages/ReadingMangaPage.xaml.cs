// Author: Ilgaz Mehmetoğlu
// Reading Manga tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ReadingMangaPage : Page
{
    public ReadingMangaPage()
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
            await Terminal1.AddColoredLineAsync("{gray}# Use -m flag for manga commands:{/}", 100);
            await Terminal1.TypePromptAsync("koware -m read \"one punch man\"");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{gray}# Or set default mode to manga:{/}", 100);
            await Terminal1.TypePromptAsync("koware config --mode manga");
            await Terminal1.AddColoredLineAsync("{green}✓{/} Default mode set to manga", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware -m read \"tokyo ghoul\" --chapter 50");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{cyan}Searching...{/}", 300);
            await Terminal2.AddColoredLineAsync("{green}✓{/} Found: Tokyo Ghoul", 150);
            await Terminal2.AddColoredLineAsync("{cyan}Loading chapter 50...{/}", 300);
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{green}✓{/} Opening reader (24 pages)", 100);
            await Terminal2.AddColoredLineAsync("{gray}Press Esc to exit reader{/}", 0);
        }
        catch (TaskCanceledException) { }
    }
}
