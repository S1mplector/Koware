// Author: Ilgaz Mehmetoğlu
// Managing Lists tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ManagingListsPage : Page
{
    public ManagingListsPage()
    {
        InitializeComponent();
        Terminal1.Loaded += async (s, e) => await AnimateTerminalAsync();
    }

    private async Task AnimateTerminalAsync()
    {
        try
        {
            Terminal1.Clear();
            await Terminal1.TypePromptAsync("koware list");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{cyan}> Anime List [12/12] ^v0%{/}", 100);
            await Terminal1.AddColoredLineAsync("  {gray}[?]{/} {cyan}▌{/}", 80);
            Terminal1.AddSeparator(55);
            await Task.Delay(100);
            await Terminal1.AddColoredLineAsync(" {cyan}>{/} {green}[1]{/} {green}[Watching]{/}  12/28  Frieren: Beyond Journey's End", 80);
            await Terminal1.AddColoredLineAsync("   {green}[2]{/} {green}[Watching]{/}  5/24   Solo Leveling", 80);
            await Terminal1.AddColoredLineAsync("   {green}[3]{/} {cyan}[Completed]{/} 13/13  Bocchi the Rock!", 80);
            await Terminal1.AddColoredLineAsync("   {green}[4]{/} {magenta}[Plan]{/}      0/24   Spy x Family", 80);
            await Terminal1.AddColoredLineAsync("   {green}[5]{/} {yellow}[On Hold]{/}   8/25   Vinland Saga", 80);
            Terminal1.AddSeparator(55);
            await Terminal1.AddColoredLineAsync("  {gray}[#] Score: 9/10 | Started: 2024-01-15{/}", 0);
        }
        catch (TaskCanceledException) { }
    }
}
