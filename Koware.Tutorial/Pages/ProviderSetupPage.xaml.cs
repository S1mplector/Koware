// Author: Ilgaz Mehmetoğlu
// Provider Setup tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ProviderSetupPage : Page
{
    public ProviderSetupPage()
    {
        InitializeComponent();
        Terminal.Loaded += async (s, e) => await AnimateTerminal1Async();
        Terminal2.Loaded += async (s, e) => await AnimateTerminal2Async();
    }

    private async Task AnimateTerminal1Async()
    {
        try
        {
            Terminal.Clear();
            
            // Type autoconfig command
            await Terminal.TypePromptAsync("koware provider autoconfig https://example-site.com");
            
            Terminal.AddEmptyLine();
            await Terminal.AddColoredLineAsync("{cyan}Analyzing site...{/}", 300);
            await Terminal.AddColoredLineAsync("{green}✓{/} Detected site type: anime", 150);
            await Terminal.AddColoredLineAsync("{green}✓{/} Found search endpoint", 150);
            await Terminal.AddColoredLineAsync("{green}✓{/} Found episode selectors", 150);
            await Terminal.AddColoredLineAsync("{green}✓{/} Found video source pattern", 150);
            Terminal.AddEmptyLine();
            await Terminal.AddColoredLineAsync("{green}✓{/} Provider added: example-site", 100);
            await Terminal.AddColoredLineAsync("{gray}Config saved to appsettings.user.json{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware config --open");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{green}✓{/} Opening config in default editor...", 0);
        }
        catch (TaskCanceledException) { }
    }
}
