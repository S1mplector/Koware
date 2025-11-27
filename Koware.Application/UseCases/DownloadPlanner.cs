// Author: Ilgaz Mehmetoğlu
// Utility logic for planning download ranges and filenames.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Application.UseCases;

public static class DownloadPlanner
{
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

    private static string SanitizeFileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "untitled";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray());

        cleaned = cleaned.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "untitled" : cleaned;
    }
}
