// Author: Ilgaz Mehmetoğlu | Summary: AllAnime implementation of IAnimeCatalog handling search, episodes, streams, and source decoding.
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

    /// <summary>
    /// Returns true if this provider is properly configured.
    /// </summary>
    public bool IsConfigured => _options.IsConfigured;

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, SearchFilters.Empty, cancellationToken);

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning("AllAnime source not configured. Add configuration to ~/.config/koware/appsettings.user.json");
            return Array.Empty<Anime>();
        }

        // Build GraphQL query with filter support
        var gql = "query( $search: SearchInput $limit: Int $page: Int $translationType: VaildTranslationTypeEnumType $countryOrigin: VaildCountryOriginEnumType ) { shows( search: $search limit: $limit page: $page translationType: $translationType countryOrigin: $countryOrigin ) { edges { _id name availableEpisodes __typename } }}";
        
        // Build search input with filter parameters
        var searchInput = new Dictionary<string, object>
        {
            ["allowAdult"] = false,
            ["allowUnknown"] = false
        };
        
        if (!string.IsNullOrWhiteSpace(query))
        {
            searchInput["query"] = query;
        }
        
        // Apply genre filter if specified
        if (filters.Genres?.Count > 0)
        {
            searchInput["genres"] = filters.Genres;
        }
        
        // Apply year filter if specified
        if (filters.Year.HasValue)
        {
            searchInput["year"] = filters.Year.Value;
        }
        
        // Apply status filter if specified
        if (filters.Status != ContentStatus.Any)
        {
            var statusValue = filters.Status switch
            {
                ContentStatus.Ongoing => "Releasing",
                ContentStatus.Completed => "Finished",
                ContentStatus.Upcoming => "Not Yet Aired",
                _ => null
            };
            if (statusValue != null)
            {
                searchInput["status"] = statusValue;
            }
        }
        
        // Map country origin
        var countryOrigin = filters.CountryOrigin switch
        {
            "JP" => "JP",
            "KR" => "KR", 
            "CN" => "CN",
            _ => "ALL"
        };
        
        // Note: AllAnime API does not support sortBy in SearchInput for shows.
        // The API returns popular content by default when no query is provided.

        var variables = new
        {
            search = searchInput,
            limit = _options.SearchLimit,
            page = 1,
            translationType = _options.TranslationType,
            countryOrigin
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

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return Array.Empty<Anime>();
        }

        // Use search with popularity sort and empty query
        var browseFilters = (filters ?? SearchFilters.Empty) with { Sort = SearchSort.Popularity };
        return await SearchAsync("", browseFilters, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        var gql = "query ($showId: String!) { show( _id: $showId ) { _id availableEpisodesDetail }}";
        var variables = new { showId = anime.Id.Value };
        var uri = BuildApiUri(gql, variables);

        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("show", out var show) ||
            !show.TryGetProperty("availableEpisodesDetail", out var available))
        {
            _logger.LogWarning("Episode response missing expected fields for anime {AnimeId}.", anime.Id.Value);
            return Array.Empty<Episode>();
        }

        var translationKey = _options.TranslationType ?? "sub";
        JsonElement episodesElement;
        if (!available.TryGetProperty(translationKey, out episodesElement))
        {
            var matched = available.EnumerateObject()
                .FirstOrDefault(prop => string.Equals(prop.Name, translationKey, StringComparison.OrdinalIgnoreCase));
            episodesElement = matched.Value;
        }

        if (episodesElement.ValueKind == JsonValueKind.Undefined)
        {
            _logger.LogWarning("Translation type '{Translation}' not found for anime {AnimeId}.", translationKey, anime.Id.Value);
            return Array.Empty<Episode>();
        }

        var episodes = new List<Episode>();
        foreach (var ep in episodesElement.EnumerateArray())
        {
            var raw = ep.GetString();
            if (!int.TryParse(raw, out var num) || num < 1)
            {
                _logger.LogDebug("Skipping invalid episode '{Episode}' for anime {AnimeId}", raw, anime.Id.Value);
                continue;
            }

            var refererBase = string.IsNullOrWhiteSpace(_options.Referer) ? "https://example.com" : _options.Referer;
            var page = new Uri($"{refererBase.TrimEnd('/')}/anime/{anime.Id.Value}/episode-{num}");
            episodes.Add(new Episode(new EpisodeId($"{anime.Id.Value}:ep-{num}"), $"Episode {num}", num, page));
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
        var attempts = new ConcurrentBag<string>();
        var tasks = sources.Select(src => ResolveSourceAsync(src, streams, attempts, cancellationToken));
        await Task.WhenAll(tasks);

        var resolved = streams
            .Where(s => s.Url.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Url)
            .Select(g => g
                .OrderByDescending(s => s.HostPriority)
                .ThenByDescending(s => ParseQualityScore(s.Quality))
                .First())
            .OrderByDescending(s => ParseQualityScore(s.Quality))
            .ThenByDescending(s => s.HostPriority)
            .ToArray();

        var summary = attempts.ToArray();
        if (summary.Length > 0)
        {
            _logger.LogInformation("Stream resolution summary: {Summary}", string.Join("; ", summary));
        }

        if (resolved.Length == 0 && summary.Length > 0)
        {
            _logger.LogWarning("No playable streams resolved. Tried: {Summary}", string.Join("; ", summary));
        }

        return resolved;
    }

    private async Task ResolveSourceAsync(ProviderSource source, ConcurrentBag<StreamLink> collector, ConcurrentBag<string> attempts, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        var host = "unknown";

        try
        {
            var decodedPath = AllAnimeSourceDecoder.Decode(source.Url);
            var absoluteUrl = EnsureAbsolute(decodedPath);
            host = Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var absUri) ? absUri.Host : "unknown";

            using var response = await SendWithRetryAsync(new Uri(absoluteUrl), timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            var links = await ExtractLinksAsync(payload, absoluteUrl, source.Name, timeoutCts.Token);
            foreach (var link in links)
            {
                collector.Add(link);
            }
            attempts.Add($"{source.Name}@{host}: ok ({links.Count} links)");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Source {Source} returned 404; skipping.", source.Name);
            attempts.Add($"{source.Name}@{host}: http 404");
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode.HasValue ? ((int)ex.StatusCode.Value).ToString() : "http-error";
            attempts.Add($"{source.Name}@{host}: http {code}");
            _logger.LogDebug(ex, "HTTP error resolving source {Source}", source.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve source {Source}", source.Name);
            attempts.Add($"{source.Name}@{host}: error {ex.GetType().Name}");
        }
    }

    private async Task<IReadOnlyCollection<StreamLink>> ExtractLinksAsync(string payload, string sourceUrl, string provider, CancellationToken cancellationToken)
    {
        var links = new List<StreamLink>();
        string? referrer = null;
        IReadOnlyList<SubtitleTrack>? subtitles = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            referrer = TryFindReferer(doc.RootElement);
            subtitles = ExtractSubtitleTracks(doc.RootElement, sourceUrl);
            Walk(doc.RootElement, provider, links, referrer, subtitles);
        }
        catch (JsonException)
        {
            _logger.LogDebug("Source payload for {Provider} was not JSON, attempting raw scan.", provider);
        }

        var effectiveReferrer = string.IsNullOrWhiteSpace(referrer) ? _options.Referer : referrer;
        if (links.Count == 0 && sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var m3u8Links = await ParseM3U8Async(sourceUrl, provider, effectiveReferrer, subtitles, cancellationToken);
            links.AddRange(m3u8Links);
        }

        if (links.Count == 0)
        {
            return links;
        }

        var expanded = await ExpandManifestStreamsAsync(links, provider, effectiveReferrer, subtitles, cancellationToken);
        return expanded.ToArray();
    }

    private async Task<IReadOnlyCollection<StreamLink>> ExpandManifestStreamsAsync(
        IReadOnlyCollection<StreamLink> links,
        string provider,
        string? fallbackReferrer,
        IReadOnlyList<SubtitleTrack>? fallbackSubtitles,
        CancellationToken cancellationToken)
    {
        var expanded = new List<StreamLink>();
        foreach (var link in links)
        {
            if (IsM3U8(link.Url) && IsCoarseQuality(link.Quality))
            {
                var manifestReferrer = link.Referrer ?? fallbackReferrer ?? _options.Referer;
                var manifestSubtitles = link.Subtitles.Count > 0 ? link.Subtitles : fallbackSubtitles;
                var variants = await ParseM3U8Async(link.Url.ToString(), provider, manifestReferrer, manifestSubtitles, cancellationToken);
                if (variants.Count > 0)
                {
                    expanded.AddRange(variants);
                    continue;
                }
            }

            expanded.Add(link);
        }

        return expanded;
    }

    private static bool IsCoarseQuality(string? quality) =>
        string.IsNullOrWhiteSpace(quality) ||
        quality.Equals("hls", StringComparison.OrdinalIgnoreCase) ||
        quality.Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsM3U8(Uri uri) =>
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private void Walk(JsonElement element, string provider, List<StreamLink> links, string? referrer, IReadOnlyList<SubtitleTrack>? subtitles)
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

                    AddLinkIfValid(links, link, quality, provider, referrer, subtitles);
                }

                if (element.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                {
                    AddLinkIfValid(links, urlProp.GetString()!, "hls", provider, referrer, subtitles);
                }

                foreach (var prop in element.EnumerateObject())
                {
                    Walk(prop.Value, provider, links, referrer, subtitles);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, provider, links, referrer, subtitles);
                }
                break;
        }
    }

    private static IReadOnlyList<SubtitleTrack>? ExtractSubtitleTracks(JsonElement root, string sourceUrl)
    {
        // AllAnime payloads often include subtitles alongside source links:
        // "subtitles": [ { "lang": "en", "label": "English", "src": "https://..." } ]
        // Grab the first subtitles array we can find and resolve relative URLs against the source.
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("subtitles") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var tracks = new List<SubtitleTrack>();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("src", out var srcProp) || srcProp.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var src = srcProp.GetString();
                        if (string.IsNullOrWhiteSpace(src))
                        {
                            continue;
                        }

                        if (!Uri.IsWellFormedUriString(src, UriKind.Absolute) && Uri.TryCreate(new Uri(sourceUrl), src, out var resolved))
                        {
                            src = resolved.ToString();
                        }

                        if (!Uri.TryCreate(src, UriKind.Absolute, out var srcUri))
                        {
                            continue;
                        }

                        var label = item.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
                            ? labelProp.GetString()
                            : "Subtitles";
                        var lang = item.TryGetProperty("lang", out var langProp) && langProp.ValueKind == JsonValueKind.String
                            ? langProp.GetString()
                            : null;

                        tracks.Add(new SubtitleTrack(label ?? "Subtitles", srcUri, lang));
                    }

                    if (tracks.Count > 0)
                    {
                        return tracks;
                    }
                }

                var nested = ExtractSubtitleTracks(prop.Value, sourceUrl);
                if (nested is { Count: > 0 })
                {
                    return nested;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var nested = ExtractSubtitleTracks(item, sourceUrl);
                if (nested is { Count: > 0 })
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? TryFindReferer(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("Referer", out var refererProp) && refererProp.ValueKind == JsonValueKind.String)
                {
                    var value = refererProp.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    var nested = TryFindReferer(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = TryFindReferer(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
                break;
        }

        return null;
    }

    private void AddLinkIfValid(List<StreamLink> links, string url, string quality, string provider, string? referrer, IReadOnlyList<SubtitleTrack>? subtitles = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var effectiveReferrer = string.IsNullOrWhiteSpace(referrer) ? _options.Referer : referrer;
        var requiresSoftSubs = subtitles is { Count: > 0 };

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            links.Add(new StreamLink(
                abs,
                quality,
                provider,
                effectiveReferrer,
                subtitles,
                requiresSoftSubs,
                ComputeHostPriority(abs),
                provider));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.Referer) && Uri.TryCreate(new Uri(_options.Referer), url, out var resolved))
        {
            links.Add(new StreamLink(
                resolved,
                quality,
                provider,
                effectiveReferrer,
                subtitles,
                requiresSoftSubs,
                ComputeHostPriority(resolved),
                provider));
        }
    }

    private static int ComputeHostPriority(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        if (host.Contains("wixmp") || host.Contains("hianime"))
        {
            return 20;
        }

        if (host.Contains("akamaized") || host.Contains("akamai"))
        {
            return 10;
        }

        if (host.Contains("sharepoint") || host.Contains("haildrop"))
        {
            return -20;
        }

        return 0;
    }

    private async Task<IReadOnlyCollection<StreamLink>> ParseM3U8Async(string m3u8Url, string provider, string? referrer, IReadOnlyList<SubtitleTrack>? fallbackSubtitles, CancellationToken cancellationToken)
    {
        var links = new List<StreamLink>();

        using var request = new HttpRequestMessage(HttpMethod.Get, m3u8Url);
        var effectiveReferrer = string.IsNullOrWhiteSpace(referrer) ? _options.Referer : referrer;
        if (!string.IsNullOrWhiteSpace(effectiveReferrer) && Uri.TryCreate(effectiveReferrer, UriKind.Absolute, out var refUri))
        {
            request.Headers.Referrer = refUri;
        }
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        HttpResponseMessage? response = null;
        string body;
        try
        {
            response = await _httpClient.SendAsync(request, timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        finally
        {
            response?.Dispose();
            // Delay CTS disposal to avoid race with timer callback
            _ = Task.Delay(100).ContinueWith(_ => timeoutCts.Dispose());
        }
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IReadOnlyList<SubtitleTrack> subtitleTracks = ExtractSubtitleTracks(lines, m3u8Url);
        if (subtitleTracks.Count == 0 && fallbackSubtitles is { Count: > 0 })
        {
            subtitleTracks = fallbackSubtitles;
        }

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

                AddLinkIfValid(links, url, $"{currentResolution}p", provider, effectiveReferrer, subtitleTracks);
                currentResolution = null;
            }
        }

        if (links.Count == 0)
        {
            AddLinkIfValid(links, m3u8Url, "auto", provider, effectiveReferrer, subtitleTracks);
        }

        return links;
    }

    private static IReadOnlyList<SubtitleTrack> ExtractSubtitleTracks(string[] lines, string m3u8Url)
    {
        var subtitles = new List<SubtitleTrack>();
        foreach (var line in lines.Where(l => l.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase)))
        {
            if (!line.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = ParseAttributes(line);
            if (!attributes.TryGetValue("URI", out var uriVal) || string.IsNullOrWhiteSpace(uriVal))
            {
                continue;
            }

            var label = attributes.TryGetValue("NAME", out var nameVal) ? nameVal : "Subtitles";
            var lang = attributes.TryGetValue("LANGUAGE", out var langVal) ? langVal : null;
            var subtitleUrl = uriVal;
            if (!Uri.IsWellFormedUriString(subtitleUrl, UriKind.Absolute) && Uri.TryCreate(new Uri(m3u8Url), subtitleUrl, out var resolvedSub))
            {
                subtitleUrl = resolvedSub.ToString();
            }

            if (Uri.TryCreate(subtitleUrl, UriKind.Absolute, out var subUri))
            {
                subtitles.Add(new SubtitleTrack(label, subUri, lang));
            }
        }

        return subtitles;
    }

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0 || colonIdx + 1 >= line.Length)
        {
            return dict;
        }

        var attributePart = line[(colonIdx + 1)..];
        var parts = attributePart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            var value = kv[1].Trim('"');
            dict[kv[0]] = value;
        }

        return dict;
    }

    private static (string showId, int episodeNumber) ParseEpisodeId(Episode episode)
    {
        var marker = ":ep-";
        var idValue = episode.Id.Value;
        var idx = idValue.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

        var showId = idx > 0 ? idValue[..idx] : idValue;
        var number = episode.Number;

        if (idx >= 0 && idx + marker.Length < idValue.Length)
        {
            var suffix = idValue[(idx + marker.Length)..];
            if (int.TryParse(suffix, out var parsed) && parsed > 0)
            {
                number = parsed;
            }
        }

        return (showId, number);
    }

    private Uri BuildApiUri(string gql, object variables)
    {
        var apiBase = string.IsNullOrWhiteSpace(_options.ApiBase) ? "https://example.com" : _options.ApiBase;
        var query = $"query={Uri.EscapeDataString(gql)}&variables={Uri.EscapeDataString(JsonSerializer.Serialize(variables, _serializerOptions))}";
        return new Uri($"{apiBase.TrimEnd('/')}/api?{query}");
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
        var referer = string.IsNullOrWhiteSpace(_options.Referer) ? "https://example.com/" : _options.Referer;
        request.Headers.Referrer = new Uri(referer);
        request.Headers.TryAddWithoutValidation("Origin", referer.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(_options.UserAgent))
        {
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        }
        request.Headers.Accept.ParseAdd("application/json, */*");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        const int perAttemptTimeoutSeconds = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = BuildRequest(uri);
            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(perAttemptTimeoutSeconds));
            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                // Delay CTS disposal to avoid race with timer callback
                _ = Task.Delay(100).ContinueWith(_ => attemptCts.Dispose());
                
                if (response.IsSuccessStatusCode || attempt == maxAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                _ = Task.Delay(100).ContinueWith(_ => attemptCts.Dispose());
                _logger.LogDebug(ex, "Request to {Uri} failed on attempt {Attempt}/{MaxAttempts}. Retrying...", uri, attempt, maxAttempts);
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                _ = Task.Delay(100).ContinueWith(_ => attemptCts.Dispose());
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
