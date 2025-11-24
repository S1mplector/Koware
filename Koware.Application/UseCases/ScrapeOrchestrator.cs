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

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required", nameof(query));
        }

        return _catalog.SearchAsync(query.Trim(), cancellationToken);
    }

    public Task<ScrapeResult> ExecuteAsync(ScrapePlan plan, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(plan, selection: null, cancellationToken);
    }

    public async Task<ScrapeResult> ExecuteAsync(
        ScrapePlan plan,
        Func<IReadOnlyCollection<Anime>, Anime?>? selection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var matches = await SearchAsync(plan.Query, cancellationToken);
        var selectedAnime = selection?.Invoke(matches) ?? matches.FirstOrDefault();

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

    private static Episode? TryPickEpisode(IReadOnlyCollection<Episode>? episodes, int? requestedNumber)
    {
        if (episodes is null || episodes.Count == 0)
        {
            return null;
        }

        if (requestedNumber is null)
        {
            return episodes.FirstOrDefault();
        }

        return episodes.FirstOrDefault(ep => ep.Number == requestedNumber);
    }

    private IReadOnlyCollection<StreamLink>? ApplyQualityPreference(IReadOnlyCollection<StreamLink>? streams, string? preferredQuality)
    {
        if (streams is null || streams.Count == 0 || string.IsNullOrWhiteSpace(preferredQuality))
        {
            return streams;
        }

        var preferred = streams.FirstOrDefault(link => string.Equals(link.Quality, preferredQuality, StringComparison.OrdinalIgnoreCase));
        if (preferred is null)
        {
            _logger.LogWarning("Requested quality {Quality} not found. Using available streams.", preferredQuality);
            return streams;
        }

        return new[] { preferred };
    }
}
