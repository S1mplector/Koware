// Author: Ilgaz Mehmetoğlu
using System.Net;
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Dynamic IAnimeCatalog implementation that executes any DynamicProviderConfig.
/// </summary>
public sealed class DynamicAnimeCatalog : IAnimeCatalog
{
    private readonly DynamicProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ITransformEngine _transforms;
    private readonly ILogger<DynamicAnimeCatalog> _logger;
    private readonly DynamicProviderRequestGuard _requestGuard;

    public DynamicAnimeCatalog(
        DynamicProviderConfig config,
        HttpClient httpClient,
        ITransformEngine transforms,
        ILogger<DynamicAnimeCatalog> logger)
    {
        _config = config;
        _httpClient = httpClient;
        _transforms = transforms;
        _logger = logger;
        _requestGuard = new DynamicProviderRequestGuard(config);

        // Register any custom decoders
        foreach (var transform in config.Transforms.Where(t => t.Type == TransformType.Custom))
        {
            RegisterBuiltInDecoder(transform.Name);
        }
    }

    public string ProviderName => _config.Name;
    public string ProviderSlug => _config.Slug;

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        return await SearchAsync(query, SearchFilters.Empty, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        var searchConfig = _config.Search;
        var variables = new Dictionary<string, string>
        {
            ["query"] = query,
            ["search"] = query,
            ["limit"] = searchConfig.PageSize.ToString()
        };

        string response;

        // Check if this is a POST request with body template
        if (!string.IsNullOrEmpty(searchConfig.BodyTemplate))
        {
            var body = BuildRequest(searchConfig.BodyTemplate, variables);
            response = await ExecutePostRequestAsync(searchConfig.Endpoint, body, cancellationToken);
        }
        else
        {
            var requestBody = BuildRequest(searchConfig.QueryTemplate, variables);
            response = await ExecuteRequestAsync(
                searchConfig.Endpoint,
                searchConfig.Method,
                requestBody,
                cancellationToken);
        }

        var results = _transforms.ExtractAll(response, searchConfig.ResultMapping, searchConfig.ResultsPath);

        return results.Select(r => new Anime(
            new AnimeId(GetString(r, "Id") ?? Guid.NewGuid().ToString()),
            GetString(r, "Title") ?? "Unknown",
            synopsis: GetString(r, "Synopsis"),
            coverImage: TryParseUri(GetString(r, "CoverImage")),
            detailPage: TryParseUri(GetString(r, "DetailPage")) ?? GetFallbackDetailPage(),
            episodes: Array.Empty<Episode>()
        )).ToList();
    }

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        // Use empty search for popular content
        return await SearchAsync("", filters ?? SearchFilters.Empty, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        var episodeConfig = _config.Content.Episodes;
        if (episodeConfig == null)
        {
            _logger.LogWarning("No episode configuration for provider {Provider}", _config.Slug);
            return Array.Empty<Episode>();
        }

        var requestBody = BuildRequest(episodeConfig.QueryTemplate, new Dictionary<string, string>
        {
            ["showId"] = anime.Id.Value,
            ["animeId"] = anime.Id.Value,
            ["id"] = anime.Id.Value
        });

        var response = await ExecuteRequestAsync(
            episodeConfig.Endpoint,
            episodeConfig.Method,
            requestBody,
            cancellationToken);

        var results = _transforms.ExtractAll(response, episodeConfig.ResultMapping);

        var episodes = new List<Episode>();
        var number = 1;

        foreach (var r in results)
        {
            var epNum = GetInt(r, "Number") ?? number;
            var epId = GetString(r, "Id") ?? $"{anime.Id.Value}:ep-{epNum}";
            var title = GetString(r, "Title") ?? $"Episode {epNum}";
            var page = TryParseUri(GetString(r, "Page"));

            episodes.Add(new Episode(new EpisodeId(epId), title, epNum, page ?? anime.DetailPage));
            number++;
        }

        return episodes.OrderBy(e => e.Number).ToList();
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var streamConfig = _config.Media.Streams;
        if (streamConfig == null)
        {
            _logger.LogWarning("No stream configuration for provider {Provider}", _config.Slug);
            return Array.Empty<StreamLink>();
        }

        // Parse episode ID to get show ID and episode number
        var (showId, epNumber) = ParseEpisodeId(episode.Id.Value);

        var requestBody = BuildRequest(streamConfig.QueryTemplate, new Dictionary<string, string>
        {
            ["showId"] = showId,
            ["animeId"] = showId,
            ["episodeId"] = episode.Id.Value,
            ["episodeString"] = epNumber,
            ["ep"] = epNumber
        });

        var response = await ExecuteRequestAsync(
            streamConfig.Endpoint,
            streamConfig.Method,
            requestBody,
            cancellationToken);

        var results = _transforms.ExtractAll(response, streamConfig.ResultMapping);

        var streams = new List<StreamLink>();

        foreach (var r in results)
        {
            var urlString = GetString(r, "Url");
            if (string.IsNullOrEmpty(urlString))
                continue;

            // Apply custom decoder if specified
            if (!string.IsNullOrEmpty(streamConfig.CustomDecoder))
            {
                var rule = _config.Transforms.FirstOrDefault(t => t.Name == streamConfig.CustomDecoder);
                if (rule != null)
                {
                    urlString = _transforms.ApplyCustomTransform(urlString, rule);
                }
            }

            var url = TryParseUri(urlString);
            if (url is null)
                continue;

            var quality = GetString(r, "Quality") ?? "auto";
            var provider = GetString(r, "Provider") ?? _config.Name;

            streams.Add(new StreamLink(
                url,
                quality,
                provider,
                _config.Hosts.Referer,
                null,
                false,
                0,
                provider));
        }

        return streams;
    }

    private async Task<string> ExecutePostRequestAsync(
        string endpoint,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _requestGuard.ResolveEndpointUri(endpoint));
        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        ApplyRequestHeaders(request);

        return await _requestGuard.SendAsync(_httpClient, request, cancellationToken);
    }

    private async Task<string> ExecuteRequestAsync(
        string endpoint,
        SearchMethod method,
        string requestBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage();

        if (method == SearchMethod.GraphQL)
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = _requestGuard.ResolveGraphQlUri(endpoint, requestBody);
        }
        else if (method == SearchMethod.REST)
        {
            // For REST, the requestBody is treated as a relative query/path suffix.
            request.Method = HttpMethod.Get;
            request.RequestUri = _requestGuard.ResolveRestRequestUri(endpoint, requestBody);
        }
        else
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = _requestGuard.ResolveEndpointUri(endpoint);
        }

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        ApplyRequestHeaders(request);

        return await _requestGuard.SendAsync(_httpClient, request, cancellationToken);
    }

    private static string BuildRequest(string template, Dictionary<string, string> variables)
    {
        var result = template;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"${{{key}}}", value);
            result = result.Replace($"$({key})", value);
            result = result.Replace($"${key}", value);
        }
        return result;
    }

    private static (string showId, string epNumber) ParseEpisodeId(string episodeId)
    {
        var marker = ":ep-";
        var idx = episodeId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (idx > 0)
        {
            return (episodeId[..idx], episodeId[(idx + marker.Length)..]);
        }

        return (episodeId, "1");
    }

    private static string? GetString(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int? GetInt(Dictionary<string, object?> dict, string key)
    {
        var str = GetString(dict, key);
        return int.TryParse(str, out var num) ? num : null;
    }

    private void ApplyRequestHeaders(HttpRequestMessage request)
    {
        if (DynamicProviderRequestGuard.TryCreateHttpUri(_config.Hosts.Referer) is { } referer)
        {
            request.Headers.Referrer = referer;
        }

        if (!string.IsNullOrWhiteSpace(_config.Hosts.UserAgent))
        {
            if (!request.Headers.UserAgent.TryParseAdd(_config.Hosts.UserAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", _config.Hosts.UserAgent);
            }
        }

        foreach (var (key, value) in _config.Hosts.CustomHeaders)
        {
            if (key.Equals("referer", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static Uri? TryParseUri(string? url)
    {
        return DynamicProviderRequestGuard.TryCreateHttpUri(url);
    }

    private Uri GetFallbackDetailPage()
    {
        if (DynamicProviderRequestGuard.TryCreateHttpUri(_config.Hosts.Referer) is { } referer)
        {
            return referer;
        }

        return _requestGuard.RequestBaseUri;
    }

    private void RegisterBuiltInDecoder(string name)
    {
        // Register known decoders
        if (name.Equals("AllAnimeSourceDecoder", StringComparison.OrdinalIgnoreCase))
        {
            _transforms.RegisterDecoder(name, DecodeAllAnimeSource);
        }
    }

    private static string DecodeAllAnimeSource(string encoded)
    {
        // AllAnime-style hex decoding
        if (!encoded.StartsWith("-"))
            return encoded;

        try
        {
            var hex = encoded[1..];
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return encoded;
        }
    }
}
