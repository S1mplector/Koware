// Author: Ilgaz Mehmetoğlu
// Reading Manga tutorial page.
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ReadingMangaPage : Page
{
    public ReadingMangaPage()
    {
        InitializeComponent();
        PopulateTerminals();
    }

    private void PopulateTerminals()
    {
        // Terminal 1: Manga mode
        Terminal1.Clear();
        Terminal1.AddColoredLine("{gray}# Use -m flag for manga commands:{/}");
        Terminal1.AddPrompt("koware -m read \"one punch man\"");
        Terminal1.AddEmptyLine();
        Terminal1.AddColoredLine("{gray}# Or set default mode to manga:{/}");
        Terminal1.AddPrompt("koware config --mode manga");
        Terminal1.AddColoredLine("{green}✓{/} Default mode set to manga");

        // Terminal 2: Reading
        Terminal2.Clear();
        Terminal2.AddPrompt("koware -m read \"tokyo ghoul\" --chapter 50");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{cyan}Searching...{/}");
        Terminal2.AddColoredLine("{green}✓{/} Found: Tokyo Ghoul");
        Terminal2.AddColoredLine("{cyan}Loading chapter 50...{/}");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{green}✓{/} Opening reader (24 pages)");
        Terminal2.AddColoredLine("{gray}Press Esc to exit reader{/}");
    }
}
