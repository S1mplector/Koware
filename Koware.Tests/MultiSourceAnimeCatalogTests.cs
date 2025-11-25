// Author: Ilgaz Mehmetoglu
// Tests for MultiSourceAnimeCatalog fallback and provider routing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Koware.Tests;

#nullable enable

public class MultiSourceAnimeCatalogTests
{
    [Fact]
    public async Task Search_UsesPrimaryWhenResultsAvailable()
    {
        var primary = new StubCatalog(searchResults: new[]
        {
            new Anime(new AnimeId("primary"), "Primary", null, new Uri("https://example.com/primary"), Array.Empty<Episode>())
        });
        var secondary = new StubCatalog(searchResults: new[]
        {
            new Anime(new AnimeId("secondary"), "Secondary", null, new Uri("https://example.com/secondary"), Array.Empty<Episode>())
        });

        var catalog = CreateCatalog(primary, secondary);

        var result = await catalog.SearchAsync("q");

        Assert.Single(result);
        Assert.Equal("primary", result.First().Id.Value);
    }

    [Fact]
    public async Task Search_FallsBackToSecondaryWhenPrimaryFails()
    {
        var primary = new StubCatalog(searchResults: Array.Empty<Anime>(), throwOnSearch: true);
        var secondary = new StubCatalog(searchResults: new[]
        {
            new Anime(new AnimeId("secondary"), "Secondary", null, new Uri("https://example.com/secondary"), Array.Empty<Episode>())
        });

        var catalog = CreateCatalog(primary, secondary);

        var result = await catalog.SearchAsync("q");

        Assert.Single(result);
        Assert.Equal("secondary", result.First().Id.Value);
    }

    [Fact]
    public async Task GetEpisodes_UsesPrimaryOnlyForNonGogoIds()
    {
        var primary = new StubCatalog(
            searchResults: Array.Empty<Anime>(),
            episodeResults: new[] { new Episode(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/ep1")) });
        var secondary = new StubCatalog(searchResults: Array.Empty<Anime>());

        var catalog = CreateCatalog(primary, secondary);
        var episodes = await catalog.GetEpisodesAsync(new Anime(new AnimeId("primary"), "Primary", null, new Uri("https://example.com/primary"), Array.Empty<Episode>()), CancellationToken.None);

        Assert.Single(episodes);
        Assert.Equal(0, secondary.EpisodeCalls);
    }

    [Fact]
    public async Task GetStreams_UsesGogoForGogoIds()
    {
        var primary = new StubCatalog(searchResults: Array.Empty<Anime>());
        var secondary = new StubCatalog(
            searchResults: Array.Empty<Anime>(),
            streamResults: new[] { new StreamLink(new Uri("https://example.com/stream"), "auto", "gogo", null) });

        var catalog = CreateCatalog(primary, secondary);
        var streams = await catalog.GetStreamsAsync(new Episode(new EpisodeId("gogo:ep-1"), "Episode 1", 1, new Uri("https://example.com/ep1")), CancellationToken.None);

        Assert.Single(streams);
        Assert.Equal(1, secondary.StreamCalls);
    }

    private static MultiSourceAnimeCatalog CreateCatalog(StubCatalog primary, StubCatalog secondary) =>
        new(primary, secondary, Options.Create(new ProviderToggleOptions()), NullLogger<MultiSourceAnimeCatalog>.Instance);

    private sealed class StubCatalog : IAnimeCatalog
    {
        public StubCatalog(
            IReadOnlyCollection<Anime> searchResults,
            IReadOnlyCollection<Episode>? episodeResults = null,
            IReadOnlyCollection<StreamLink>? streamResults = null,
            bool throwOnSearch = false)
        {
            SearchResults = searchResults;
            EpisodeResults = episodeResults ?? Array.Empty<Episode>();
            StreamResults = streamResults ?? Array.Empty<StreamLink>();
            ThrowOnSearch = throwOnSearch;
        }

        public bool ThrowOnSearch { get; }
        public int SearchCalls { get; private set; }
        public int EpisodeCalls { get; private set; }
        public int StreamCalls { get; private set; }
        public IReadOnlyCollection<Anime> SearchResults { get; }
        public IReadOnlyCollection<Episode> EpisodeResults { get; }
        public IReadOnlyCollection<StreamLink> StreamResults { get; }

        public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            if (ThrowOnSearch)
            {
                throw new InvalidOperationException("Primary search failed");
            }
            return Task.FromResult(SearchResults);
        }

        public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
        {
            EpisodeCalls++;
            return Task.FromResult(EpisodeResults);
        }

        public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            return Task.FromResult(StreamResults);
        }
    }
}
