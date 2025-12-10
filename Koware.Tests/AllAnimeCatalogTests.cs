// Author: Ilgaz Mehmetoğlu
// Tests for AllAnime catalog search, episodes, stream parsing, and request handling.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
#nullable enable
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Koware.Tests;

public class AllAnimeCatalogTests
{
    [Fact]
    public async Task SearchAsync_ReturnsTitlesAndIds_RespectsLimitInRequest()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new AllAnimeOptions
        {
            Enabled = true,
            ApiBase = "https://api.allanime.day",
            BaseHost = "allanime.day",
            Referer = "https://allmanga.to",
            UserAgent = "test-agent",
            TranslationType = "sub",
            SearchLimit = 5
        });

        handler.SetResponse(uri => uri.Host.Contains("allanime"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
{
  "data": {
    "shows": {
      "edges": [
        {"_id":"id1","name":"Title 1"},
        {"_id":"id2","name":"Title 2"}
      ]
    }
  }
}
""")
            });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var results = await catalog.SearchAsync("query", CancellationToken.None);

        Assert.Collection(results,
            a => Assert.Equal(("id1", "Title 1"), (a.Id.Value, a.Title)),
            a => Assert.Equal(("id2", "Title 2"), (a.Id.Value, a.Title)));
        var variablesParam = handler.LastRequest?.RequestUri?.Query
            ?.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?.FirstOrDefault(p => p.StartsWith("variables=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2, StringSplitOptions.TrimEntries)[1];

        Assert.NotNull(variablesParam);
        var variablesJson = Uri.UnescapeDataString(variablesParam!);
        using var doc = JsonDocument.Parse(variablesJson);
        Assert.Equal(5, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task GetEpisodesAsync_SkipsInvalidAndOrders()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(DefaultOptions());

        handler.SetResponse(_ => true, () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
{
  "data": {
    "show": {
      "availableEpisodesDetail": {
        "sub": ["0", "2", "x", "1"]
      }
    }
  }
}
""")
            });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var episodes = await catalog.GetEpisodesAsync(new Anime(new AnimeId("abc"), "demo", null, null, new Uri("https://test"), Array.Empty<Episode>()), CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, episodes.Select(e => e.Number).ToArray());
    }

    [Fact]
    public async Task GetStreamsAsync_ParsesMasterPlaylistAndSubtitles()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(DefaultOptions());

        var encodedMaster = EncodeForAllAnime("https://media.example.com/master.m3u8");

        // First call: episode -> sourceUrls
        handler.SetResponse(uri => uri.AbsoluteUri.Contains("episodeString"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
{
  "data": {
    "episode": {
      "episodeString": "1",
      "sourceUrls": [
        { "sourceName": "demo", "sourceUrl": "{{encodedMaster}}" }
      ]
    }
  }
}
""")
            });

        // Second call: master playlist
        handler.SetResponse(uri => uri.AbsoluteUri.Contains("master.m3u8"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
#EXTM3U
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",URI="subs/en.vtt"
#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360,SUBTITLES="subs"
index-f1.m3u8
""")
            });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var episode = new Episode(new EpisodeId("show:ep-1"), "Episode 1", 1, new Uri("https://example.com"));

        var streams = await catalog.GetStreamsAsync(episode, CancellationToken.None);

        var stream = Assert.Single(streams);
        Assert.Equal("360p", stream.Quality);
        Assert.StartsWith("https://media.example.com/index-f1.m3u8", stream.Url.ToString());

        var sub = Assert.Single(stream.Subtitles);
        Assert.Equal("English", sub.Label);
        Assert.Equal("https://media.example.com/subs/en.vtt", sub.Url.ToString());
        Assert.Equal("en", sub.Language);
    }

    [Fact]
    public async Task GetStreamsAsync_AttachesJsonSubtitlesToResolvedLinks()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(DefaultOptions());

        var encodedJsonSource = EncodeForAllAnime("https://media.example.com/source.json");

        handler.SetResponse(uri => uri.AbsoluteUri.Contains("episodeString"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
{
  "data": {
    "episode": {
      "episodeString": "1",
      "sourceUrls": [
        { "sourceName": "demo", "sourceUrl": "{{encodedJsonSource}}" }
      ]
    }
  }
}
""")
            });

        handler.SetResponse(uri => uri.AbsoluteUri.Contains("source.json"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
{
  "link": "https://media.example.com/video-720.m3u8",
  "resolutionStr": "720p",
  "subtitles": [
    { "lang": "en", "label": "English", "src": "subs/en.vtt" },
    { "lang": "es", "label": "Spanish", "src": "https://media.example.com/subs/es.vtt" }
  ]
}
""")
            });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var episode = new Episode(new EpisodeId("show:ep-1"), "Episode 1", 1, new Uri("https://example.com"));

        var streams = await catalog.GetStreamsAsync(episode, CancellationToken.None);

        var stream = Assert.Single(streams);
        Assert.Equal("720p", stream.Quality);
        Assert.Equal("https://media.example.com/video-720.m3u8", stream.Url.ToString());

        Assert.Collection(stream.Subtitles,
            s =>
            {
                Assert.Equal("English", s.Label);
                Assert.Equal("en", s.Language);
                Assert.Equal("https://media.example.com/subs/en.vtt", s.Url.ToString());
            },
            s =>
            {
                Assert.Equal("Spanish", s.Label);
                Assert.Equal("es", s.Language);
                Assert.Equal("https://media.example.com/subs/es.vtt", s.Url.ToString());
            });
    }

    [Fact]
    public async Task GetEpisodesAsync_MissingTranslationKey_DoesNotThrow_ReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(DefaultOptions());

        handler.SetResponse(_ => true, () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
{
  "data": {
    "show": {
      "availableEpisodesDetail": {
        "dub": ["1", "2"]
      }
    }
  }
}
""")
            });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var episodes = await catalog.GetEpisodesAsync(new Anime(new AnimeId("abc"), "demo", null, null, new Uri("https://test"), Array.Empty<Episode>()), CancellationToken.None);

        Assert.Empty(episodes);
    }

    [Fact]
    public async Task GetStreamsAsync_UsesFullShowIdFromEpisodeId()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(DefaultOptions());

        var encodedMaster = EncodeForAllAnime("https://media.example.com/master.m3u8");

        handler.SetResponse(uri => uri.AbsoluteUri.Contains("episodeString"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
{
  "data": {
    "episode": {
      "episodeString": "5",
      "sourceUrls": [
        { "sourceName": "demo", "sourceUrl": "{{encodedMaster}}" }
      ]
    }
  }
}
""")
            });

        handler.SetResponse(uri => uri.AbsoluteUri.Contains("master.m3u8"), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
#EXTM3U
#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360
index.m3u8
""")
            });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var episode = new Episode(new EpisodeId("demo:123:ep-5"), "Episode 5", 5, new Uri("https://example.com"));

        var streams = await catalog.GetStreamsAsync(episode, CancellationToken.None);
        var stream = Assert.Single(streams);
        Assert.Equal("360p", stream.Quality);

        var firstRequest = handler.Requests.First(r => r.RequestUri!.AbsoluteUri.Contains("episodeString", StringComparison.OrdinalIgnoreCase));
        var variablesParam = firstRequest.RequestUri?.Query
            ?.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?.FirstOrDefault(p => p.StartsWith("variables=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2, StringSplitOptions.TrimEntries)[1];

        Assert.NotNull(variablesParam);
        var variablesJson = Uri.UnescapeDataString(variablesParam!);
        using var doc = JsonDocument.Parse(variablesJson);
        Assert.Equal("demo:123", doc.RootElement.GetProperty("showId").GetString());
        Assert.Equal("5", doc.RootElement.GetProperty("episodeString").GetString());
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenNotEnabled()
    {
        var options = new AllAnimeOptions
        {
            Enabled = false,
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenApiBaseMissing()
    {
        var options = new AllAnimeOptions
        {
            Enabled = true,
            ApiBase = null,
            BaseHost = "example.com",
            Referer = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenBaseHostMissing()
    {
        var options = new AllAnimeOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            BaseHost = null,
            Referer = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenRefererMissing()
    {
        var options = new AllAnimeOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = null
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ReturnsTrue_WhenFullyConfigured()
    {
        var options = new AllAnimeOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = "https://example.com"
        };
        
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNotConfigured()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new AllAnimeOptions
        {
            Enabled = false,  // Not enabled
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = "https://example.com"
        });

        var catalog = new AllAnimeCatalog(httpClient, options, NullLogger<AllAnimeCatalog>.Instance);
        var results = await catalog.SearchAsync("test", CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(handler.Requests); // No HTTP requests should be made
    }

    [Fact]
    public void IsConfigured_Property_MatchesOptions()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        
        var configuredOptions = Options.Create(DefaultOptions());
        var configuredCatalog = new AllAnimeCatalog(httpClient, configuredOptions, NullLogger<AllAnimeCatalog>.Instance);
        Assert.True(configuredCatalog.IsConfigured);
        
        var unconfiguredOptions = Options.Create(new AllAnimeOptions { Enabled = false });
        var unconfiguredCatalog = new AllAnimeCatalog(httpClient, unconfiguredOptions, NullLogger<AllAnimeCatalog>.Instance);
        Assert.False(unconfiguredCatalog.IsConfigured);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("--", "")]
    public void Decode_EmptyOrNull_ReturnsEmpty(string? input, string expected)
    {
        var result = AllAnimeSourceDecoder.Decode(input!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Decode_SimpleUrl_DecodesCorrectly()
    {
        // "https" encoded with XOR 56: h=0x68^56=0x50, t=0x74^56=0x4c, etc.
        // h(0x68)^56=0x50, t(0x74)^56=0x4c, t(0x74)^56=0x4c, p(0x70)^56=0x48, s(0x73)^56=0x4b
        // :(0x3a)^56=0x02, /(0x2f)^56=0x17, /(0x2f)^56=0x17
        var encoded = EncodeForAllAnime("https://example.com/test");
        var decoded = AllAnimeSourceDecoder.Decode(encoded);
        Assert.Equal("https://example.com/test", decoded);
    }

    [Fact]
    public void Decode_WithDoubleDashPrefix_StripsPrefix()
    {
        var encoded = "--" + EncodeForAllAnime("test").TrimStart('-');
        var decoded = AllAnimeSourceDecoder.Decode(encoded);
        Assert.Equal("test", decoded);
    }

    [Fact]
    public void Decode_WithSingleDashPrefix_StripsPrefix()
    {
        var encoded = "-" + EncodeForAllAnime("test").TrimStart('-');
        var decoded = AllAnimeSourceDecoder.Decode(encoded);
        Assert.Equal("test", decoded);
    }

    [Fact]
    public void Decode_RealWorldUrl_DecodesCorrectly()
    {
        // Test with a real-world style URL
        var original = "https://www038.vipanicdn.net/streamhls/some/path/ep.1.1080.m3u8";
        var encoded = EncodeForAllAnime(original);
        var decoded = AllAnimeSourceDecoder.Decode(encoded);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_SpecialCharacters_HandlesCorrectly()
    {
        var original = "https://test.com/path?query=value&other=123#fragment";
        var encoded = EncodeForAllAnime(original);
        var decoded = AllAnimeSourceDecoder.Decode(encoded);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_RelativePath_DecodesCorrectly()
    {
        var original = "/apivtwo/clock?id=abc123";
        var encoded = EncodeForAllAnime(original);
        var decoded = AllAnimeSourceDecoder.Decode(encoded);
        Assert.Equal(original, decoded);
    }

    private static AllAnimeOptions DefaultOptions() => new()
    {
        Enabled = true,
        ApiBase = "https://api.allanime.day",
        BaseHost = "allanime.day",
        Referer = "https://allmanga.to",
        UserAgent = "test-agent",
        TranslationType = "sub",
        SearchLimit = 10
    };

    /// <summary>
    /// Encode a string using XOR 56 (the inverse of AllAnimeSourceDecoder.Decode).
    /// </summary>
    private static string EncodeForAllAnime(string text)
    {
        const int XorKey = 56;
        var result = new System.Text.StringBuilder(text.Length * 2 + 2);
        result.Append("--");
        foreach (var ch in text)
        {
            var encoded = (byte)(ch ^ XorKey);
            result.Append(encoded.ToString("x2"));
        }
        return result.ToString();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(Func<Uri, bool> predicate, Func<HttpResponseMessage> responseFactory)> _responses = new();
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = new();

        public void SetResponse(Func<Uri, bool> match, Func<HttpResponseMessage> responseFactory) =>
            _responses.Add((match, responseFactory));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests.Add(request);
            foreach (var (predicate, factory) in _responses)
            {
                if (predicate(request.RequestUri!))
                {
                    return Task.FromResult(factory());
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
