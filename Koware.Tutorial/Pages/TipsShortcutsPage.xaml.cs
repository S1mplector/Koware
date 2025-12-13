// Author: Ilgaz Mehmetoğlu
// Tips & Shortcuts tutorial page.
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class TipsShortcutsPage : Page
{
    public TipsShortcutsPage()
    {
        InitializeComponent();
        PopulateTerminals();
    }

    private void PopulateTerminals()
    {
        // Terminal 1: Quick resume
        Terminal1.Clear();
        Terminal1.AddColoredLine("{gray}# Resume your last watched anime:{/}");
        Terminal1.AddPrompt("koware continue");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{cyan}Resuming:{/} Frieren: Beyond Journey's End");
        Terminal1.AddColoredLine("{green}▶{/} Episode 13");

        // Terminal 2: Useful commands
        Terminal2.Clear();
        Terminal2.AddColoredLine("{gray}# Browse watch history interactively:{/}");
        Terminal2.AddPrompt("koware history");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{gray}# View downloaded content:{/}");
        Terminal2.AddPrompt("koware offline");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{gray}# Get recommendations based on history:{/}");
        Terminal2.AddPrompt("koware recommend");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{gray}# Check for updates:{/}");
        Terminal2.AddPrompt("koware --update");
    }
}
