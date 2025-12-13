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
        Terminal1.AddHeader("❯ Anime List [12/12]");
        Terminal1.AddSeparator(50);
        Terminal1.AddColoredLine("  {gray}🔍{/} {cyan}▌{/}");
        Terminal1.AddSeparator(50);
        Terminal1.AddColoredLine(" {cyan}>{/} {green}[Watching]{/}     {gray}12/28{/}     Frieren: Beyond Journey's End");
        Terminal1.AddColoredLine("   {green}[Watching]{/}     {gray}5/24{/}      Solo Leveling");
        Terminal1.AddColoredLine("   {cyan}[Completed]{/}    {gray}13/13{/}     Bocchi the Rock!");
        Terminal1.AddColoredLine("   {magenta}[Plan to Watch]{/} {gray}0/24{/}      Spy x Family");
        Terminal1.AddColoredLine("   {yellow}[On Hold]{/}      {gray}8/25{/}      Vinland Saga");
        Terminal1.AddSeparator(50);
        Terminal1.AddColoredLine("{gray}[Enter] Actions  [s] Status  [e] Edit  [d] Delete  [p] Play{/}");
    }
}
