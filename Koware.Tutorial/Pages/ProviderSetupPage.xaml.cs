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
            
            // Show provider help with autoconfig commands
            await Terminal.TypePromptAsync("koware provider --help");
            Terminal.AddEmptyLine();
            await Terminal.AddColoredLineAsync("{gray}...{/}", 100);
            await Terminal.AddColoredLineAsync("  {green}test [name]{/}           Test provider connectivity (DNS + HTTP)", 80);
            await Terminal.AddColoredLineAsync("  {green}autoconfig [name]{/}     Fetch config from koware-providers repo", 80);
            await Terminal.AddColoredLineAsync("  {green}autoconfig --list{/}     List available remote provider configs", 80);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware config path");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("C:\\Users\\You\\AppData\\Roaming\\koware\\appsettings.user.json", 0);
        }
        catch (TaskCanceledException) { }
    }
}
