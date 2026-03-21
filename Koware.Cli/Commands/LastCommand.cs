// Author: Ilgaz Mehmetoğlu
using System.Globalization;
using System.Text.Json;
using Koware.Cli.Configuration;
using Koware.Cli.History;
using Microsoft.Extensions.Logging;

namespace Koware.Cli.Commands;

/// <summary>
/// Implements the 'koware last' command: show or replay the most recent history entry.
/// </summary>
public sealed class LastCommand : ICliCommand
{
    public string Name => "last";
    public string Description => "Show or replay the most recent watch/read history entry";

    public async Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        var json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
        var mode = context.Defaults.GetMode();

        if (mode == CliMode.Manga)
        {
            return await HandleMangaModeAsync(args, context, json);
        }

        return await HandleAnimeModeAsync(args, context, json);
    }

    private static async Task<int> HandleMangaModeAsync(string[] args, CommandContext context, bool json)
    {
        var readHistory = context.GetRequiredService<IReadHistoryStore>();
        var entry = await readHistory.GetLastAsync(context.CancellationToken);
        
        if (entry is null)
        {
            context.Logger.LogWarning("No read history found.");
            return 1;
        }

        if (json)
        {
            var jsonText = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
            System.Console.WriteLine(jsonText);
            return 0;
        }

        System.Console.ForegroundColor = ConsoleColor.Magenta;
        System.Console.WriteLine("Last Read");
        System.Console.ResetColor();
        System.Console.WriteLine(new string('─', 40));

        WriteField("Manga", entry.MangaTitle, ConsoleColor.White);

        var chText = entry.ChapterNumber.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(entry.ChapterTitle) && entry.ChapterTitle != $"Chapter {entry.ChapterNumber}")
        {
            chText += $" - {entry.ChapterTitle}";
        }
        WriteField("Chapter", chText, ConsoleColor.Yellow);
        WriteField("Provider", entry.Provider, ConsoleColor.Gray);

        var ago = DateTimeOffset.UtcNow - entry.ReadAt;
        var agoText = FormatTimeAgo(ago, entry.ReadAt);
        WriteField("Read", $"{entry.ReadAt.LocalDateTime:g} ({agoText})", ConsoleColor.DarkGray);

        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine("Tip: Use 'koware continue' to read the next chapter.");
        System.Console.ResetColor();

        return 0;
    }

    private static async Task<int> HandleAnimeModeAsync(string[] args, CommandContext context, bool json)
    {
        var history = context.GetRequiredService<IWatchHistoryStore>();
        var entry = await history.GetLastAsync(context.CancellationToken);
        
        if (entry is null)
        {
            context.Logger.LogWarning("No watch history found.");
            return 1;
        }

        var play = args.Any(a => string.Equals(a, "--play", StringComparison.OrdinalIgnoreCase));

        if (play)
        {
            var launcher = context.GetRequiredService<IKowareSubprocessLauncher>();
            var replayArgs = BuildReplayArgs(entry);
            var exitCode = await launcher.TryRunAsync(replayArgs, context.Logger, context.CancellationToken);
            if (exitCode.HasValue)
            {
                return exitCode.Value;
            }

            context.Logger.LogWarning("Could not relaunch Koware to replay the last watch entry.");
            return 1;
        }

        if (!play)
        {
            if (json)
            {
                var jsonText = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
                System.Console.WriteLine(jsonText);
                return 0;
            }

            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine("Last Watched");
            System.Console.ResetColor();
            System.Console.WriteLine(new string('─', 40));

            WriteField("Anime", entry.AnimeTitle, ConsoleColor.White);

            var epText = entry.EpisodeNumber.ToString();
            if (!string.IsNullOrWhiteSpace(entry.EpisodeTitle))
            {
                epText += $" - {entry.EpisodeTitle}";
            }
            WriteField("Episode", epText, ConsoleColor.Yellow);
            WriteField("Provider", entry.Provider, ConsoleColor.Gray);

            if (!string.IsNullOrWhiteSpace(entry.Quality))
            {
                WriteField("Quality", entry.Quality, ConsoleColor.Gray);
            }

            var ago = DateTimeOffset.UtcNow - entry.WatchedAt;
            var agoText = FormatTimeAgo(ago, entry.WatchedAt);
            WriteField("Watched", $"{entry.WatchedAt.LocalDateTime:g} ({agoText})", ConsoleColor.DarkGray);

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("Tip: Use 'koware last --play' to replay, or 'koware continue' for next episode.");
            System.Console.ResetColor();
        }

        return 0;
    }

    private static IReadOnlyList<string> BuildReplayArgs(WatchHistoryEntry entry)
    {
        var args = new List<string>
        {
            "watch",
            entry.AnimeTitle,
            "--episode",
            entry.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
            "--non-interactive"
        };

        if (!string.IsNullOrWhiteSpace(entry.Quality))
        {
            args.Add("--quality");
            args.Add(entry.Quality);
        }

        return args;
    }

    private static void WriteField(string label, string value, ConsoleColor valueColor)
    {
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.Write($"  {label,-10} ");
        System.Console.ForegroundColor = valueColor;
        System.Console.WriteLine(value);
        System.Console.ResetColor();
    }

    private static string FormatTimeAgo(TimeSpan ago, DateTimeOffset timestamp)
    {
        return ago.TotalMinutes < 1 ? "just now" :
               ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago" :
               ago.TotalHours < 24 ? $"{(int)ago.TotalHours}h ago" :
               ago.TotalDays < 7 ? $"{(int)ago.TotalDays}d ago" :
               timestamp.LocalDateTime.ToString("MMM dd, yyyy");
    }
}
