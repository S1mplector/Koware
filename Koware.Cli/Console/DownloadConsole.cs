using System;
using System.IO;
using Koware.Domain.Models;

namespace Koware.Cli.Console;

internal static class DownloadConsole
{
    public static void PrintEpisodeHeader(string title, Episode episode, string? quality, int index, int total, string outputPath)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"Downloading {title} - Ep {episode.Number} [{quality ?? "auto"}] ({index}/{total})");
        System.Console.ResetColor();
        System.Console.WriteLine($"  -> {outputPath}");
    }

    public static void PrintEpisodeResult(string outputPath, int episodesLeft)
    {
        double sizeMb = 0;

        try
        {
            if (File.Exists(outputPath))
            {
                var length = new FileInfo(outputPath).Length;
                sizeMb = length / (1024.0 * 1024.0);
            }
        }
        catch
        {
        }

        if (sizeMb > 0)
        {
            System.Console.WriteLine($"  Downloaded size: {sizeMb:0.0} MiB");
        }
        else
        {
            System.Console.WriteLine("  Download completed.");
        }

        if (episodesLeft > 0)
        {
            System.Console.WriteLine($"  Episodes left to download: {episodesLeft}");
        }
    }
}
