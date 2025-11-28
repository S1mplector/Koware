using System;
using System.IO;
using Koware.Domain.Models;

namespace Koware.Cli.Console;

/// <summary>
/// Console output helpers for the download command.
/// </summary>
internal static class DownloadConsole
{
    /// <summary>
    /// Print a header line before downloading an episode.
    /// </summary>
    /// <param name="title">Anime title.</param>
    /// <param name="episode">Episode being downloaded.</param>
    /// <param name="quality">Quality label.</param>
    /// <param name="index">Current episode index (1-based).</param>
    /// <param name="total">Total episodes to download.</param>
    /// <param name="outputPath">Destination file path.</param>
    public static void PrintEpisodeHeader(string title, Episode episode, string? quality, int index, int total, string outputPath)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"Downloading {title} - Ep {episode.Number} [{quality ?? "auto"}] ({index}/{total})");
        System.Console.ResetColor();
        System.Console.WriteLine($"  -> {outputPath}");
    }

    /// <summary>
    /// Print result after downloading an episode (file size and remaining count).
    /// </summary>
    /// <param name="outputPath">Downloaded file path.</param>
    /// <param name="episodesLeft">Number of episodes remaining.</param>
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
