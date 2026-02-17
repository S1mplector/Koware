// Author: Ilgaz Mehmetoğlu
// Hanime provider implementation using search index + page manifest scraping.
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class HanimeCatalog : IAnimeCatalog
{
    private readonly HttpClient _httpClient;
    private readonly HanimeOptions _options;
    private readonly ILogger<HanimeCatalog> _logger;

    private static readonly SemaphoreSlim SearchIndexLock = new(1, 1);
    private static List<HanimeSearchEntry>? _cachedSearchIndex;
    private static DateTimeOffset _cachedSearchIndexAt;

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ManifestStreamObjectRegex = new("\\{[^{}]*url:\"(?<url>[^\"]+)\"[^{}]*\\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeightRegex = new("height:\"?(?<height>\\d{3,4})\"?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KindRegex = new("kind:\"(?<kind>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public HanimeCatalog(HttpClient httpClient, IOptions<HanimeOptions> options, ILogger<HanimeCatalog> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, SearchFilters.Empty, cancellationToken);

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Hanime source not configured. Add configuration to ~/.config/koware/appsettings.user.json");
            return Array.Empty<Anime>();
        }

        var index = await GetSearchIndexAsync(cancellationToken);
        if (index.Count == 0)
        {
            return Array.Empty<Anime>();
        }

        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return index
                .OrderByDescending(e => e.Views)
                .Take(_options.SearchLimit > 0 ? _options.SearchLimit : 20)
                .Select(ToAnime)
                .ToArray();
        }

        var normalizedQuery = trimmed.ToLowerInvariant();
        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var results = index
            .Select(entry => new { entry, score = Score(entry, normalizedQuery, tokens) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.entry.Views)
            .Take(_options.SearchLimit > 0 ? _options.SearchLimit : 20)
            .Select(x => ToAnime(x.entry))
            .ToArray();

        return results;
    }

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<Anime>();
        }

        var index = await GetSearchIndexAsync(cancellationToken);
        return index
            .OrderByDescending(e => e.Views)
            .Take(_options.SearchLimit > 0 ? _options.SearchLimit : 20)
            .Select(ToAnime)
            .ToArray();
    }

    public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Task.FromResult<IReadOnlyCollection<Episode>>(Array.Empty<Episode>());
        }

        var slug = NormalizeSlug(anime.Id.Value, anime.DetailPage);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Task.FromResult<IReadOnlyCollection<Episode>>(Array.Empty<Episode>());
        }

        var pageUrl = BuildVideoUri(slug);
        var episode = new Episode(new EpisodeId(slug), "Episode 1", 1, pageUrl);

        return Task.FromResult<IReadOnlyCollection<Episode>>(new[] { episode });
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<StreamLink>();
        }

        var slug = NormalizeSlug(episode.Id.Value, episode.PageUrl);
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.LogWarning("Could not parse Hanime slug from episode id '{EpisodeId}'", episode.Id.Value);
            return Array.Empty<StreamLink>();
        }

        var detailPage = BuildVideoUri(slug);
        using var request = BuildRequest(detailPage.ToString(), _options.EffectiveReferer);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var html = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<StreamLink>();
        }

        var manifestSegment = ExtractManifestSegment(html);
        if (manifestSegment == null)
        {
            _logger.LogDebug("Hanime manifest section not found for {Url}", detailPage);
            return Array.Empty<StreamLink>();
        }

        var streams = new List<StreamLink>();
        foreach (Match match in ManifestStreamObjectRegex.Matches(manifestSegment))
        {
            var streamObject = match.Value;
            var rawUrl = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                continue;
            }

            var decodedUrl = DecodeJsEscapes(rawUrl);
            if (!Uri.TryCreate(decodedUrl, UriKind.Absolute, out var streamUri))
            {
                continue;
            }

            var quality = HeightRegex.Match(streamObject) is var heightMatch &&
                          heightMatch.Success &&
                          int.TryParse(heightMatch.Groups["height"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                ? $"{height}p"
                : "auto";

            if (quality == "auto" &&
                KindRegex.Match(streamObject) is var kindMatch &&
                kindMatch.Success &&
                kindMatch.Groups["kind"].Value.Contains("hls", StringComparison.OrdinalIgnoreCase))
            {
                quality = "auto";
            }

            streams.Add(new StreamLink(
                streamUri,
                quality,
                "hanime",
                detailPage.ToString(),
                Subtitles: null,
                RequiresSoftSubSupport: false,
                HostPriority: 7,
                SourceTag: "hanime"));
        }

        return streams
            .GroupBy(s => s.Url)
            .Select(g => g.First())
            .ToArray();
    }

    private Anime ToAnime(HanimeSearchEntry entry)
    {
        Uri? cover = null;
        if (!string.IsNullOrWhiteSpace(entry.CoverUrl) && Uri.TryCreate(entry.CoverUrl, UriKind.Absolute, out var coverUri))
        {
            cover = coverUri;
        }

        var detailPage = BuildVideoUri(entry.Slug);
        return new Anime(
            new AnimeId(entry.Slug),
            entry.Name,
            synopsis: entry.Description,
            coverImage: cover,
            detailPage: detailPage,
            episodes: Array.Empty<Episode>());
    }

    private async Task<IReadOnlyList<HanimeSearchEntry>> GetSearchIndexAsync(CancellationToken cancellationToken)
    {
        if (_cachedSearchIndex is not null &&
            DateTimeOffset.UtcNow - _cachedSearchIndexAt < TimeSpan.FromHours(2))
        {
            return _cachedSearchIndex;
        }

        await SearchIndexLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSearchIndex is not null &&
                DateTimeOffset.UtcNow - _cachedSearchIndexAt < TimeSpan.FromHours(2))
            {
                return _cachedSearchIndex;
            }

            using var request = BuildRequest(_options.EffectiveSearchApiUrl, _options.EffectiveReferer);
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

            var payload = await SendAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return _cachedSearchIndex ?? (IReadOnlyList<HanimeSearchEntry>)Array.Empty<HanimeSearchEntry>();
            }

            var parsed = ParseSearchIndex(payload);
            if (parsed.Count > 0)
            {
                _cachedSearchIndex = parsed;
                _cachedSearchIndexAt = DateTimeOffset.UtcNow;
                return _cachedSearchIndex;
            }

            return _cachedSearchIndex ?? (IReadOnlyList<HanimeSearchEntry>)Array.Empty<HanimeSearchEntry>();
        }
        finally
        {
            SearchIndexLock.Release();
        }
    }

    private List<HanimeSearchEntry> ParseSearchIndex(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new List<HanimeSearchEntry>();
            }

            var list = new List<HanimeSearchEntry>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var slug = GetString(item, "slug");
                var name = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var searchTitles = GetString(item, "search_titles") ?? string.Empty;
                var description = StripTags(GetString(item, "description") ?? string.Empty);
                var coverUrl = GetString(item, "cover_url");
                var views = GetInt(item, "views");

                list.Add(new HanimeSearchEntry(
                    name.Trim(),
                    slug.Trim(),
                    searchTitles,
                    description,
                    coverUrl,
                    views));
            }

            return list;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Hanime search index payload was not valid JSON.");
            return new List<HanimeSearchEntry>();
        }
    }

    private static int Score(HanimeSearchEntry entry, string normalizedQuery, IReadOnlyList<string> tokens)
    {
        var name = entry.NameLower;
        var alt = entry.SearchTitlesLower;
        var desc = entry.DescriptionLower;

        var score = 0;

        if (name.Equals(normalizedQuery, StringComparison.Ordinal)) score += 1000;
        if (name.StartsWith(normalizedQuery, StringComparison.Ordinal)) score += 700;
        if (name.Contains(normalizedQuery, StringComparison.Ordinal)) score += 450;
        if (alt.Contains(normalizedQuery, StringComparison.Ordinal)) score += 240;
        if (desc.Contains(normalizedQuery, StringComparison.Ordinal)) score += 120;

        foreach (var token in tokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            if (name.Contains(token, StringComparison.Ordinal)) score += 130;
            else if (alt.Contains(token, StringComparison.Ordinal)) score += 80;
            else if (desc.Contains(token, StringComparison.Ordinal)) score += 25;
        }

        return score;
    }

    private Uri BuildVideoUri(string slug)
    {
        return new Uri($"{_options.BaseUrl!.TrimEnd('/')}/videos/hentai/{slug}", UriKind.Absolute);
    }

    private static string NormalizeSlug(string rawId, Uri? pageUri)
    {
        if (!string.IsNullOrWhiteSpace(rawId))
        {
            var candidate = rawId.Trim();
            if (!candidate.Contains('/'))
            {
                return candidate;
            }

            var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.FindIndex(segments, s => s.Equals("hentai", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < segments.Length)
            {
                return segments[idx + 1];
            }
        }

        if (pageUri is not null)
        {
            var segments = pageUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.FindIndex(segments, s => s.Equals("hentai", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < segments.Length)
            {
                return segments[idx + 1];
            }
        }

        return string.Empty;
    }

    private static string? ExtractManifestSegment(string html)
    {
        var start = html.IndexOf("videos_manifest:{", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = html.IndexOf("},user_license", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = html.IndexOf(",user_license", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                return null;
            }
            return html[start..end];
        }

        return html[start..(end + 1)];
    }

    private static string DecodeJsEscapes(string value)
    {
        var decodedUnicode = Regex.Replace(value, "\\\\u([0-9A-Fa-f]{4})", m =>
        {
            var codePoint = Convert.ToInt32(m.Groups[1].Value, 16);
            return ((char)codePoint).ToString();
        });

        return decodedUnicode
            .Replace("\\/", "/")
            .Replace("\\\"", "\"");
    }

    private static string StripTags(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return HtmlTagRegex.Replace(value, " ")
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private HttpRequestMessage BuildRequest(string url, string referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            request.Headers.Referrer = refererUri;
            request.Headers.TryAddWithoutValidation("Origin", $"{refererUri.Scheme}://{refererUri.Host}");
        }

        if (!string.IsNullOrWhiteSpace(_options.UserAgent))
        {
            if (!request.Headers.UserAgent.TryParseAdd(_options.UserAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            }
        }

        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return request;
    }

    private async Task<string?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Hanime request to {Url} failed with HTTP {Status}", request.RequestUri, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hanime request failed for {Url}", request.RequestUri);
            return null;
        }
    }

    private sealed record HanimeSearchEntry(
        string Name,
        string Slug,
        string SearchTitles,
        string Description,
        string? CoverUrl,
        int Views)
    {
        public string NameLower { get; } = Name.ToLowerInvariant();
        public string SearchTitlesLower { get; } = SearchTitles.ToLowerInvariant();
        public string DescriptionLower { get; } = Description.ToLowerInvariant();
    }
}
