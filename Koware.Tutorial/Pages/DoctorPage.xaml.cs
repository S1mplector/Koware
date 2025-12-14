// Author: Ilgaz Mehmetoğlu
// Doctor & Troubleshooting tutorial page.
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Koware.Tutorial.Pages;

public partial class DoctorPage : Page
{
    public DoctorPage()
    {
        InitializeComponent();
        Terminal1.Loaded += async (s, e) => await AnimateTerminal1Async();
        Terminal2.Loaded += async (s, e) => await AnimateTerminal2Async();
    }

    private async Task AnimateTerminal1Async()
    {
        try
        {
            Terminal1.Clear();
            await Terminal1.TypePromptAsync("koware doctor");
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{white}┌─ Environment ─────────────────────────────────────────────────{/}", 50);
            await Terminal1.AddColoredLineAsync("{green}  ✓{/} Operating System: macOS 14.0", 30);
            await Terminal1.AddColoredLineAsync("{green}  ✓{/} .NET Runtime: 8.0.0", 30);
            await Terminal1.AddColoredLineAsync("{green}  ✓{/} Disk Space: 45.2 GB free", 30);
            Terminal1.AddEmptyLine();
            await Terminal1.AddColoredLineAsync("{white}┌─ Toolchain ────────────────────────────────────────────────────{/}", 50);
            await Terminal1.AddColoredLineAsync("{green}  ✓{/} ffmpeg: 6.1 (installed)", 30);
            await Terminal1.AddColoredLineAsync("{green}  ✓{/} yt-dlp: 2024.01.01 (installed)", 30);
            await Terminal1.AddColoredLineAsync("{yellow}  ⚠{/} aria2c: Not found (optional)", 0);
        }
        catch (TaskCanceledException) { }
    }

    private async Task AnimateTerminal2Async()
    {
        try
        {
            Terminal2.Clear();
            await Terminal2.TypePromptAsync("koware doctor -c network");
            Terminal2.AddEmptyLine();
            await Terminal2.AddColoredLineAsync("{white}┌─ Network ──────────────────────────────────────────────────────{/}", 50);
            await Terminal2.AddColoredLineAsync("{green}  ✓{/} Internet: Connected (23ms)", 80);
            await Terminal2.AddColoredLineAsync("{green}  ✓{/} DNS Resolution: OK", 80);
            await Terminal2.AddColoredLineAsync("{green}  ✓{/} HTTPS: TLS 1.3 supported", 0);
        }
        catch (TaskCanceledException) { }
    }
}
