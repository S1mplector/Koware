// Author: Ilgaz Mehmetoğlu
using System.Net;
using System.Text;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Runtime;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Koware.Tests.Autoconfig;

public sealed class DynamicProviderGuardrailTests
{
    private readonly StubHttpMessageHandler _httpHandler = new();
    private readonly HttpClient _httpClient;
    private readonly TransformEngine _transformEngine;
    private readonly LoggerFactory _loggerFactory = new();

    public DynamicProviderGuardrailTests()
    {
        _httpClient = new HttpClient(_httpHandler);
        _transformEngine = new TransformEngine(_loggerFactory.CreateLogger<TransformEngine>());
    }

    [Fact]
    public async Task SearchAsync_BlocksAbsoluteEndpointsOutsideConfiguredHosts()
    {
        var config = CreateAnimeConfig() with
        {
            Search = CreateSearchConfig("https://attacker.example.net/api/search")
        };

        var catalog = CreateAnimeCatalog(config);

        var ex = await Assert.ThrowsAsync<DynamicProviderRuntimeException>(
            () => catalog.SearchAsync("naruto"));

        Assert.Equal(DynamicProviderFailureKind.BlockedEndpoint, ex.Kind);
        Assert.Empty(_httpHandler.Requests);
    }

    [Fact]
    public async Task SearchAsync_RejectsOversizedResponses()
    {
        var config = CreateAnimeConfig();
        var catalog = CreateAnimeCatalog(config);

        _httpHandler.SetResponse(
            uri => uri.AbsolutePath.EndsWith("/api/search", StringComparison.Ordinal),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 5 * 1024 * 1024), Encoding.UTF8, "application/json")
            });

        var ex = await Assert.ThrowsAsync<DynamicProviderRuntimeException>(
            () => catalog.SearchAsync("naruto"));

        Assert.Equal(DynamicProviderFailureKind.ResponseTooLarge, ex.Kind);
        Assert.Single(_httpHandler.Requests);
    }

    [Fact]
    public async Task GetStreamsAsync_FiltersNonHttpUrls()
    {
        var config = CreateAnimeConfig();
        var catalog = CreateAnimeCatalog(config);

        _httpHandler.SetResponse(
            uri => uri.AbsolutePath.EndsWith("/api/streams", StringComparison.Ordinal),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    [
                      { "url": "javascript:alert('x')", "quality": "bad" },
                      { "url": "https://cdn.example.com/stream.m3u8", "quality": "1080p" }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json")
            });

        var episode = new Episode(
            new EpisodeId("show-1:ep-1"),
            "Episode 1",
            1,
            new Uri("https://example.com/shows/show-1"));

        var streams = await catalog.GetStreamsAsync(episode);

        var stream = Assert.Single(streams);
        Assert.Equal("https", stream.Url.Scheme);
        Assert.Equal("1080p", stream.Quality);
    }

    private DynamicAnimeCatalog CreateAnimeCatalog(DynamicProviderConfig config)
    {
        return new DynamicAnimeCatalog(
            config,
            _httpClient,
            _transformEngine,
            _loggerFactory.CreateLogger<DynamicAnimeCatalog>());
    }

    private static DynamicProviderConfig CreateAnimeConfig()
    {
        return new DynamicProviderConfig
        {
            Name = "GuardedAnime",
            Slug = "guardedanime",
            Type = ProviderType.Anime,
            Hosts = new HostConfig
            {
                BaseHost = "example.com",
                ApiBase = "https://api.example.com",
                Referer = "https://example.com/"
            },
            Search = CreateSearchConfig("/api/search"),
            Content = new ContentConfig
            {
                Episodes = new EndpointConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = "/api/episodes",
                    QueryTemplate = "?id=${id}",
                    ResultMapping = new[]
                    {
                        new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
                        new FieldMapping { SourcePath = "$.number", TargetField = "Number" }
                    }
                }
            },
            Media = new MediaConfig
            {
                Streams = new StreamConfig
                {
                    Method = SearchMethod.REST,
                    Endpoint = "/api/streams",
                    QueryTemplate = "?episode=${episodeId}",
                    ResultMapping = new[]
                    {
                        new FieldMapping { SourcePath = "$.url", TargetField = "Url" },
                        new FieldMapping { SourcePath = "$.quality", TargetField = "Quality" }
                    }
                }
            }
        };
    }

    private static SearchConfig CreateSearchConfig(string endpoint)
    {
        return new SearchConfig
        {
            Method = SearchMethod.REST,
            Endpoint = endpoint,
            QueryTemplate = "?query=${query}",
            ResultsPath = "$.data",
            ResultMapping = new[]
            {
                new FieldMapping { SourcePath = "$.id", TargetField = "Id" },
                new FieldMapping { SourcePath = "$.title", TargetField = "Title" }
            }
        };
    }
}
