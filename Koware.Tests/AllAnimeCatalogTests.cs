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
        var episodes = await catalog.GetEpisodesAsync(new Anime(new AnimeId("abc"), "demo", null, new Uri("https://test"), Array.Empty<Episode>()), CancellationToken.None);

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
        var episodes = await catalog.GetEpisodesAsync(new Anime(new AnimeId("abc"), "demo", null, new Uri("https://test"), Array.Empty<Episode>()), CancellationToken.None);

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

    private static AllAnimeOptions DefaultOptions() => new()
    {
        ApiBase = "https://api.allanime.day",
        BaseHost = "allanime.day",
        Referer = "https://allmanga.to",
        UserAgent = "test-agent",
        TranslationType = "sub",
        SearchLimit = 10
    };

    private static string EncodeForAllAnime(string text)
    {
        var map = new Dictionary<char, string>
        {
            ['A']="79",['B']="7a",['C']="7b",['D']="7c",['E']="7d",['F']="7e",['G']="7f",
            ['H']="70",['I']="71",['J']="72",['K']="73",['L']="74",['M']="75",['N']="76",['O']="77",
            ['P']="68",['Q']="69",['R']="6a",['S']="6b",['T']="6c",['U']="6d",['V']="6e",['W']="6f",
            ['X']="60",['Y']="61",['Z']="62",
            ['a']="59",['b']="5a",['c']="5b",['d']="5c",['e']="5d",['f']="5e",['g']="5f",
            ['h']="50",['i']="51",['j']="52",['k']="53",['l']="54",['m']="55",['n']="56",['o']="57",
            ['p']="48",['q']="49",['r']="4a",['s']="4b",['t']="4c",['u']="4d",['v']="4e",['w']="4f",
            ['x']="40",['y']="41",['z']="42",
            ['0']="08",['1']="09",['2']="0a",['3']="0b",['4']="0c",['5']="0d",['6']="0e",['7']="0f",
            ['8']="00",['9']="01",
            ['-']="15",['.']="16",['_']="67",['~']="46",[':']="02",['/']="17",['?']="07",['#']="1b",
            ['[']="63",[']']="65",['@']="78",['!']="19",['$']="1c",['&']="1e",['(']="10",[')']="11",
            ['*']="12",['+']="13",[',']="14",[';']="03",['=']="05",['%']="1d"
        };

        var result = new List<string>();
        foreach (var ch in text)
        {
            if (!map.TryGetValue(ch, out var code))
            {
                throw new InvalidOperationException($"Cannot encode character '{ch}'");
            }
            result.Add(code);
        }

        return "--" + string.Join(string.Empty, result);
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
