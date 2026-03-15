// Author: Ilgaz Mehmetoğlu
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Application.UseCases;

/// <summary>
/// Utility logic for planning download ranges and generating filenames.
/// </summary>
public static class DownloadPlanner
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly HashSet<char> InvalidFileNameCharSet = new(InvalidFileNameChars);

    /// <summary>
    /// Resolve which episodes to download based on user input.
    /// </summary>
    /// <param name="episodesArg">Episode selection string: "all", "N", or "N-M".</param>
    /// <param name="singleEpisodeNumber">Single episode number from --episode flag.</param>
    /// <param name="episodes">Available episodes from the anime.</param>
    /// <param name="logger">Optional logger for warnings.</param>
    /// <returns>List of episodes to download, sorted by number.</returns>
    /// <remarks>
    /// Priority: episodesArg > singleEpisodeNumber > first episode.
    /// "all" returns all episodes; "N-M" returns a range.
    /// </remarks>
    public static IReadOnlyList<Episode> ResolveEpisodeSelection(
        string? episodesArg,
        int? singleEpisodeNumber,
        IReadOnlyList<Episode> episodes,
        ILogger? logger = null)
    {
        if (episodes.Count == 0)
        {
            return Array.Empty<Episode>();
        }

        if (string.IsNullOrWhiteSpace(episodesArg))
        {
            if (singleEpisodeNumber.HasValue)
            {
                var match = episodes.FirstOrDefault(e => e.Number == singleEpisodeNumber.Value);
                if (match is not null)
                {
                    return new[] { match };
                }

                logger?.LogWarning("Requested episode {Episode} not found. No episodes will be downloaded.", singleEpisodeNumber);
                return Array.Empty<Episode>();
            }

            var first = episodes.OrderBy(e => e.Number).First();
            return new[] { first };
        }

        episodesArg = episodesArg.Trim();

        if (episodesArg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return episodes.OrderBy(e => e.Number).ToArray();
        }

        int? from = null;
        int? to = null;

        var dashIndex = episodesArg.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            var startPart = episodesArg[..dashIndex];
            var endPart = episodesArg[(dashIndex + 1)..];

            if (!int.TryParse(startPart, out var start) || start <= 0)
            {
                logger?.LogWarning("Invalid --episodes value '{Value}'. Expected formats: N, N-M, or all.", episodesArg);
                return Array.Empty<Episode>();
            }

            int endValue = start;
            if (!string.IsNullOrWhiteSpace(endPart))
            {
                if (!int.TryParse(endPart, out endValue) || endValue <= 0)
                {
                    logger?.LogWarning("Invalid --episodes value '{Value}'. Expected formats: N, N-M, or all.", episodesArg);
                    return Array.Empty<Episode>();
                }
            }

            from = start;
            to = endValue;
        }
        else
        {
            if (!int.TryParse(episodesArg, out var single) || single <= 0)
            {
                logger?.LogWarning("Invalid --episodes value '{Value}'. Expected formats: N, N-M, or all.", episodesArg);
                return Array.Empty<Episode>();
            }

            from = to = single;
        }

        if (from.HasValue && to.HasValue && from > to)
        {
            (from, to) = (to, from);
        }

        var selected = episodes
            .Where(e => (!from.HasValue || e.Number >= from) && (!to.HasValue || e.Number <= to))
            .OrderBy(e => e.Number)
            .ToArray();

        if (selected.Length == 0)
        {
            logger?.LogWarning("No episodes fall within the requested range {From}-{To}.", from, to);
        }

        return selected;
    }

    /// <summary>
    /// Resolve which manga chapters to download based on user input.
    /// </summary>
    /// <param name="chaptersArg">Chapter selection string: "all", "N", "N-M", or comma-separated segments.</param>
    /// <param name="singleChapterNumber">Single chapter number from --chapter.</param>
    /// <param name="chapters">Available chapters for the manga.</param>
    /// <param name="logger">Optional logger for warnings.</param>
    /// <returns>List of chapters to download, sorted by number.</returns>
    public static IReadOnlyList<Chapter> ResolveChapterSelection(
        string? chaptersArg,
        float? singleChapterNumber,
        IReadOnlyList<Chapter> chapters,
        ILogger? logger = null)
    {
        if (chapters.Count == 0)
        {
            return Array.Empty<Chapter>();
        }

        if (string.IsNullOrWhiteSpace(chaptersArg))
        {
            if (singleChapterNumber.HasValue)
            {
                var match = chapters.FirstOrDefault(c => Math.Abs(c.Number - singleChapterNumber.Value) < 0.001f)
                    ?? chapters.FirstOrDefault(c => (int)c.Number == (int)singleChapterNumber.Value);
                if (match is not null)
                {
                    return new[] { match };
                }

                logger?.LogWarning("Requested chapter {Chapter} not found. No chapters will be downloaded.", singleChapterNumber);
                return Array.Empty<Chapter>();
            }

            var first = chapters.OrderBy(c => c.Number).First();
            return new[] { first };
        }

        chaptersArg = chaptersArg.Trim();
        if (chaptersArg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return chapters.OrderBy(c => c.Number).ToArray();
        }

        var segments = chaptersArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            logger?.LogWarning("Invalid --chapters value '{Value}'. Expected formats: N, N-M, N,N2, or all.", chaptersArg);
            return Array.Empty<Chapter>();
        }

        var parsedSegments = new List<(float from, float to)>(segments.Length);
        foreach (var segment in segments)
        {
            if (!TryParseChapterSegment(segment, out var from, out var to))
            {
                logger?.LogWarning("Invalid --chapters segment '{Segment}'. Expected formats: N, N-M, or all.", segment);
                return Array.Empty<Chapter>();
            }

            if (from > to)
            {
                (from, to) = (to, from);
            }

            parsedSegments.Add((from, to));
        }

        var selected = chapters
            .Where(chapter => parsedSegments.Any(segment => chapter.Number >= segment.from - 0.001f && chapter.Number <= segment.to + 0.001f))
            .OrderBy(chapter => chapter.Number)
            .ToArray();

        if (selected.Length == 0)
        {
            logger?.LogWarning("No chapters match the requested selection '{Selection}'.", chaptersArg);
        }

        return selected;
    }

    /// <summary>
    /// Build a safe filename for a downloaded episode.
    /// </summary>
    /// <param name="animeTitle">Anime title (sanitized for filesystem).</param>
    /// <param name="episode">Episode to download.</param>
    /// <param name="quality">Optional quality label to include in filename.</param>
    /// <returns>Filename like "Anime - Ep 001 - Title [1080p].mp4".</returns>
    public static string BuildDownloadFileName(string animeTitle, Episode episode, string? quality)
    {
        var titleSegment = SanitizeFileNameSegment(animeTitle);
        var episodeTitle = string.IsNullOrWhiteSpace(episode.Title)
            ? $"Ep {episode.Number:D3}"
            : $"Ep {episode.Number:D3} - {episode.Title}";
        episodeTitle = SanitizeFileNameSegment(episodeTitle);

        var qualitySegment = string.IsNullOrWhiteSpace(quality)
            ? string.Empty
            : $" [{SanitizeFileNameSegment(quality)}]";

        var name = $"{titleSegment} - {episodeTitle}{qualitySegment}.mp4";
        return name;
    }

    /// <summary>Remove invalid filename characters from a string segment.</summary>
    private static string SanitizeFileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "untitled";
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "untitled";
        }

        var firstInvalidIndex = trimmed.AsSpan().IndexOfAny(InvalidFileNameChars);
        if (firstInvalidIndex < 0)
        {
            return trimmed;
        }

        var chars = trimmed.ToCharArray();
        for (var i = firstInvalidIndex; i < chars.Length; i++)
        {
            if (InvalidFileNameCharSet.Contains(chars[i]))
            {
                chars[i] = '_';
            }
        }

        var cleaned = new string(chars);
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }

    private static bool TryParseChapterSegment(string segment, out float from, out float to)
    {
        from = 0;
        to = 0;

        var dashIndex = segment.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex < 0)
        {
            if (!float.TryParse(segment, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var single) || single <= 0)
            {
                return false;
            }

            from = to = single;
            return true;
        }

        var startPart = segment[..dashIndex];
        var endPart = segment[(dashIndex + 1)..];

        if (!float.TryParse(startPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out from) || from <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(endPart))
        {
            to = from;
            return true;
        }

        if (!float.TryParse(endPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out to) || to <= 0)
        {
            return false;
        }

        return true;
    }
}
