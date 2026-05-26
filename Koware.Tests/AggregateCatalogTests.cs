// Author: Ilgaz Mehmetoğlu
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Runtime;
using Koware.Autoconfig.Storage;
using Koware.Domain.Models;
using Koware.Tests.Autoconfig;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Koware.Tests;

#nullable enable

public sealed class AggregateCatalogTests
{
    [Fact]
    public async Task AnimeSearch_MergesPrefixesAndRanksByQueryMatch()
    {
        var first = new StubAnimeCatalog(searchResults:
        [
            Anime("a", "Unrelated Result"),
            Anime("b", "Naruto Shippuden")
        ]);
        var second = new StubAnimeCatalog(searchResults:
        [
            Anime("c", "Naruto")
        ]);

        var catalog = CreateAnimeAggregate(
            new AggregateAnimeCatalog.BuiltInAnimeProvider("first", "First", first),
            new AggregateAnimeCatalog.BuiltInAnimeProvider("second", "Second", second));

        var results = await catalog.SearchAsync("naruto");

        Assert.Collection(results,
            anime =>
            {
                Assert.Equal("second:c", anime.Id.Value);
                Assert.Equal("Naruto", anime.Title);
            },
            anime => Assert.Equal("first:b", anime.Id.Value),
            anime => Assert.Equal("first:a", anime.Id.Value));
    }

    [Fact]
    public async Task AnimeGetStreams_RoutesPrefixedEpisodeToOwningProvider()
    {
        var first = new StubAnimeCatalog(searchResults: []);
        var second = new StubAnimeCatalog(
            searchResults: [],
            streamResults:
            [
                new StreamLink(new Uri("https://cdn.example.com/stream.m3u8"), "1080p", "", null)
            ]);

        var catalog = CreateAnimeAggregate(
            new AggregateAnimeCatalog.BuiltInAnimeProvider("first", "First", first),
            new AggregateAnimeCatalog.BuiltInAnimeProvider("second", "Second", second));

        var streams = await catalog.GetStreamsAsync(Episode("second:ep-1", 1));

        var stream = Assert.Single(streams);
        Assert.Equal("https://cdn.example.com/stream.m3u8", stream.Url.ToString());
        Assert.Equal(0, first.StreamCalls);
        Assert.Equal(1, second.StreamCalls);
        Assert.Equal("second", stream.SourceTag);
    }

    [Fact]
    public async Task AnimeGetEpisodes_UnprefixedLegacyIdTriesProvidersUntilOneReturnsEpisodes()
    {
        var first = new StubAnimeCatalog(searchResults: []);
        var second = new StubAnimeCatalog(
            searchResults: [],
            episodeResults:
            [
                Episode("ep-2", 2)
            ]);

        var catalog = CreateAnimeAggregate(
            new AggregateAnimeCatalog.BuiltInAnimeProvider("first", "First", first),
            new AggregateAnimeCatalog.BuiltInAnimeProvider("second", "Second", second));

        var episodes = await catalog.GetEpisodesAsync(Anime("legacy-id", "Legacy"));

        var episode = Assert.Single(episodes);
        Assert.Equal("second:ep-2", episode.Id.Value);
        Assert.Equal(1, first.EpisodeCalls);
        Assert.Equal(1, second.EpisodeCalls);
    }

    [Fact]
    public async Task AnimeSearch_DoesNotSwallowCancellation()
    {
        var throwing = new StubAnimeCatalog(searchResults: [], throwOperationCanceledOnSearch: true);
        var catalog = CreateAnimeAggregate(new AggregateAnimeCatalog.BuiltInAnimeProvider("cancel", "Cancel", throwing));

        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.SearchAsync("q"));
    }

    [Fact]
    public async Task AnimeSearch_IncludesAllConfiguredDynamicProvidersWithActiveFirst()
    {
        var first = DynamicAnimeConfig("dynamic-one", "Dynamic One", "api.one.example");
        var second = DynamicAnimeConfig("dynamic-two", "Dynamic Two", "api.two.example");
        var store = new ConfigProviderStore([first, second], activeAnime: "dynamic-two");
        var httpHandler = new StubHttpMessageHandler();
        httpHandler.SetResponse(
            uri => uri.Host == "api.one.example",
            () => JsonResponse("""{"data":[{"id":"one","title":"Dynamic Show"}]}"""));
        httpHandler.SetResponse(
            uri => uri.Host == "api.two.example",
            () => JsonResponse("""{"data":[{"id":"two","title":"Dynamic Show"}]}"""));

        var catalog = CreateAnimeAggregate(store, new HttpClient(httpHandler));

        var results = await catalog.SearchAsync("dynamic show");

        Assert.Collection(results,
            anime => Assert.Equal("dynamic-two:two", anime.Id.Value),
            anime => Assert.Equal("dynamic-one:one", anime.Id.Value));
    }

    [Fact]
    public async Task MangaSearch_MergesAndRanksResults()
    {
        var first = new StubMangaCatalog(searchResults:
        [
            Manga("a", "Something Else"),
            Manga("b", "One Piece")
        ]);
        var second = new StubMangaCatalog(searchResults:
        [
            Manga("c", "One Piece Color Walk")
        ]);

        var catalog = CreateMangaAggregate(
            new("first", "First", first),
            new("second", "Second", second));

        var results = await catalog.SearchAsync("one piece");

        Assert.Collection(results,
            manga => Assert.Equal("first:b", manga.Id.Value),
            manga => Assert.Equal("second:c", manga.Id.Value),
            manga => Assert.Equal("first:a", manga.Id.Value));
    }

    private static AggregateAnimeCatalog CreateAnimeAggregate(params AggregateAnimeCatalog.BuiltInAnimeProvider[] providers)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new AggregateAnimeCatalog(
            providers,
            new EmptyProviderStore(),
            new TransformEngine(loggerFactory.CreateLogger<TransformEngine>()),
            new HttpClient(),
            loggerFactory);
    }

    private static AggregateAnimeCatalog CreateAnimeAggregate(IProviderStore providerStore, HttpClient httpClient)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new AggregateAnimeCatalog(
            [],
            providerStore,
            new TransformEngine(loggerFactory.CreateLogger<TransformEngine>()),
            httpClient,
            loggerFactory);
    }

    private static AggregateMangaCatalog CreateMangaAggregate(params AggregateMangaCatalog.BuiltInMangaProvider[] providers)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new AggregateMangaCatalog(
            providers,
            new EmptyProviderStore(),
            new TransformEngine(loggerFactory.CreateLogger<TransformEngine>()),
            new HttpClient(),
            loggerFactory);
    }

    private static Anime Anime(string id, string title) =>
        new(new AnimeId(id), title, null, null, new Uri($"https://example.com/anime/{id}"), []);

    private static Episode Episode(string id, int number) =>
        new(new EpisodeId(id), $"Episode {number}", number, new Uri($"https://example.com/episode/{id}"));

    private static Manga Manga(string id, string title) =>
        new(new MangaId(id), title, null, null, new Uri($"https://example.com/manga/{id}"), []);

    private static HttpResponseMessage JsonResponse(string json) =>
        new()
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static DynamicProviderConfig DynamicAnimeConfig(string slug, string name, string apiHost) =>
        new()
        {
            Slug = slug,
            Name = name,
            Type = ProviderType.Anime,
            Hosts = new HostConfig
            {
                BaseHost = apiHost,
                ApiBase = $"https://{apiHost}",
                Referer = $"https://{apiHost}/"
            },
            Search = new SearchConfig
            {
                Method = SearchMethod.REST,
                Endpoint = "/search",
                QueryTemplate = "?q=${query}",
                ResultsPath = "$.data",
                ResultMapping =
                [
                    new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
                    new FieldMapping { SourcePath = "$.title", TargetField = "Title" }
                ]
            },
            Content = new ContentConfig(),
            Media = new MediaConfig()
        };

    private sealed class StubAnimeCatalog : IAnimeCatalog
    {
        private readonly IReadOnlyCollection<Anime> _searchResults;
        private readonly IReadOnlyCollection<Episode> _episodeResults;
        private readonly IReadOnlyCollection<StreamLink> _streamResults;
        private readonly bool _throwOperationCanceledOnSearch;

        public StubAnimeCatalog(
            IReadOnlyCollection<Anime> searchResults,
            IReadOnlyCollection<Episode>? episodeResults = null,
            IReadOnlyCollection<StreamLink>? streamResults = null,
            bool throwOperationCanceledOnSearch = false)
        {
            _searchResults = searchResults;
            _episodeResults = episodeResults ?? [];
            _streamResults = streamResults ?? [];
            _throwOperationCanceledOnSearch = throwOperationCanceledOnSearch;
        }

        public int EpisodeCalls { get; private set; }
        public int StreamCalls { get; private set; }

        public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (_throwOperationCanceledOnSearch)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(_searchResults);
        }

        public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
        {
            EpisodeCalls++;
            return Task.FromResult(_episodeResults);
        }

        public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            return Task.FromResult(_streamResults);
        }
    }

    private sealed class StubMangaCatalog : IMangaCatalog
    {
        private readonly IReadOnlyCollection<Manga> _searchResults;

        public StubMangaCatalog(IReadOnlyCollection<Manga> searchResults)
        {
            _searchResults = searchResults;
        }

        public Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(_searchResults);

        public Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Chapter>>([]);

        public Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ChapterPage>>([]);
    }

    private sealed class EmptyProviderStore : IProviderStore
    {
        public Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

        public Task<DynamicProviderConfig?> GetAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<DynamicProviderConfig?>(null);

        public Task SaveAsync(DynamicProviderConfig config, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task SetActiveAsync(string slug, ProviderType type, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<DynamicProviderConfig?> GetActiveAsync(ProviderType type, CancellationToken ct = default) =>
            Task.FromResult<DynamicProviderConfig?>(null);

        public Task<bool> ExistsAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<string> ExportAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<DynamicProviderConfig> ImportAsync(string json, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ConfigProviderStore : IProviderStore
    {
        private readonly IReadOnlyDictionary<string, DynamicProviderConfig> _configs;
        private readonly string? _activeAnime;

        public ConfigProviderStore(IReadOnlyCollection<DynamicProviderConfig> configs, string? activeAnime = null)
        {
            _configs = configs.ToDictionary(c => c.Slug, StringComparer.OrdinalIgnoreCase);
            _activeAnime = activeAnime;
        }

        public Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken ct = default)
        {
            var providers = _configs.Values
                .Select(config => ProviderInfo.FromConfig(config, string.Equals(config.Slug, _activeAnime, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return Task.FromResult<IReadOnlyList<ProviderInfo>>(providers);
        }

        public Task<DynamicProviderConfig?> GetAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(_configs.GetValueOrDefault(slug));

        public Task SaveAsync(DynamicProviderConfig config, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task SetActiveAsync(string slug, ProviderType type, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<DynamicProviderConfig?> GetActiveAsync(ProviderType type, CancellationToken ct = default) =>
            Task.FromResult(type == ProviderType.Anime && _activeAnime is not null ? _configs.GetValueOrDefault(_activeAnime) : null);

        public Task<bool> ExistsAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(_configs.ContainsKey(slug));

        public Task<string> ExportAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<DynamicProviderConfig> ImportAsync(string json, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
