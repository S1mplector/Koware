// Author: Ilgaz Mehmetoğlu
// Coordinates catalog search, episode/stream selection, and quality preferences.
using System.Diagnostics.CodeAnalysis;
using Koware.Application.Abstractions;
using Koware.Application.Models;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Application.UseCases;

public sealed class ScrapeOrchestrator
{
    private readonly IAnimeCatalog _catalog;
    private readonly ILogger<ScrapeOrchestrator> _logger;

    public ScrapeOrchestrator(IAnimeCatalog catalog, ILogger<ScrapeOrchestrator> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required", nameof(query));
        }

        var trimmed = query.Trim();
        var normalizedQuery = Normalize(trimmed);

        var matches = await _catalog.SearchAsync(trimmed, cancellationToken);

        if (matches.Count == 0 || string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return matches;
        }

        var ordered = matches
            .OrderByDescending(anime => ScoreMatch(normalizedQuery, anime.Title))
            .ThenBy(anime => anime.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ordered;
    }

    public Task<ScrapeResult> ExecuteAsync(ScrapePlan plan, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(plan, selection: matches => ChooseMatch(matches, plan.PreferredMatchIndex), cancellationToken);
    }

    public async Task<ScrapeResult> ExecuteAsync(
        ScrapePlan plan,
        Func<IReadOnlyCollection<Anime>, Anime?>? selection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var matches = await SearchAsync(plan.Query, cancellationToken);
        var selectedAnime = selection?.Invoke(matches) ?? ChooseMatch(matches, preferredIndex: null);

        IReadOnlyCollection<Episode>? episodes = null;
        Episode? selectedEpisode = null;
        IReadOnlyCollection<StreamLink>? streams = null;

        if (selectedAnime is not null)
        {
            episodes = await _catalog.GetEpisodesAsync(selectedAnime, cancellationToken);

            selectedEpisode = TryPickEpisode(episodes, plan.EpisodeNumber);

            if (selectedEpisode is not null)
            {
                streams = await _catalog.GetStreamsAsync(selectedEpisode, cancellationToken);
                streams = ApplyQualityPreference(streams, plan.PreferredQuality);
            }
        }
        else
        {
            _logger.LogInformation("No anime matched query {Query}", plan.Query);
        }

        return new ScrapeResult(matches, selectedAnime, episodes, selectedEpisode, streams);
    }

    private Anime? ChooseMatch(IReadOnlyCollection<Anime> matches, int? preferredIndex)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var index = preferredIndex ?? 1;
        if (index < 1)
        {
            _logger.LogWarning("Preferred match index {Index} is invalid; defaulting to the first match.", index);
            index = 1;
        }
        else if (index > matches.Count)
        {
            _logger.LogWarning("Preferred match index {Index} exceeds available matches ({Count}); using the last match.", index, matches.Count);
            index = matches.Count;
        }

        return matches.ElementAt(index - 1);
    }

    private Episode? TryPickEpisode(IReadOnlyCollection<Episode>? episodes, int? requestedNumber)
    {
        if (episodes is null || episodes.Count == 0)
        {
            return null;
        }

        if (requestedNumber is null)
        {
            return episodes.FirstOrDefault();
        }

        var exact = episodes.FirstOrDefault(ep => ep.Number == requestedNumber);
        if (exact is not null)
        {
            return exact;
        }

        var maxEpisode = episodes.Max(ep => ep.Number);
        if (requestedNumber > maxEpisode)
        {
            _logger.LogWarning("Requested episode {Requested} exceeds available episodes ({Max}). Using latest episode.", requestedNumber, maxEpisode);
            return episodes.OrderByDescending(ep => ep.Number).FirstOrDefault();
        }

        _logger.LogWarning("Requested episode {Requested} not found. Using closest available episode instead.", requestedNumber);

        return episodes
            .OrderBy(ep => Math.Abs(ep.Number - requestedNumber.Value))
            .FirstOrDefault();
    }

    private IReadOnlyCollection<StreamLink>? ApplyQualityPreference(IReadOnlyCollection<StreamLink>? streams, string? preferredQuality)
    {
        if (streams is null || streams.Count == 0 || string.IsNullOrWhiteSpace(preferredQuality))
        {
            return streams;
        }

        var allStreams = streams.ToList();

        var preferred = allStreams
            .Where(link => string.Equals(link.Quality, preferredQuality, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (preferred.Length == 0)
        {
            var available = string.Join(", ", allStreams.Select(s => s.Quality).Where(q => !string.IsNullOrWhiteSpace(q)).Distinct());
            _logger.LogWarning("Requested quality {Quality} not found. Using available streams. Available: {Available}", preferredQuality, available);

            return allStreams
                .OrderByDescending(s => TryParseQualityNumber(s.Quality))
                .ThenByDescending(s => s.HostPriority)
                .ToArray();
        }

        return preferred
            .Concat(allStreams.Where(s => !preferred.Contains(s)))
            .ToArray();
    }

    private static int TryParseQualityNumber(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return 0;
        }

        var digits = new string(quality.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var value))
        {
            return value;
        }

        return 0;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = new string(
            value
                .ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ')
                .ToArray());

        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int ScoreMatch(string normalizedQuery, string? title)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var t = Normalize(title);
        if (t.Length == 0)
        {
            return 0;
        }

        var q = normalizedQuery;
        var score = 0;

        if (t.Equals(q, StringComparison.Ordinal))
        {
            score += 1000;
        }

        if (t.StartsWith(q, StringComparison.Ordinal))
        {
            score += 500;
        }

        if (t.Contains(q, StringComparison.Ordinal))
        {
            score += 300;
        }

        var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tTokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in qTokens)
        {
            if (tTokens.Contains(token))
            {
                score += 80;
            }
            else if (tTokens.Any(tt => tt.StartsWith(token, StringComparison.Ordinal)))
            {
                score += 40;
            }
            else if (t.Contains(token, StringComparison.Ordinal))
            {
                score += 20;
            }
        }

        var lengthDiff = Math.Abs(t.Length - q.Length);
        score -= lengthDiff / 2;

        return score < 0 ? 0 : score;
    }
}
