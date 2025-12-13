// Author: Ilgaz Mehmetoğlu
// Getting Started tutorial page.
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class GettingStartedPage : Page
{
    public GettingStartedPage()
    {
        InitializeComponent();
        PopulateTerminal();
    }

    private void PopulateTerminal()
    {
        Terminal.Clear();
        Terminal.AddPrompt("koware help");
        Terminal.AddEmptyLine();
        Terminal.AddHeader("❯ Koware - Anime & Manga CLI");
        Terminal.AddSeparator(40);
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{cyan}Usage:{/} koware <command> [options]");
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{cyan}Commands:{/}");
        Terminal.AddColoredLine("  {green}watch{/}      Search and stream anime");
        Terminal.AddColoredLine("  {green}read{/}       Search and read manga");
        Terminal.AddColoredLine("  {green}list{/}       Manage your watchlist");
        Terminal.AddColoredLine("  {green}history{/}    Browse watch/read history");
        Terminal.AddColoredLine("  {green}continue{/}   Resume last watched");
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{gray}Run 'koware help <command>' for details{/}");
    }
}
