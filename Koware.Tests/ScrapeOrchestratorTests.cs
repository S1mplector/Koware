// Author: Ilgaz Mehmetoğlu
// Tests for ScrapeOrchestrator match selection and index clamping behavior.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koware.Application.Abstractions;
using Koware.Application.Models;
using Koware.Application.UseCases;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Koware.Tests;

public class ScrapeOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_ClampsPreferredIndexToAvailableMatches()
    {
        var catalog = new FakeCatalog(new[]
        {
            new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()),
            new Anime(new AnimeId("two"), "Two", null, new System.Uri("https://example.com/two"), Array.Empty<Episode>())
        });

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", PreferredMatchIndex: 5);

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal("Two", result.SelectedAnime?.Title);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToFirstMatchWhenIndexTooLow()
    {
        var catalog = new FakeCatalog(new[]
        {
            new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()),
            new Anime(new AnimeId("two"), "Two", null, new System.Uri("https://example.com/two"), Array.Empty<Episode>())
        });

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", PreferredMatchIndex: 0);

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal("One", result.SelectedAnime?.Title);
    }

    private sealed class FakeCatalog : IAnimeCatalog
    {
        private readonly IReadOnlyCollection<Anime> _matches;

        public FakeCatalog(IReadOnlyCollection<Anime> matches)
        {
            _matches = matches;
        }

        public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(_matches);

        public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Episode>>(new[]
            {
                new Episode(new EpisodeId($"{anime.Id.Value}:ep-1"), "Episode 1", 1, new System.Uri("https://example.com/ep1"))
            });

        public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<StreamLink>>(new[]
            {
                new StreamLink(new System.Uri("https://media.example.com/stream.m3u8"), "1080p", "demo", null)
            });
    }
}
