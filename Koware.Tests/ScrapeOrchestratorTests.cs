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

#nullable enable

public class ScrapeOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_ClampsPreferredIndexToAvailableMatches()
    {
        var catalog = new FakeCatalog(
            new[]
            {
                new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()),
                new Anime(new AnimeId("two"), "Two", null, new System.Uri("https://example.com/two"), Array.Empty<Episode>())
            },
            defaultStreams: new[]
            {
                new StreamLink(new System.Uri("https://media.example.com/stream.m3u8"), "1080p", "demo", null)
            });

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", PreferredMatchIndex: 5);

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal("Two", result.SelectedAnime?.Title);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToFirstMatchWhenIndexTooLow()
    {
        var catalog = new FakeCatalog(
            new[]
            {
                new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()),
                new Anime(new AnimeId("two"), "Two", null, new System.Uri("https://example.com/two"), Array.Empty<Episode>())
            },
            defaultStreams: new[]
            {
                new StreamLink(new System.Uri("https://media.example.com/stream.m3u8"), "1080p", "demo", null)
            });

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", PreferredMatchIndex: 0);

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal("One", result.SelectedAnime?.Title);
    }

    [Fact]
    public async Task ExecuteAsync_ReordersPreferredQualityButKeepsAllStreams()
    {
        var streams = new[]
        {
            new StreamLink(new System.Uri("https://media.example.com/high.m3u8"), "1080p", "demo", null),
            new StreamLink(new System.Uri("https://media.example.com/med.m3u8"), "720p", "demo", null)
        };

        var catalog = new FakeCatalog(
            new[] { new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()) },
            defaultStreams: streams);

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", PreferredQuality: "720p");

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.NotNull(result.Streams);
        Assert.Equal(2, result.Streams!.Count);
        Assert.Equal("720p", result.Streams!.First().Quality);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackWhenQualityMissing()
    {
        var streams = new[]
        {
            new StreamLink(new System.Uri("https://media.example.com/high.m3u8"), "1080p", "demo", null),
            new StreamLink(new System.Uri("https://media.example.com/med.m3u8"), "720p", "demo", null)
        };

        var catalog = new FakeCatalog(
            new[] { new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()) },
            defaultStreams: streams);

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", PreferredQuality: "144p");

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.NotNull(result.Streams);
        Assert.Equal(2, result.Streams!.Count);
        Assert.Equal("1080p", result.Streams!.First().Quality);
    }

    [Fact]
    public async Task ExecuteAsync_ClampsEpisodeWhenOutOfRange()
    {
        var catalog = new FakeCatalog(
            new[] { new Anime(new AnimeId("one"), "One", null, new System.Uri("https://example.com/one"), Array.Empty<Episode>()) },
            defaultStreams: new[]
            {
                new StreamLink(new System.Uri("https://media.example.com/stream.m3u8"), "1080p", "demo", null)
            });

        var orchestrator = new ScrapeOrchestrator(catalog, NullLogger<ScrapeOrchestrator>.Instance);
        var plan = new ScrapePlan("ignored", EpisodeNumber: 99);

        var result = await orchestrator.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(1, result.SelectedEpisode?.Number);
    }

    private sealed class FakeCatalog : IAnimeCatalog
    {
        private readonly IReadOnlyCollection<Anime> _matches;
        private readonly IReadOnlyCollection<StreamLink> _streams;

        public FakeCatalog(IReadOnlyCollection<Anime> matches, IReadOnlyCollection<StreamLink>? defaultStreams = null)
        {
            _matches = matches;
            _streams = defaultStreams ?? new[]
            {
                new StreamLink(new System.Uri("https://media.example.com/stream.m3u8"), "1080p", "demo", null)
            };
        }

        public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(_matches);

        public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Episode>>(new[]
            {
                new Episode(new EpisodeId($"{anime.Id.Value}:ep-1"), "Episode 1", 1, new System.Uri("https://example.com/ep1"))
            });

        public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default) =>
            Task.FromResult(_streams);
    }
}
