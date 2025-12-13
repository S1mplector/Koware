// Author: Ilgaz Mehmetoğlu
// Getting Started tutorial page.
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class GettingStartedPage : Page
{
    public GettingStartedPage()
    {
        InitializeComponent();
        Terminal.AutoAnimate = true;
        Terminal.Loaded += async (s, e) => await AnimateTerminalAsync();
    }

    private async Task AnimateTerminalAsync()
    {
        try
        {
            Terminal.Clear();
            
            // Type the command with animation
            await Terminal.TypePromptAsync("koware help");
            
            // Show output appearing line by line
            Terminal.AddEmptyLine();
            await Terminal.AddColoredLineAsync("{cyan}> Help [16/16] ^v0%{/}", 100);
            await Terminal.AddColoredLineAsync("  {gray}[?]{/} {cyan}▌{/}", 80);
            Terminal.AddSeparator(55);
            await Task.Delay(100);
            
            await Terminal.AddColoredLineAsync(" {cyan}>{/} {green}[1]{/} search", 60);
            await Terminal.AddColoredLineAsync("   {green}[2]{/} recommend", 60);
            await Terminal.AddColoredLineAsync("   {green}[3]{/} stream", 60);
            await Terminal.AddColoredLineAsync("   {green}[4]{/} watch", 60);
            await Terminal.AddColoredLineAsync("   {green}[5]{/} download", 60);
            await Terminal.AddColoredLineAsync("   {green}[6]{/} read", 60);
            await Terminal.AddColoredLineAsync("   {green}[7]{/} last", 60);
            await Terminal.AddColoredLineAsync("   {green}[8]{/} continue", 60);
            await Terminal.AddColoredLineAsync("   {green}[9]{/} history", 60);
            
            Terminal.AddSeparator(55);
            await Terminal.AddColoredLineAsync("  {gray}[#] Find anime or manga with optional filters{/}", 0);
        }
        catch (TaskCanceledException)
        {
            // Page was navigated away, that's fine
        }
    }
}
