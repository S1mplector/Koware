// Author: Ilgaz Mehmetoğlu
// Provider Setup tutorial page.
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ProviderSetupPage : Page
{
    public ProviderSetupPage()
    {
        InitializeComponent();
        PopulateTerminal();
    }

    private void PopulateTerminal()
    {
        Terminal.Clear();
        Terminal.AddPrompt("koware config");
        Terminal.AddEmptyLine();
        Terminal.AddHeader("❯ Koware Configuration");
        Terminal.AddSeparator(40);
        Terminal.AddColoredLine("{cyan}Config file:{/} %APPDATA%\\koware\\appsettings.user.json");
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{green}✓{/} Default quality: 1080p");
        Terminal.AddColoredLine("{green}✓{/} Default mode: anime");
        Terminal.AddColoredLine("{yellow}!{/} Providers: 0 configured");
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{gray}Run 'koware config --open' to edit{/}");
    }
}
