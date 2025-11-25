// Author: Ilgaz Mehmetoğlu
// GogoAnime provider implementation using the consumet API for search/episodes/streams.
using System.Text.Json;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class GogoAnimeCatalog : IAnimeCatalog
{
    private readonly HttpClient _httpClient;
    private readonly GogoAnimeOptions _options;
    private readonly ILogger<GogoAnimeCatalog> _logger;

    public GogoAnimeCatalog(HttpClient httpClient, IOptions<GogoAnimeOptions> options, ILogger<GogoAnimeCatalog> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.ApiBase.TrimEnd('/')}/anime/gogoanime/{Uri.EscapeDataString(query)}?page=1";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Anime>();
        }

        var list = new List<Anime>();
        foreach (var item in results.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? string.Empty;
            var title = item.GetProperty("title").GetString() ?? id;
            var urlSlug = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            var detailUrl = BuildSiteUrl(urlSlug ?? $"/category/{id}");
            list.Add(new Anime(new AnimeId($"gogo:{id}"), title, null, detailUrl, Array.Empty<Episode>()));
        }

        return list;
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        var (id, isGogo) = SplitId(anime.Id);
        if (!isGogo)
        {
            throw new InvalidOperationException("GogoAnimeCatalog can only handle gogo-prefixed anime ids.");
        }

        var url = $"{_options.ApiBase.TrimEnd('/')}/anime/gogoanime/info/{Uri.EscapeDataString(id)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("episodes", out var episodesEl) || episodesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Episode>();
        }

        var episodes = new List<Episode>();
        foreach (var ep in episodesEl.EnumerateArray())
        {
            var epId = ep.GetProperty("id").GetString() ?? string.Empty;
            var number = ep.TryGetProperty("number", out var numProp) && numProp.TryGetInt32(out var n) ? n : episodes.Count + 1;
            var title = ep.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
            var pageUrl = BuildSiteUrl($"/{id}-episode-{number}");
            episodes.Add(new Episode(new EpisodeId($"gogo:{epId}"), title ?? $"Episode {number}", number, pageUrl));
        }

        return episodes;
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var (id, isGogo) = SplitId(episode.Id);
        if (!isGogo)
        {
            throw new InvalidOperationException("GogoAnimeCatalog can only handle gogo-prefixed episode ids.");
        }

        var url = $"{_options.ApiBase.TrimEnd('/')}/anime/gogoanime/watch/{Uri.EscapeDataString(id)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var list = new List<StreamLink>();
        var referrer = json.RootElement.TryGetProperty("referer", out var refProp) ? refProp.GetString() : null;

        if (json.RootElement.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var src in sourcesEl.EnumerateArray())
            {
                var srcUrl = src.GetProperty("url").GetString();
                if (string.IsNullOrWhiteSpace(srcUrl))
                {
                    continue;
                }

                var quality = src.TryGetProperty("quality", out var qProp) ? qProp.GetString() ?? "auto" : "auto";
                if (!Uri.TryCreate(srcUrl, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                list.Add(new StreamLink(uri, quality, "gogoanime", referrer));
            }
        }

        if (list.Count == 0)
        {
            _logger.LogWarning("No streams returned from GogoAnime for episode {Episode}", episode.Id.Value);
        }

        return list;
    }

    private (string id, bool isGogo) SplitId(EpisodeId id) => SplitId(id.Value);
    private (string id, bool isGogo) SplitId(AnimeId id) => SplitId(id.Value);

    private static (string id, bool isGogo) SplitId(string value)
    {
        const string prefix = "gogo:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (value[prefix.Length..], true);
        }

        return (value, false);
    }

    private Uri BuildSiteUrl(string path)
    {
        var trimmed = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : _options.SiteBase.TrimEnd('/') + path;
        return new Uri(trimmed);
    }
}
