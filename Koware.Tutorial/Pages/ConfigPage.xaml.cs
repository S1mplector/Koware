// Author: Ilgaz Mehmetoğlu
// Configuration tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class ConfigPage : Page
{
    public ConfigPage()
    {
        InitializeComponent();
        Terminal1.Loaded += async (s, e) => await AnimateTerminal1Async();
        Terminal2.Loaded += async (s, e) => await AnimateTerminal2Async();
        Terminal3.Loaded += async (s, e) => await AnimateTerminal3Async();
    }

    private async Task AnimateTerminal1Async()
    {
        try
        {
            Terminal1.Clear();
            await Terminal1.TypePromptAsync("koware config get player.command");
            await Terminal1.AddColoredLineAsync("{cyan}player.command:{/} mpv", 100);
            Terminal1.AddEmptyLine();
            await Terminal1.TypePromptAsync("koware config list");
            await Terminal1.AddColoredLineAsync("{gray}defaults.mode = anime{/}", 50);
            await Terminal1.AddColoredLineAsync("{gray}defaults.quality = 1080p{/}", 50);
            await Terminal1.AddColoredLineAsync("{gray}player.command = mpv{/}", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware config set defaults.quality 720p");
            await Terminal2.AddColoredLineAsync("{green}✓{/} Set defaults.quality = 720p", 100);
            Terminal2.AddEmptyLine();
            await Terminal2.TypePromptAsync("koware config set downloads.path ~/Videos/Anime");
            await Terminal2.AddColoredLineAsync("{green}✓{/} Set downloads.path = ~/Videos/Anime", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal3Async()
    {
        try
        {
            Terminal3.Clear();
            await Terminal3.TypePromptAsync("koware config set player.command vlc");
            await Terminal3.AddColoredLineAsync("{green}✓{/} Set player.command = vlc", 100);
            Terminal3.AddEmptyLine();
            await Terminal3.AddColoredLineAsync("{gray}# VLC will now be used for playback{/}", 0);
        }
        catch (TaskCanceledException) { }
    }
}
