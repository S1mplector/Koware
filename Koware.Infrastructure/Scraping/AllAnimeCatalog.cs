using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class AllAnimeCatalog : IAnimeCatalog
{
    private readonly HttpClient _httpClient;
    private readonly AllAnimeOptions _options;
    private readonly ILogger<AllAnimeCatalog> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public AllAnimeCatalog(HttpClient httpClient, IOptions<AllAnimeOptions> options, ILogger<AllAnimeCatalog> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var gql = "query( $search: SearchInput $limit: Int $page: Int $translationType: VaildTranslationTypeEnumType $countryOrigin: VaildCountryOriginEnumType ) { shows( search: $search limit: $limit page: $page translationType: $translationType countryOrigin: $countryOrigin ) { edges { _id name availableEpisodes __typename } }}";
        var variables = new
        {
            search = new { allowAdult = false, allowUnknown = false, query },
            limit = _options.SearchLimit,
            page = 1,
            translationType = _options.TranslationType,
            countryOrigin = "ALL"
        };

        var uri = BuildApiUri(gql, variables);
        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var edges = json.RootElement
            .GetProperty("data")
            .GetProperty("shows")
            .GetProperty("edges");

        var results = new List<Anime>();
        foreach (var edge in edges.EnumerateArray())
        {
            var id = edge.GetProperty("_id").GetString()!;
            var title = edge.GetProperty("name").GetString() ?? id;
            results.Add(new Anime(
                new AnimeId(id),
                title,
                synopsis: null,
                detailPage: BuildDetailUri(id),
                episodes: Array.Empty<Episode>()));
        }

        return results;
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        var gql = "query ($showId: String!) { show( _id: $showId ) { _id availableEpisodesDetail }}";
        var variables = new { showId = anime.Id.Value };
        var uri = BuildApiUri(gql, variables);

        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var episodesElement = json.RootElement
            .GetProperty("data")
            .GetProperty("show")
            .GetProperty("availableEpisodesDetail")
            .GetProperty(_options.TranslationType);

        var episodes = new List<Episode>();
        foreach (var ep in episodesElement.EnumerateArray())
        {
            if (int.TryParse(ep.GetString(), out var num))
            {
                var page = new Uri($"{_options.Referer.TrimEnd('/')}/anime/{anime.Id.Value}/episode-{num}");
                episodes.Add(new Episode(new EpisodeId($"{anime.Id.Value}:ep-{num}"), $"Episode {num}", num, page));
            }
        }

        return episodes.OrderBy(e => e.Number).ToArray();
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var (showId, episodeNumber) = ParseEpisodeId(episode);
        var gql = "query ($showId: String!, $translationType: VaildTranslationTypeEnumType!, $episodeString: String!) { episode( showId: $showId translationType: $translationType episodeString: $episodeString ) { episodeString sourceUrls }}";
        var variables = new
        {
            showId = showId,
            translationType = _options.TranslationType,
            episodeString = episodeNumber.ToString()
        };

        var uri = BuildApiUri(gql, variables);
        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var sources = json.RootElement
            .GetProperty("data")
            .GetProperty("episode")
            .GetProperty("sourceUrls")
            .EnumerateArray()
            .Select(el => new ProviderSource(el.GetProperty("sourceName").GetString()!, el.GetProperty("sourceUrl").GetString()!))
            .ToArray();

        var streams = new ConcurrentBag<StreamLink>();
        var tasks = sources.Select(src => ResolveSourceAsync(src, streams, cancellationToken));
        await Task.WhenAll(tasks);

        return streams
            .GroupBy(s => s.Url)
            .Select(g => g.First())
            .OrderByDescending(s => ParseQualityScore(s.Quality))
            .ToArray();
    }

    private async Task ResolveSourceAsync(ProviderSource source, ConcurrentBag<StreamLink> collector, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var decodedPath = AllAnimeSourceDecoder.Decode(source.Url);
            var absoluteUrl = EnsureAbsolute(decodedPath);

            using var response = await SendWithRetryAsync(new Uri(absoluteUrl), timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            var links = await ExtractLinksAsync(payload, absoluteUrl, source.Name, timeoutCts.Token);
            foreach (var link in links)
            {
                collector.Add(link);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Source {Source} returned 404; skipping.", source.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve source {Source}", source.Name);
        }
    }

    private async Task<IReadOnlyCollection<StreamLink>> ExtractLinksAsync(string payload, string sourceUrl, string provider, CancellationToken cancellationToken)
    {
        var links = new List<StreamLink>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            Walk(doc.RootElement, provider, links);
        }
        catch (JsonException)
        {
            _logger.LogDebug("Source payload for {Provider} was not JSON, attempting raw scan.", provider);
        }

        // If payload was m3u8 URL only, try that
        if (links.Count == 0 && sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var m3u8Links = await ParseM3U8Async(sourceUrl, provider, cancellationToken);
            links.AddRange(m3u8Links);
        }

        return links;
    }

    private void Walk(JsonElement element, string provider, List<StreamLink> links)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
                {
                    var link = linkProp.GetString()!;
                    var quality = element.TryGetProperty("resolutionStr", out var res) && res.ValueKind == JsonValueKind.String
                        ? res.GetString() ?? "auto"
                        : "auto";

                    AddLinkIfValid(links, link, quality, provider);
                }

                if (element.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                {
                    AddLinkIfValid(links, urlProp.GetString()!, "hls", provider);
                }

                foreach (var prop in element.EnumerateObject())
                {
                    Walk(prop.Value, provider, links);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, provider, links);
                }
                break;
        }
    }

    private void AddLinkIfValid(List<StreamLink> links, string url, string quality, string provider)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            links.Add(new StreamLink(abs, quality, provider));
            return;
        }

        // handle relative URLs from playlists
        if (Uri.TryCreate(new Uri(_options.Referer), url, out var resolved))
        {
            links.Add(new StreamLink(resolved, quality, provider));
        }
    }

    private async Task<IReadOnlyCollection<StreamLink>> ParseM3U8Async(string m3u8Url, string provider, CancellationToken cancellationToken)
    {
        var links = new List<StreamLink>();

        using var request = new HttpRequestMessage(HttpMethod.Get, m3u8Url);
        request.Headers.Referrer = new Uri(_options.Referer);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? currentResolution = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                var res = line.Split(',').FirstOrDefault(part => part.Contains("RESOLUTION", StringComparison.OrdinalIgnoreCase));
                if (res is not null)
                {
                    var parts = res.Split('=');
                    if (parts.Length == 2)
                    {
                        currentResolution = parts[1].Split('x').LastOrDefault();
                    }
                }
            }
            else if (!line.StartsWith("#") && currentResolution is not null)
            {
                var url = line;
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute) && Uri.TryCreate(new Uri(m3u8Url), url, out var resolved))
                {
                    url = resolved.ToString();
                }

                AddLinkIfValid(links, url, $"{currentResolution}p", provider);
                currentResolution = null;
            }
        }

        if (links.Count == 0)
        {
            AddLinkIfValid(links, m3u8Url, "auto", provider);
        }

        return links;
    }

    private static (string showId, int episodeNumber) ParseEpisodeId(Episode episode)
    {
        var parts = episode.Id.Value.Split(":ep-");
        var showId = parts[0];
        var number = episode.Number;
        return (showId, number);
    }

    private Uri BuildApiUri(string gql, object variables)
    {
        var query = $"query={Uri.EscapeDataString(gql)}&variables={Uri.EscapeDataString(JsonSerializer.Serialize(variables, _serializerOptions))}";
        return new Uri($"{_options.ApiBase.TrimEnd('/')}/api?{query}");
    }

    private Uri BuildDetailUri(string id) => new($"https://{_options.BaseHost}/anime/{id}");

    private static int ParseQualityScore(string quality)
    {
        if (int.TryParse(new string(quality.Where(char.IsDigit).ToArray()), out var number))
        {
            return number;
        }

        return quality.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }

    private string EnsureAbsolute(string path)
    {
        if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
        {
            return path;
        }

        var baseUrl = $"https://{_options.BaseHost}";
        return path.StartsWith('/') ? $"{baseUrl}{path}" : $"{baseUrl}/{path}";
    }

    private HttpRequestMessage BuildRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri(_options.Referer);
        request.Headers.TryAddWithoutValidation("Origin", _options.Referer.TrimEnd('/'));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("application/json, */*");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        const int perAttemptTimeoutSeconds = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = BuildRequest(uri);
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(perAttemptTimeoutSeconds));
            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                if (response.IsSuccessStatusCode || attempt == maxAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                _logger.LogDebug(ex, "Request to {Uri} failed on attempt {Attempt}/{MaxAttempts}. Retrying...", uri, attempt, maxAttempts);
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && attempt < maxAttempts)
            {
                _logger.LogDebug("Request to {Uri} timed out on attempt {Attempt}/{MaxAttempts}. Retrying...", uri, attempt, maxAttempts);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
        }

        // Should never hit because we return on maxAttempts
        throw new InvalidOperationException("Retry handler failed unexpectedly.");
    }

    private sealed record ProviderSource(string Name, string Url);
}

internal static class AllAnimeSourceDecoder
{
    private static readonly IReadOnlyDictionary<string, char> Map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
    {
        ["79"]='A',["7a"]='B',["7b"]='C',["7c"]='D',["7d"]='E',["7e"]='F',["7f"]='G',
        ["70"]='H',["71"]='I',["72"]='J',["73"]='K',["74"]='L',["75"]='M',["76"]='N',["77"]='O',
        ["68"]='P',["69"]='Q',["6a"]='R',["6b"]='S',["6c"]='T',["6d"]='U',["6e"]='V',["6f"]='W',
        ["60"]='X',["61"]='Y',["62"]='Z',
        ["59"]='a',["5a"]='b',["5b"]='c',["5c"]='d',["5d"]='e',["5e"]='f',["5f"]='g',
        ["50"]='h',["51"]='i',["52"]='j',["53"]='k',["54"]='l',["55"]='m',["56"]='n',["57"]='o',
        ["48"]='p',["49"]='q',["4a"]='r',["4b"]='s',["4c"]='t',["4d"]='u',["4e"]='v',["4f"]='w',
        ["40"]='x',["41"]='y',["42"]='z',
        ["08"]='0',["09"]='1',["0a"]='2',["0b"]='3',["0c"]='4',["0d"]='5',["0e"]='6',["0f"]='7',
        ["00"]='8',["01"]='9',
        ["15"]='-',["16"]='.',["67"]='_',["46"]='~',["02"]= ':',["17"]='/',["07"]='?',["1b"]='#',
        ["63"]='[',["65"]=']',["78"]='@',["19"]='!',["1c"]='$',["1e"]='&',["10"]='(',["11"]=')',
        ["12"]='*',["13"]='+',["14"]=',',["03"]=';',["05"]='=',["1d"]='%'
    };

    public static string Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        var trimmed = encoded.StartsWith("--", StringComparison.Ordinal) ? encoded[2..] : encoded.TrimStart('-');
        var builder = new StringBuilder(trimmed.Length / 2);

        for (var i = 0; i + 1 < trimmed.Length; i += 2)
        {
            var key = trimmed.Substring(i, 2);
            if (Map.TryGetValue(key, out var ch))
            {
                builder.Append(ch);
            }
        }

        var decoded = builder.ToString().Replace("/clock", "/clock.json", StringComparison.OrdinalIgnoreCase);
        return decoded;
    }
}
