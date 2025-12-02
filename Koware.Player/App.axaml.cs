// Author: Ilgaz Mehmetoğlu
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Linq;

namespace Koware.Player;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            
            // Parse command line arguments
            // Usage: Koware.Player <url> [title] [--referer <url>] [--user-agent <ua>] [--subtitle <url>]
            string? streamUrl = null;
            string? title = "Koware Player";
            string? referer = null;
            string? userAgent = null;
            string? subtitleUrl = null;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                
                if (arg.Equals("--referer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    referer = args[++i];
                }
                else if (arg.Equals("--user-agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    userAgent = args[++i];
                }
                else if (arg.Equals("--subtitle", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    subtitleUrl = args[++i];
                }
                else if (streamUrl is null && arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    streamUrl = arg;
                }
                else if (streamUrl is not null && title == "Koware Player" && !arg.StartsWith("--"))
                {
                    title = arg;
                }
            }

            desktop.MainWindow = new MainWindow
            {
                StreamUrl = streamUrl,
                Title = title,
                HttpReferer = referer,
                HttpUserAgent = userAgent,
                SubtitleUrl = subtitleUrl
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
