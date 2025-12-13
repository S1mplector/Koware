// Author: Ilgaz Mehmetoğlu
// Watching Anime tutorial page.
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class WatchingAnimePage : Page
{
    public WatchingAnimePage()
    {
        InitializeComponent();
        PopulateTerminals();
    }

    private void PopulateTerminals()
    {
        // Terminal 1: Basic command
        Terminal1.Clear();
        Terminal1.AddPrompt("koware watch \"frieren\"");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{cyan}Searching...{/}");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{green}✓{/} Found 3 results");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{gray}You can also specify episode:{/}");
        Terminal1.AddPrompt("koware watch \"frieren\" --episode 5");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{gray}Or quality:{/}");
        Terminal1.AddPrompt("koware watch \"frieren\" --quality 720p");

        // Terminal 2: Fuzzy selector (matching actual UI)
        Terminal2.Clear();
        Terminal2.AddHeader("> Search Results [3/3] ^v0%");
        Terminal2.AddColoredLine("  {gray}[?]{/} frier{cyan}▌{/}");
        Terminal2.AddSeparator(55);
        Terminal2.AddColoredLine(" {cyan}>{/} {green}[1]{/} Frieren: Beyond Journey's End");
        Terminal2.AddColoredLine("   {green}[2]{/} Frieren: Beyond Journey's End (Dub)");
        Terminal2.AddColoredLine("   {green}[3]{/} Frieren Specials");
        Terminal2.AddSeparator(55);
        Terminal2.AddColoredLine("  {gray}[#] 28 episodes | Sub | 1080p{/}");
    }
}
