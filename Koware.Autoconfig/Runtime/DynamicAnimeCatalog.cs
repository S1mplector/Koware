// Author: Ilgaz Mehmetoğlu
using System.Text.Json;
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
        try
        {
            var searchConfig = _config.Search;
            var requestBody = BuildRequest(searchConfig.QueryTemplate, new Dictionary<string, string>
            {
                ["query"] = query,
                ["search"] = query,
                ["limit"] = searchConfig.PageSize.ToString()
            });

            var response = await ExecuteRequestAsync(
                searchConfig.Endpoint,
                searchConfig.Method,
                requestBody,
                cancellationToken);

            var results = _transforms.ExtractAll(response, searchConfig.ResultMapping);
            
            return results.Select(r => new Anime(
                new AnimeId(GetString(r, "Id") ?? Guid.NewGuid().ToString()),
                GetString(r, "Title") ?? "Unknown",
                synopsis: GetString(r, "Synopsis"),
                coverImage: TryParseUri(GetString(r, "CoverImage")),
                detailPage: TryParseUri(GetString(r, "DetailPage")),
                episodes: Array.Empty<Episode>()
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for provider {Provider}", _config.Slug);
            return Array.Empty<Anime>();
        }
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

        try
        {
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
                
                episodes.Add(new Episode(new EpisodeId(epId), title, epNum, page));
                number++;
            }
            
            return episodes.OrderBy(e => e.Number).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetEpisodes failed for anime {AnimeId}", anime.Id.Value);
            return Array.Empty<Episode>();
        }
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var streamConfig = _config.Media.Streams;
        if (streamConfig == null)
        {
            _logger.LogWarning("No stream configuration for provider {Provider}", _config.Slug);
            return Array.Empty<StreamLink>();
        }

        try
        {
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
                
                if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStreams failed for episode {EpisodeId}", episode.Id.Value);
            return Array.Empty<StreamLink>();
        }
    }

    private async Task<string> ExecuteRequestAsync(
        string endpoint,
        SearchMethod method,
        string requestBody,
        CancellationToken cancellationToken)
    {
        var baseUrl = _config.Hosts.ApiBase ?? $"https://{_config.Hosts.BaseHost}";
        var fullUrl = endpoint.StartsWith("http") ? endpoint : $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";

        using var request = new HttpRequestMessage();
        
        if (method == SearchMethod.GraphQL)
        {
            request.Method = HttpMethod.Get;
            var query = Uri.EscapeDataString(requestBody);
            request.RequestUri = new Uri($"{fullUrl}?query={query}");
        }
        else if (method == SearchMethod.REST)
        {
            // For REST, the requestBody might contain the full URL with parameters
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri(requestBody.StartsWith("http") ? requestBody : fullUrl + requestBody);
        }
        else
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri(fullUrl);
        }

        // Add headers
        request.Headers.Referrer = new Uri(_config.Hosts.Referer);
        request.Headers.UserAgent.ParseAdd(_config.Hosts.UserAgent);
        
        foreach (var (key, value) in _config.Hosts.CustomHeaders)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStringAsync(cancellationToken);
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

    private static Uri? TryParseUri(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
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
