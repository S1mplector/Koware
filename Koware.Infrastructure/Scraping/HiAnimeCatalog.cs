// Author: Ilgaz Mehmetoğlu
// HiAnime-compatible provider implementation with MegaCloud source resolution.
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class HiAnimeCatalog : IAnimeCatalog
{
    private readonly HiAnimeLikeCatalogCore _core;

    public HiAnimeCatalog(HttpClient httpClient, IOptions<HiAnimeOptions> options, ILogger<HiAnimeCatalog> logger)
    {
        var value = options.Value;
        _core = new HiAnimeLikeCatalogCore(
            httpClient,
            providerSlug: "hianime",
            providerName: "HiAnime",
            enabled: value.Enabled,
            baseUrl: value.BaseUrl,
            referer: value.EffectiveReferer,
            userAgent: value.UserAgent,
            preferredServer: value.PreferredServer,
            searchLimit: value.SearchLimit,
            logger);
    }

    public bool IsConfigured => _core.IsConfigured;

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => _core.SearchAsync(query, cancellationToken);

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
        => _core.SearchAsync(query, filters, cancellationToken);

    public Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
        => _core.BrowsePopularAsync(filters, cancellationToken);

    public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
        => _core.GetEpisodesAsync(anime, cancellationToken);

    public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
        => _core.GetStreamsAsync(episode, cancellationToken);
}

internal sealed class HiAnimeLikeCatalogCore : IAnimeCatalog
{
    private readonly HttpClient _httpClient;
    private readonly string _providerSlug;
    private readonly string _providerName;
    private readonly bool _enabled;
    private readonly string? _baseUrl;
    private readonly string _referer;
    private readonly string _userAgent;
    private readonly string _preferredServer;
    private readonly int _searchLimit;
    private readonly ILogger _logger;

    private const string MegacloudKeyUrl = "https://raw.githubusercontent.com/yogesh-hacker/MegacloudKeys/refs/heads/main/keys.json";

    private static readonly Regex SearchResultRegex = new(
        "<a\\s+href=\\\\?\"/(?<href>[^\\\\\"#]+)\\\\?\"\\s+class=\\\\?\"nav-item\\\\?\"(?<rest>.*?)<h3\\s+class=\\\\?\"film-name\\\\?\"[^>]*>(?<title>.*?)</h3>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HomeLinkRegex = new(
        "href=\\\\?\"/watch/(?<slug>[a-z0-9\\-]+)\\\\?\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EpisodeItemRegex = new(
        "<a\\s+[^>]*class=\\\\?\"[^\\\\\"]*ep-item[^\\\\\"]*\\\\?\"[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ServerItemRegex = new(
        "<div\\s+class=\\\\?\"item\\s+server-item\\\\?\"[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex AttributeRegex = new(
        "(?<name>[a-zA-Z0-9_\\-:]+)=\\\\?\"(?<value>[^\\\\\"]*)\\\\?\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SourceIdRegex = new(
        "/([^/?]+)\\?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MetaKeyRegex = new(
        "<meta\\s+name=\"_gg_fb\"\\s+content=\"(?<key>[a-zA-Z0-9]+)\">",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsThRegex = new(
        "<!--\\s*_is_th:(?<key>[a-zA-Z0-9]+)\\s*-->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LkDbRegex = new(
        "window\\._lk_db\\s*=\\s*\\{(?<body>[^}]*)\\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LkDbValueRegex = new(
        "[xyz]\\s*:\\s*['\"](?<key>[a-zA-Z0-9]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DpiRegex = new(
        "data-dpi=\"(?<key>[a-zA-Z0-9]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NonceRegex = new(
        "<script\\s+nonce=\"(?<key>[a-zA-Z0-9]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex XyWsRegex = new(
        "window\\._xy_ws\\s*=\\s*['\"`](?<key>[a-zA-Z0-9]+)['\"`]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly SemaphoreSlim KeyLock = new(1, 1);
    private static string? _cachedMegaKey;
    private static DateTimeOffset _cachedMegaKeyAt;

    public HiAnimeLikeCatalogCore(
        HttpClient httpClient,
        string providerSlug,
        string providerName,
        bool enabled,
        string? baseUrl,
        string referer,
        string userAgent,
        string preferredServer,
        int searchLimit,
        ILogger logger)
    {
        _httpClient = httpClient;
        _providerSlug = providerSlug;
        _providerName = providerName;
        _enabled = enabled;
        _baseUrl = baseUrl?.TrimEnd('/');
        _referer = string.IsNullOrWhiteSpace(referer) ? (baseUrl?.TrimEnd('/') ?? "") : referer.TrimEnd('/');
        _userAgent = userAgent;
        _preferredServer = string.IsNullOrWhiteSpace(preferredServer) ? "hd-1" : preferredServer.Trim().ToLowerInvariant();
        _searchLimit = searchLimit > 0 ? searchLimit : 20;
        _logger = logger;
    }

    public bool IsConfigured => _enabled && !string.IsNullOrWhiteSpace(_baseUrl);

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, SearchFilters.Empty, cancellationToken);

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("{Provider} source not configured. Add configuration to ~/.config/koware/appsettings.user.json", _providerName);
            return Array.Empty<Anime>();
        }

        query = query?.Trim() ?? "";
        if (query.Length == 0)
        {
            return await BrowsePopularAsync(filters, cancellationToken);
        }

        var endpoint = $"{_baseUrl}/ajax/search/suggest?keyword={Uri.EscapeDataString(query)}";
        using var request = BuildRequest(endpoint, $"{_baseUrl}/");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("*/*");

        var payload = await SendAsync(request, cancellationToken);
        if (payload == null)
        {
            return Array.Empty<Anime>();
        }

        var html = ExtractJsonStringField(payload, "html") ?? payload;
        var list = ParseSearchResults(html)
            .Take(_searchLimit)
            .ToArray();

        return list;
    }

    public async Task<IReadOnlyCollection<Anime>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<Anime>();
        }

        using var request = BuildRequest($"{_baseUrl}/home", $"{_baseUrl}/");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<Anime>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Anime>();
        foreach (Match match in HomeLinkRegex.Matches(payload))
        {
            var slug = NormalizeAnimeSlug(match.Groups["slug"].Value);
            if (slug.Length == 0 || !seen.Add(slug))
            {
                continue;
            }

            list.Add(new Anime(
                new AnimeId(slug),
                ToTitleFromSlug(slug),
                synopsis: null,
                coverImage: null,
                detailPage: BuildWatchUri(slug),
                episodes: Array.Empty<Episode>()));

            if (list.Count >= _searchLimit)
            {
                break;
            }
        }

        return list;
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<Episode>();
        }

        var slug = NormalizeAnimeSlug(anime.Id.Value);
        if (slug.Length == 0)
        {
            return Array.Empty<Episode>();
        }

        var animeNumericId = ExtractTrailingNumber(slug);
        if (animeNumericId == null)
        {
            _logger.LogWarning("Could not parse anime numeric id from slug '{Slug}' for provider {Provider}", slug, _providerSlug);
            return Array.Empty<Episode>();
        }

        var referer = BuildWatchUri(slug).ToString();
        using var request = BuildRequest($"{_baseUrl}/ajax/v2/episode/list/{animeNumericId}", referer);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var payload = await SendAsync(request, cancellationToken);
        if (payload == null)
        {
            return Array.Empty<Episode>();
        }

        var html = ExtractJsonStringField(payload, "html") ?? payload;
        var episodes = ParseEpisodeItems(html, anime)
            .OrderBy(e => e.Number)
            .ToArray();

        return episodes;
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<StreamLink>();
        }

        var episodeId = ExtractTrailingNumber(episode.Id.Value) ?? ExtractEpisodeIdFromDetailUrl(episode.PageUrl);
        if (episodeId == null)
        {
            _logger.LogWarning("Could not parse episode id '{EpisodeId}' for provider {Provider}", episode.Id.Value, _providerSlug);
            return Array.Empty<StreamLink>();
        }

        var watchReferer = episode.PageUrl.ToString();
        var serverDataId = await ResolveServerDataIdAsync(episodeId.Value.ToString(CultureInfo.InvariantCulture), watchReferer, cancellationToken);
        if (serverDataId == null)
        {
            return Array.Empty<StreamLink>();
        }

        using var sourceRequest = BuildRequest($"{_baseUrl}/ajax/v2/episode/sources?id={serverDataId}", watchReferer);
        sourceRequest.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        sourceRequest.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var sourcePayload = await SendAsync(sourceRequest, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourcePayload))
        {
            return Array.Empty<StreamLink>();
        }

        var streams = await ResolveStreamsFromSourcesPayloadAsync(sourcePayload, watchReferer, cancellationToken);
        return streams;
    }

    private IEnumerable<Anime> ParseSearchResults(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SearchResultRegex.Matches(html))
        {
            var href = HtmlDecode(match.Groups["href"].Value);
            var title = HtmlDecode(StripTags(match.Groups["title"].Value)).Trim();
            var slug = NormalizeAnimeSlug(href);

            if (slug.Length == 0 || !seen.Add(slug))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = ToTitleFromSlug(slug);
            }

            yield return new Anime(
                new AnimeId(slug),
                title,
                synopsis: null,
                coverImage: null,
                detailPage: BuildWatchUri(slug),
                episodes: Array.Empty<Episode>());
        }
    }

    private IEnumerable<Episode> ParseEpisodeItems(string html, Anime anime)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var normalized = UnescapeQuotedJsonString(html);
        foreach (Match match in EpisodeItemRegex.Matches(normalized))
        {
            var attrs = ParseAttributes(match.Value);
            if (!attrs.TryGetValue("data-id", out var episodeDataId) || string.IsNullOrWhiteSpace(episodeDataId))
            {
                continue;
            }

            if (!attrs.TryGetValue("data-number", out var rawEpisodeNumber) ||
                !int.TryParse(rawEpisodeNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
                number <= 0)
            {
                continue;
            }

            attrs.TryGetValue("href", out var href);
            attrs.TryGetValue("title", out var titleAttr);

            var page = BuildEpisodeUriFromHref(href, anime, number, episodeDataId);
            var title = string.IsNullOrWhiteSpace(titleAttr)
                ? $"Episode {number}"
                : HtmlDecode(titleAttr.Trim());

            yield return new Episode(
                new EpisodeId($"{episodeDataId}"),
                title,
                number,
                page);
        }
    }

    private async Task<string?> ResolveServerDataIdAsync(string episodeId, string watchReferer, CancellationToken cancellationToken)
    {
        using var request = BuildRequest($"{_baseUrl}/ajax/v2/episode/servers?episodeId={episodeId}", watchReferer);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var payload = await SendAsync(request, cancellationToken);
        if (payload == null)
        {
            return null;
        }

        var html = ExtractJsonStringField(payload, "html") ?? payload;
        var normalized = UnescapeQuotedJsonString(html);
        var servers = ParseServerItems(normalized);
        if (servers.Count == 0)
        {
            return null;
        }

        var preferred = SelectPreferredServer(servers);
        return preferred?.DataId;
    }

    private async Task<IReadOnlyCollection<StreamLink>> ResolveStreamsFromSourcesPayloadAsync(string sourcePayload, string watchReferer, CancellationToken cancellationToken)
    {
        var streams = new List<StreamLink>();

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(sourcePayload);
        }
        catch (JsonException)
        {
            return streams;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var subtitles = ParseSubtitles(root);

            if (root.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array)
            {
                AddSourcesFromArray(sourcesElement, subtitles, streams, watchReferer);
            }

            if (streams.Count > 0)
            {
                return streams;
            }

            var link = root.TryGetProperty("link", out var linkElement) && linkElement.ValueKind == JsonValueKind.String
                ? linkElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(link))
            {
                return streams;
            }

            if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri))
            {
                return streams;
            }

            if (linkUri.Host.Contains("megacloud", StringComparison.OrdinalIgnoreCase))
            {
                var megaStreams = await ResolveMegaCloudSourcesAsync(linkUri, watchReferer, cancellationToken);
                if (megaStreams.Count > 0)
                {
                    return megaStreams;
                }
            }

            streams.Add(new StreamLink(
                linkUri,
                "auto",
                _providerSlug,
                watchReferer,
                subtitles,
                subtitles.Count > 0,
                ComputeHostPriority(linkUri),
                _providerSlug));
        }

        return streams;
    }

    private void AddSourcesFromArray(
        JsonElement sourcesElement,
        IReadOnlyList<SubtitleTrack> subtitles,
        List<StreamLink> target,
        string fallbackReferer)
    {
        foreach (var source in sourcesElement.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var file = GetString(source, "file") ?? GetString(source, "url");
            if (string.IsNullOrWhiteSpace(file) || !Uri.TryCreate(file, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var type = GetString(source, "type");
            var label = GetString(source, "label");
            var quality = GuessQuality(file, type, label);

            target.Add(new StreamLink(
                uri,
                quality,
                _providerSlug,
                fallbackReferer,
                subtitles,
                subtitles.Count > 0,
                ComputeHostPriority(uri),
                _providerSlug));
        }
    }

    private async Task<IReadOnlyCollection<StreamLink>> ResolveMegaCloudSourcesAsync(Uri embedUri, string watchReferer, CancellationToken cancellationToken)
    {
        var streams = new List<StreamLink>();
        var match = SourceIdRegex.Match(embedUri.ToString());
        if (!match.Success)
        {
            _logger.LogDebug("Could not extract MegaCloud source id from '{Uri}'", embedUri);
            return streams;
        }

        var sourceId = match.Groups[1].Value;
        var clientKey = await GetMegaCloudClientKeyAsync(sourceId, watchReferer, cancellationToken);
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            return streams;
        }

        var sourceUrl = $"https://megacloud.blog/embed-2/v3/e-1/getSources?id={sourceId}&_k={Uri.EscapeDataString(clientKey)}";
        using var request = BuildRequest(sourceUrl, embedUri.ToString());
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return streams;
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "MegaCloud payload was not valid JSON.");
            return streams;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var subtitles = ParseSubtitles(root);

            if (root.TryGetProperty("encrypted", out var encryptedElement) &&
                encryptedElement.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("sources", out var clearSources) &&
                clearSources.ValueKind == JsonValueKind.Array)
            {
                AddSourcesFromArray(clearSources, subtitles, streams, $"{embedUri.Scheme}://{embedUri.Host}/");
                return streams;
            }

            var encrypted = root.TryGetProperty("sources", out var encryptedSourceElement) &&
                            encryptedSourceElement.ValueKind == JsonValueKind.String
                ? encryptedSourceElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(encrypted))
            {
                return streams;
            }

            var megaKey = await GetMegaCloudKeyAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(megaKey))
            {
                _logger.LogWarning("Could not fetch MegaCloud decrypt key; skipping encrypted stream decode.");
                return streams;
            }

            var decryptedJson = DecryptMegaCloudSources(encrypted!, clientKey, megaKey);
            if (string.IsNullOrWhiteSpace(decryptedJson))
            {
                return streams;
            }

            JsonDocument? decodedDoc = null;
            try
            {
                decodedDoc = JsonDocument.Parse(decryptedJson);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse decrypted MegaCloud source JSON.");
                return streams;
            }

            using (decodedDoc)
            {
                if (decodedDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    AddSourcesFromArray(decodedDoc.RootElement, subtitles, streams, $"{embedUri.Scheme}://{embedUri.Host}/");
                }
            }
        }

        return streams;
    }

    private async Task<string?> GetMegaCloudClientKeyAsync(string sourceId, string watchReferer, CancellationToken cancellationToken)
    {
        var embedPageUrl = $"https://megacloud.blog/embed-2/v3/e-1/{sourceId}";
        using var request = BuildRequest(embedPageUrl, watchReferer);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var html = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var lkDbMatch = LkDbRegex.Match(html);
        if (lkDbMatch.Success)
        {
            var values = LkDbValueRegex.Matches(lkDbMatch.Groups["body"].Value)
                .Select(m => m.Groups["key"].Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(3)
                .ToArray();

            if (values.Length == 3)
            {
                return string.Concat(values);
            }
        }

        var fromMeta = TryGroup(MetaKeyRegex, html);
        if (!string.IsNullOrWhiteSpace(fromMeta))
        {
            return fromMeta;
        }

        var fromIsTh = TryGroup(IsThRegex, html);
        if (!string.IsNullOrWhiteSpace(fromIsTh))
        {
            return fromIsTh;
        }

        var fromDpi = TryGroup(DpiRegex, html);
        if (!string.IsNullOrWhiteSpace(fromDpi))
        {
            return fromDpi;
        }

        var fromNonce = TryGroup(NonceRegex, html);
        if (!string.IsNullOrWhiteSpace(fromNonce))
        {
            return fromNonce;
        }

        var fromXyWs = TryGroup(XyWsRegex, html);
        if (!string.IsNullOrWhiteSpace(fromXyWs))
        {
            return fromXyWs;
        }

        return null;
    }

    private static string? TryGroup(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups["key"].Value : null;
    }

    private async Task<string?> GetMegaCloudKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedMegaKey) &&
            DateTimeOffset.UtcNow - _cachedMegaKeyAt < TimeSpan.FromHours(6))
        {
            return _cachedMegaKey;
        }

        await KeyLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedMegaKey) &&
                DateTimeOffset.UtcNow - _cachedMegaKeyAt < TimeSpan.FromHours(6))
            {
                return _cachedMegaKey;
            }

            using var request = BuildRequest(MegacloudKeyUrl, _referer);
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

            var payload = await SendAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return _cachedMegaKey;
            }

            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("mega", out var mega) && mega.ValueKind == JsonValueKind.String)
            {
                var key = mega.GetString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _cachedMegaKey = key;
                    _cachedMegaKeyAt = DateTimeOffset.UtcNow;
                    return _cachedMegaKey;
                }
            }

            return _cachedMegaKey;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch MegaCloud key.");
            return _cachedMegaKey;
        }
        finally
        {
            KeyLock.Release();
        }
    }

    private static string DecryptMegaCloudSources(string src, string clientKey, string megacloudKey)
    {
        const int layers = 3;
        var generatedKey = Keygen(megacloudKey, clientKey);
        var decodedBytes = Convert.FromBase64String(src);
        var decSrc = Encoding.UTF8.GetString(decodedBytes);
        var printableChars = Enumerable.Range(32, 95).Select(i => (char)i).ToArray();

        for (var iteration = layers; iteration > 0; iteration--)
        {
            var layerKey = generatedKey + iteration.ToString(CultureInfo.InvariantCulture);
            var seed = Compute32BitHash(layerKey);
            decSrc = ApplyRandomShift(decSrc, printableChars, ref seed);
            decSrc = ColumnarCipher(decSrc, layerKey);

            var shuffled = SeedShuffle(printableChars, layerKey);
            var map = new Dictionary<char, char>(shuffled.Length);
            for (var i = 0; i < shuffled.Length; i++)
            {
                map[shuffled[i]] = printableChars[i];
            }

            var chars = decSrc.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (map.TryGetValue(chars[i], out var remapped))
                {
                    chars[i] = remapped;
                }
            }
            decSrc = new string(chars);
        }

        if (decSrc.Length < 4 || !int.TryParse(decSrc[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dataLength))
        {
            return string.Empty;
        }

        if (dataLength < 0 || 4 + dataLength > decSrc.Length)
        {
            return string.Empty;
        }

        return decSrc.Substring(4, dataLength);
    }

    private static string ApplyRandomShift(string input, char[] printableChars, ref BigInteger seed)
    {
        var output = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            var idx = Array.IndexOf(printableChars, ch);
            if (idx < 0)
            {
                output.Append(ch);
                continue;
            }

            var rand = SeedRand(ref seed, 95);
            var newIndex = (idx - rand + 95) % 95;
            output.Append(printableChars[newIndex]);
        }
        return output.ToString();
    }

    private static int SeedRand(ref BigInteger seed, int modulo)
    {
        seed = ((seed * 1103515245) + 12345) & 0x7fffffff;
        return (int)(seed % modulo);
    }

    private static BigInteger Compute32BitHash(string input)
    {
        BigInteger hash = 0;
        foreach (var ch in input)
        {
            hash = ((hash * 31) + ch) & 0xffffffff;
        }
        return hash;
    }

    private static char[] SeedShuffle(char[] chars, string key)
    {
        var result = chars.ToArray();
        var seed = Compute32BitHash(key);
        for (var i = result.Length - 1; i > 0; i--)
        {
            var swapIndex = SeedRand(ref seed, i + 1);
            (result[i], result[swapIndex]) = (result[swapIndex], result[i]);
        }
        return result;
    }

    private static string ColumnarCipher(string src, string key)
    {
        var columnCount = key.Length;
        if (columnCount <= 0)
        {
            return src;
        }

        var rowCount = (int)Math.Ceiling(src.Length / (double)columnCount);
        var cipher = new char[rowCount, columnCount];
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < columnCount; c++)
            {
                cipher[r, c] = ' ';
            }
        }

        var keyMap = key
            .Select((ch, idx) => (ch, idx))
            .OrderBy(x => x.ch)
            .ThenBy(x => x.idx)
            .ToArray();

        var srcIndex = 0;
        foreach (var (_, index) in keyMap)
        {
            for (var row = 0; row < rowCount; row++)
            {
                cipher[row, index] = srcIndex < src.Length ? src[srcIndex++] : ' ';
            }
        }

        var output = new StringBuilder(rowCount * columnCount);
        for (var row = 0; row < rowCount; row++)
        {
            for (var col = 0; col < columnCount; col++)
            {
                output.Append(cipher[row, col]);
            }
        }

        return output.ToString();
    }

    private static string Keygen(string megacloudKey, string clientKey)
    {
        BigInteger hash = 0;
        var tempKey = megacloudKey + clientKey;
        foreach (var ch in tempKey)
        {
            hash = ch + hash * 31 + (hash << 7) - hash;
        }

        if (hash < 0)
        {
            hash = BigInteger.Negate(hash);
        }

        var lHash = (long)(hash % 0x7fffffffffffffffL);

        var obfuscated = new string(tempKey.Select(c => (char)(c ^ 247)).ToArray());
        var pivot = (int)(lHash % obfuscated.Length + 5);
        obfuscated = obfuscated[pivot..] + obfuscated[..pivot];

        var reversedClientKey = new string(clientKey.Reverse().ToArray());
        var maxLength = Math.Max(obfuscated.Length, reversedClientKey.Length);
        var merged = new StringBuilder(maxLength * 2);
        for (var i = 0; i < maxLength; i++)
        {
            if (i < obfuscated.Length)
            {
                merged.Append(obfuscated[i]);
            }
            if (i < reversedClientKey.Length)
            {
                merged.Append(reversedClientKey[i]);
            }
        }

        var take = Math.Min(merged.Length, 96 + (int)(lHash % 33));
        var normalized = new string(merged
            .ToString(0, take)
            .Select(c => (char)(c % 95 + 32))
            .ToArray());

        return normalized;
    }

    private IReadOnlyList<SubtitleTrack> ParseSubtitles(JsonElement root)
    {
        if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SubtitleTrack>();
        }

        var subtitles = new List<SubtitleTrack>();
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var file = GetString(track, "file") ?? GetString(track, "url");
            if (string.IsNullOrWhiteSpace(file) || !Uri.TryCreate(file, UriKind.Absolute, out var fileUri))
            {
                continue;
            }

            var label = GetString(track, "label") ?? GetString(track, "kind") ?? "Subtitle";
            var lang = GetString(track, "language") ?? GetString(track, "srclang") ?? "und";
            subtitles.Add(new SubtitleTrack(label, fileUri, lang));
        }

        return subtitles;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    private static string GuessQuality(string file, string? type, string? label)
    {
        var probe = $"{file} {type} {label}";
        if (probe.Contains("1080", StringComparison.OrdinalIgnoreCase)) return "1080p";
        if (probe.Contains("720", StringComparison.OrdinalIgnoreCase)) return "720p";
        if (probe.Contains("480", StringComparison.OrdinalIgnoreCase)) return "480p";
        if (probe.Contains("360", StringComparison.OrdinalIgnoreCase)) return "360p";
        if (!string.IsNullOrWhiteSpace(type) && type.Contains("hls", StringComparison.OrdinalIgnoreCase)) return "auto";
        if (file.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return "auto";
        return "auto";
    }

    private ServerEntry? SelectPreferredServer(IReadOnlyList<ServerEntry> servers)
    {
        var preferredServerId = _preferredServer switch
        {
            "hd-1" => 4,
            "hd-2" => 1,
            "hd-3" => 6,
            _ => (int?)null
        };

        var preferred = servers.FirstOrDefault(s =>
            s.Type.Equals("sub", StringComparison.OrdinalIgnoreCase) &&
            ((preferredServerId.HasValue && s.ServerId == preferredServerId.Value) ||
             s.Label.Equals(_preferredServer, StringComparison.OrdinalIgnoreCase)));

        if (preferred != null)
        {
            return preferred;
        }

        preferred = servers.FirstOrDefault(s => s.Type.Equals("sub", StringComparison.OrdinalIgnoreCase));
        if (preferred != null)
        {
            return preferred;
        }

        return servers.FirstOrDefault();
    }

    private List<ServerEntry> ParseServerItems(string html)
    {
        var list = new List<ServerEntry>();
        foreach (Match match in ServerItemRegex.Matches(html))
        {
            var attrs = ParseAttributes(match.Value);
            if (!attrs.TryGetValue("data-id", out var dataId) || string.IsNullOrWhiteSpace(dataId))
            {
                continue;
            }

            var type = attrs.TryGetValue("data-type", out var dataType) && !string.IsNullOrWhiteSpace(dataType)
                ? dataType
                : "sub";
            _ = attrs.TryGetValue("data-server-id", out var serverIdRaw);
            var serverId = int.TryParse(serverIdRaw, out var parsedId) ? parsedId : 0;
            var label = ParseServerLabel(match.Value);

            list.Add(new ServerEntry(dataId, type.ToLowerInvariant(), serverId, label));
        }
        return list;
    }

    private static string ParseServerLabel(string htmlSnippet)
    {
        var labelMatch = Regex.Match(htmlSnippet, "<a[^>]*>(?<label>[^<]+)</a>", RegexOptions.IgnoreCase);
        if (!labelMatch.Success)
        {
            return string.Empty;
        }

        return HtmlDecode(labelMatch.Groups["label"].Value).Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseAttributes(string htmlTag)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(htmlTag))
        {
            var name = match.Groups["name"].Value;
            var value = HtmlDecode(match.Groups["value"].Value);
            dict[name] = value;
        }
        return dict;
    }

    private Uri BuildEpisodeUriFromHref(string? href, Anime anime, int number, string episodeDataId)
    {
        if (!string.IsNullOrWhiteSpace(href))
        {
            var normalized = UnescapeQuotedJsonString(href.Trim());
            if (normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
            {
                return absolute;
            }

            if (normalized.StartsWith("/"))
            {
                if (normalized.StartsWith("/watch/", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri($"{_baseUrl}{normalized}");
                }

                return new Uri($"{_baseUrl}/watch{normalized}");
            }
        }

        return new Uri($"{_baseUrl}/watch/{NormalizeAnimeSlug(anime.Id.Value)}?ep={episodeDataId}");
    }

    private int? ExtractEpisodeIdFromDetailUrl(Uri detailPage)
    {
        var query = detailPage.Query;
        var match = Regex.Match(query, @"[?&]ep=(?<ep>\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["ep"].Value, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static int? ExtractTrailingNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"(?<n>\d+)(?!.*\d)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private string NormalizeAnimeSlug(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = UnescapeQuotedJsonString(raw.Trim())
            .TrimStart('/')
            .Trim();

        if (cleaned.StartsWith("watch/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["watch/".Length..];
        }

        var queryIndex = cleaned.IndexOf('?');
        if (queryIndex >= 0)
        {
            cleaned = cleaned[..queryIndex];
        }

        return cleaned;
    }

    private Uri BuildWatchUri(string slug)
    {
        return new Uri($"{_baseUrl}/watch/{slug}");
    }

    private static string HtmlDecode(string input) => System.Net.WebUtility.HtmlDecode(input);

    private static string StripTags(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return Regex.Replace(input, "<.*?>", string.Empty, RegexOptions.Singleline);
    }

    private static string ToTitleFromSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Unknown";
        }

        var title = slug.Replace('-', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant());
    }

    private static string UnescapeQuotedJsonString(string value)
    {
        return value
            .Replace("\\/", "/")
            .Replace("\\\"", "\"")
            .Replace("\\u003C", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u003E", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("\\n", "\n");
    }

    private static string? ExtractJsonStringField(string payload, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty(fieldName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore invalid payloads and fall back to raw text.
        }

        return null;
    }

    private HttpRequestMessage BuildRequest(string url, string referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            request.Headers.Referrer = refererUri;
            request.Headers.TryAddWithoutValidation("Origin", $"{refererUri.Scheme}://{refererUri.Host}");
        }

        if (!string.IsNullOrWhiteSpace(_userAgent))
        {
            if (!request.Headers.UserAgent.TryParseAdd(_userAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
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
                _logger.LogDebug("{Provider} request to {Url} failed with HTTP {Status}", _providerSlug, request.RequestUri, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Provider} request failed for {Url}", _providerSlug, request.RequestUri);
            return null;
        }
    }

    private static int ComputeHostPriority(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("lightning", StringComparison.Ordinal) || host.Contains("megacloud", StringComparison.Ordinal))
        {
            return 8;
        }

        if (host.Contains("m3u8", StringComparison.Ordinal) || host.Contains("hls", StringComparison.Ordinal))
        {
            return 4;
        }

        return 0;
    }

    private sealed record ServerEntry(string DataId, string Type, int ServerId, string Label);
}
