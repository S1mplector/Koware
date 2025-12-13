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

        // Terminal 2: Fuzzy selector
        Terminal2.Clear();
        Terminal2.AddHeader("❯ Select Anime");
        Terminal2.AddSeparator(45);
        Terminal2.AddColoredLine("  {gray}🔍{/} frier{cyan}▌{/}");
        Terminal2.AddSeparator(45);
        Terminal2.AddSelectionItem("Frieren: Beyond Journey's End", true);
        Terminal2.AddSelectionItem("Frieren: Beyond Journey's End (Dub)");
        Terminal2.AddSelectionItem("Frieren Specials");
        Terminal2.AddSeparator(45);
        Terminal2.AddColoredLine("{gray}[↑↓] Navigate  [Enter] Select  [Esc] Cancel{/}");
    }
}
