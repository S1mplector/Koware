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
        Terminal.AddHeader("> Help [16/16] ^v0%");
        Terminal.AddColoredLine("  {gray}[?]{/} {cyan}▌{/}");
        Terminal.AddSeparator(55);
        Terminal.AddColoredLine(" {cyan}>{/} {green}[1]{/} search");
        Terminal.AddColoredLine("   {green}[2]{/} recommend");
        Terminal.AddColoredLine("   {green}[3]{/} stream");
        Terminal.AddColoredLine("   {green}[4]{/} watch");
        Terminal.AddColoredLine("   {green}[5]{/} download");
        Terminal.AddColoredLine("   {green}[6]{/} read");
        Terminal.AddColoredLine("   {green}[7]{/} last");
        Terminal.AddColoredLine("   {green}[8]{/} continue");
        Terminal.AddColoredLine("   {green}[9]{/} history");
        Terminal.AddSeparator(55);
        Terminal.AddColoredLine("  {gray}[#] Find anime or manga with optional filters{/}");
    }
}
