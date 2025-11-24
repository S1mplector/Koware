using System;

namespace Koware.Player.Win;

public sealed class PlayerArguments
{
    public PlayerArguments(Uri url, string title, string? referer, string? userAgent, Uri? subtitleUrl, string? subtitleLabel)
    {
        Url = url;
        Title = string.IsNullOrWhiteSpace(title) ? "Koware Player" : title;
        Referer = referer;
        UserAgent = userAgent;
        SubtitleUrl = subtitleUrl;
        SubtitleLabel = subtitleLabel;
    }

    public Uri Url { get; }

    public string Title { get; }

    public string? Referer { get; }

    public string? UserAgent { get; }

    public Uri? SubtitleUrl { get; }

    public string? SubtitleLabel { get; }

    public static bool TryParse(string[] args, out PlayerArguments? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (args.Length == 0)
        {
            error = "Missing stream URL.";
            return false;
        }

        if (!Uri.TryCreate(args[0], UriKind.Absolute, out var url))
        {
            error = "The first argument must be a valid absolute URL.";
            return false;
        }

        var title = args.Length > 1 ? args[1] : "Koware Player";
        string? referer = null;
        string? userAgent = null;
        Uri? subtitleUrl = null;
        string? subtitleLabel = null;

        for (var i = 2; i < args.Length; i++)
        {
            var current = args[i];
            if (current.Equals("--referer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                referer = args[++i];
                continue;
            }

            if (current.Equals("--user-agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                userAgent = args[++i];
                continue;
            }

            if (current.Equals("--subtitle", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Uri.TryCreate(args[++i], UriKind.Absolute, out var subUri))
                {
                    subtitleUrl = subUri;
                }
                continue;
            }

            if (current.Equals("--subtitle-label", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                subtitleLabel = args[++i];
                continue;
            }

            if (current.Equals("--ua", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                userAgent = args[++i];
                continue;
            }

            error = $"Unrecognized argument '{current}'.";
            return false;
        }

        parsed = new PlayerArguments(url, title, referer, userAgent, subtitleUrl, subtitleLabel);
        return true;
    }
}
