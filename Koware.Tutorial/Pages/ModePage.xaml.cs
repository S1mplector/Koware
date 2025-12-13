// Author: Ilgaz Mehmetoğlu
// Mode switching tutorial page.
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ModePage : Page
{
    public ModePage()
    {
        InitializeComponent();
        PopulateTerminals();
    }

    private void PopulateTerminals()
    {
        // Terminal 1: Check current mode
        Terminal1.Clear();
        Terminal1.AddPrompt("koware mode");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{cyan}Current mode:{/} anime");
        Terminal1.AddColoredLine("{gray}Use 'koware mode manga' to switch{/}");

        // Terminal 2: Temporary switch with -m flag
        Terminal2.Clear();
        Terminal2.AddPrompt("koware -m read \"one punch man\"");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{cyan}[Manga Mode]{/} Searching...");
        Terminal2.AddColoredLine("{green}✓{/} Found: One Punch Man");

        // Terminal 3: Permanent switch
        Terminal3.Clear();
        Terminal3.AddPrompt("koware mode manga");
        Terminal3.AddEmptyLine();
        Terminal3.AddColoredLine("{green}✓{/} Default mode set to: manga");
        Terminal3.AddEmptyLine();
        Terminal3.AddColoredLine("{gray}# Now all commands default to manga mode:{/}");
        Terminal3.AddPrompt("koware read \"solo leveling\"");
        Terminal3.AddColoredLine("{cyan}Searching...{/}");
        Terminal3.AddColoredLine("{green}✓{/} Found: Solo Leveling");
    }
}
