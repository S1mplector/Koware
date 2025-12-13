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
        // Terminal 1: Autoconfig command
        Terminal.Clear();
        Terminal.AddPrompt("koware provider autoconfig https://example-site.com");
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{cyan}Analyzing site...{/}");
        Terminal.AddColoredLine("{green}✓{/} Detected site type: anime");
        Terminal.AddColoredLine("{green}✓{/} Found search endpoint");
        Terminal.AddColoredLine("{green}✓{/} Found episode selectors");
        Terminal.AddColoredLine("{green}✓{/} Found video source pattern");
        Terminal.AddEmptyLine();
        Terminal.AddColoredLine("{green}✓{/} Provider added: example-site");
        Terminal.AddColoredLine("{gray}Config saved to appsettings.user.json{/}");

        // Terminal 2: Manual config open
        Terminal2.Clear();
        Terminal2.AddPrompt("koware config --open");
        Terminal2.AddEmptyLine();
        Terminal2.AddColoredLine("{green}✓{/} Opening config in default editor...");
    }
}
