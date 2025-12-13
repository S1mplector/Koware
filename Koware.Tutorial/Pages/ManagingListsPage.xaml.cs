// Author: Ilgaz Mehmetoğlu
// Managing Lists tutorial page.
using System.Windows.Controls;
using Koware.Tutorial.Controls;

namespace Koware.Tutorial.Pages;

public partial class ManagingListsPage : Page
{
    public ManagingListsPage()
    {
        InitializeComponent();
        PopulateTerminal();
    }

    private void PopulateTerminal()
    {
        Terminal1.Clear();
        Terminal1.AddHeader("> Anime List [12/12] ^v0%");
        Terminal1.AddColoredLine("  {gray}[?]{/} {cyan}▌{/}");
        Terminal1.AddSeparator(55);
        Terminal1.AddColoredLine(" {cyan}>{/} {green}[1]{/} {green}[Watching]{/}  12/28  Frieren: Beyond Journey's End");
        Terminal1.AddColoredLine("   {green}[2]{/} {green}[Watching]{/}  5/24   Solo Leveling");
        Terminal1.AddColoredLine("   {green}[3]{/} {cyan}[Completed]{/} 13/13  Bocchi the Rock!");
        Terminal1.AddColoredLine("   {green}[4]{/} {magenta}[Plan]{/}      0/24   Spy x Family");
        Terminal1.AddColoredLine("   {green}[5]{/} {yellow}[On Hold]{/}   8/25   Vinland Saga");
        Terminal1.AddSeparator(55);
        Terminal1.AddColoredLine("  {gray}[#] Score: 9/10 | Started: 2024-01-15{/}");
    }
}
