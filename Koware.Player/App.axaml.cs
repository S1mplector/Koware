// Author: Ilgaz Mehmetoğlu
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Koware.WatchTogether;
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
            string? watchRelay = null;
            string? watchRoom = null;
            string? watchClientId = null;
            string? watchName = null;
            string? watchRole = null;

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
                else if (arg.Equals("--watch-relay", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    watchRelay = args[++i];
                }
                else if (arg.Equals("--watch-room", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    watchRoom = args[++i];
                }
                else if (arg.Equals("--watch-client-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    watchClientId = args[++i];
                }
                else if (arg.Equals("--watch-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    watchName = args[++i];
                }
                else if (arg.Equals("--watch-role", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    watchRole = args[++i];
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
                SubtitleUrl = subtitleUrl,
                WatchTogetherSession = BuildWatchTogetherSession(watchRelay, watchRoom, watchClientId, watchName, watchRole)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WatchTogetherSessionOptions? BuildWatchTogetherSession(
        string? relay,
        string? room,
        string? clientId,
        string? name,
        string? role)
    {
        if (string.IsNullOrWhiteSpace(relay) || string.IsNullOrWhiteSpace(room))
        {
            return null;
        }

        return new WatchTogetherSessionOptions(
            WatchTogetherClient.NormalizeRelayUri(relay),
            room,
            string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId,
            string.IsNullOrWhiteSpace(name) ? Environment.UserName : name,
            string.IsNullOrWhiteSpace(role) ? WatchTogetherRoles.Guest : role);
    }
}
