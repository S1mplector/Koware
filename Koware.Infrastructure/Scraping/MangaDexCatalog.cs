// Author: Ilgaz Mehmetoğlu
// MangaDex provider implementation using the official public API.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class MangaDexCatalog : IMangaCatalog
{
    private static readonly Regex UuidRegex = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly MangaDexOptions _options;
    private readonly ILogger<MangaDexCatalog> _logger;

    public MangaDexCatalog(HttpClient httpClient, IOptions<MangaDexOptions> options, ILogger<MangaDexCatalog> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, SearchFilters.Empty, cancellationToken);

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("MangaDex source not configured. Add configuration to ~/.config/koware/appsettings.user.json");
            return Array.Empty<Manga>();
        }

        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return await BrowsePopularAsync(filters, cancellationToken);
        }

        var searchLimit = NormalizeSearchLimit(_options.SearchLimit);
        var endpoint = BuildApiUrl(
            "/manga",
            [
                new("title", trimmed),
                new("limit", searchLimit.ToString(CultureInfo.InvariantCulture)),
                new("offset", "0"),
                new("includes[]", "cover_art"),
                new("order[relevance]", "desc")
            ],
            languageFilterKey: "availableTranslatedLanguage[]",
            includeContentRatings: true);

        using var request = BuildRequest(endpoint);
        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<Manga>();
        }

        return ParseMangaCollection(payload, searchLimit);
    }

    public async Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<Manga>();
        }

        var searchLimit = NormalizeSearchLimit(_options.SearchLimit);
        var endpoint = BuildApiUrl(
            "/manga",
            [
                new("limit", searchLimit.ToString(CultureInfo.InvariantCulture)),
                new("offset", "0"),
                new("includes[]", "cover_art"),
                new("order[followedCount]", "desc")
            ],
            languageFilterKey: "availableTranslatedLanguage[]",
            includeContentRatings: true);

        using var request = BuildRequest(endpoint);
        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<Manga>();
        }

        return ParseMangaCollection(payload, searchLimit);
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<Chapter>();
        }

        var mangaId = ExtractUuid(manga.Id.Value) ?? ExtractUuid(manga.DetailPage.ToString());
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            _logger.LogWarning("Could not parse MangaDex manga id from '{MangaId}'.", manga.Id.Value);
            return Array.Empty<Chapter>();
        }

        const int maxPageSize = 100;
        var maxChapters = NormalizeChapterLimit(_options.MaxChapterCount);
        var fallbackNumber = 1f;
        var offset = 0;
        var total = int.MaxValue;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chapters = new List<Chapter>();

        while (chapters.Count < maxChapters && offset < total)
        {
            var requestSize = Math.Min(maxPageSize, maxChapters - chapters.Count);
            var endpoint = BuildApiUrl(
                $"/manga/{mangaId}/feed",
                [
                    new("limit", requestSize.ToString(CultureInfo.InvariantCulture)),
                    new("offset", offset.ToString(CultureInfo.InvariantCulture)),
                    new("order[volume]", "asc"),
                    new("order[chapter]", "asc"),
                    new("includeFuturePublishAt", "0"),
                    new("includeEmptyPages", "0"),
                    new("includeExternalUrl", "0")
                ],
                languageFilterKey: "translatedLanguage[]",
                includeContentRatings: false);

            using var request = BuildRequest(endpoint);
            var payload = await SendAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                break;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var parsedTotal) && parsedTotal >= 0)
            {
                total = parsedTotal;
            }

            if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var parsedInBatch = 0;
            foreach (var item in dataElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var chapter = ParseChapter(item, ref fallbackNumber);
                if (chapter is null || !seen.Add(chapter.Id.Value))
                {
                    continue;
                }

                chapters.Add(chapter);
                parsedInBatch++;

                if (chapters.Count >= maxChapters)
                {
                    break;
                }
            }

            if (root.TryGetProperty("offset", out var offsetElement) &&
                offsetElement.TryGetInt32(out var parsedOffset) &&
                root.TryGetProperty("limit", out var limitElement) &&
                limitElement.TryGetInt32(out var parsedLimit))
            {
                offset = parsedOffset + parsedLimit;
            }
            else
            {
                offset += Math.Max(parsedInBatch, requestSize);
            }

            if (parsedInBatch == 0 || parsedInBatch < requestSize)
            {
                break;
            }
        }

        return chapters.OrderBy(c => c.Number).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<ChapterPage>();
        }

        var chapterId = ExtractUuid(chapter.Id.Value) ?? ExtractUuid(chapter.PageUrl.ToString());
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            _logger.LogWarning("Could not parse MangaDex chapter id from '{ChapterId}'.", chapter.Id.Value);
            return Array.Empty<ChapterPage>();
        }

        var endpoint = BuildApiUrl($"/at-home/server/{chapterId}");
        using var request = BuildRequest(endpoint);
        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<ChapterPage>();
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("baseUrl", out var baseUrlElement) || baseUrlElement.ValueKind != JsonValueKind.String)
            {
                return Array.Empty<ChapterPage>();
            }

            if (!root.TryGetProperty("chapter", out var chapterElement) || chapterElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<ChapterPage>();
            }

            var baseUrl = baseUrlElement.GetString();
            var hash = TryGetString(chapterElement, "hash");
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(hash))
            {
                return Array.Empty<ChapterPage>();
            }

            var preferDataSaver = _options.UseDataSaver;
            var pageArray = GetArray(chapterElement, preferDataSaver ? "dataSaver" : "data");
            var pathSegment = preferDataSaver ? "data-saver" : "data";

            if (pageArray is null || pageArray.Value.ValueKind != JsonValueKind.Array)
            {
                pageArray = GetArray(chapterElement, preferDataSaver ? "data" : "dataSaver");
                pathSegment = preferDataSaver ? "data" : "data-saver";
            }

            if (pageArray is null || pageArray.Value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ChapterPage>();
            }

            var pages = new List<ChapterPage>();
            var pageNumber = 1;

            foreach (var fileElement in pageArray.Value.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var fileName = fileElement.GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var imageUrl = $"{baseUrl.TrimEnd('/')}/{pathSegment}/{hash}/{fileName}";
                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
                {
                    continue;
                }

                pages.Add(new ChapterPage(pageNumber, imageUri, _options.Referer));
                pageNumber++;
            }

            return pages;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "MangaDex at-home payload was not valid JSON.");
            return Array.Empty<ChapterPage>();
        }
    }

    private IReadOnlyCollection<Manga> ParseMangaCollection(string payload, int limit)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "MangaDex search payload was not valid JSON.");
            return Array.Empty<Manga>();
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Manga>();
            }

            var results = new List<Manga>();
            foreach (var item in dataElement.EnumerateArray())
            {
                var manga = ParseManga(item);
                if (manga is null)
                {
                    continue;
                }

                results.Add(manga);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            return results;
        }
    }

    private Manga? ParseManga(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = TryGetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = ResolveTitle(attributes, id);
        var synopsis = ResolveDescription(attributes);
        var detailPage = new Uri($"{_options.WebBase!.TrimEnd('/')}/title/{id}", UriKind.Absolute);
        var coverImage = ResolveCoverImage(item, id);

        return new Manga(
            new MangaId(id),
            title,
            synopsis,
            coverImage,
            detailPage,
            Array.Empty<Chapter>());
    }

    private Chapter? ParseChapter(JsonElement item, ref float fallbackNumber)
    {
        var chapterId = TryGetString(item, "id");
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var chapterRaw = TryGetString(attributes, "chapter");
        var parsedChapter = ParseChapterNumber(chapterRaw);
        float chapterNumber;
        if (parsedChapter.HasValue && parsedChapter.Value > 0)
        {
            chapterNumber = parsedChapter.Value;
            if (fallbackNumber <= chapterNumber)
            {
                fallbackNumber = chapterNumber + 1f;
            }
        }
        else
        {
            chapterNumber = fallbackNumber;
            fallbackNumber += 1f;
        }

        var chapterTitle = TryGetString(attributes, "title");
        if (string.IsNullOrWhiteSpace(chapterTitle))
        {
            chapterTitle = $"Chapter {chapterNumber.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        var chapterPage = new Uri($"{_options.WebBase!.TrimEnd('/')}/chapter/{chapterId}", UriKind.Absolute);
        return new Chapter(new ChapterId(chapterId), chapterTitle, chapterNumber, chapterPage);
    }

    private string ResolveTitle(JsonElement attributes, string fallback)
    {
        if (attributes.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.Object)
        {
            var fromTitle = ResolveLocalizedMap(titleElement);
            if (!string.IsNullOrWhiteSpace(fromTitle))
            {
                return fromTitle!;
            }
        }

        if (attributes.TryGetProperty("altTitles", out var altTitlesElement) && altTitlesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var altTitle in altTitlesElement.EnumerateArray())
            {
                if (altTitle.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var resolved = ResolveLocalizedMap(altTitle);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved!;
                }
            }
        }

        return fallback;
    }

    private string? ResolveDescription(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("description", out var descriptionElement) || descriptionElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ResolveLocalizedMap(descriptionElement);
    }

    private Uri? ResolveCoverImage(JsonElement item, string mangaId)
    {
        if (!item.TryGetProperty("relationships", out var relationshipsElement) || relationshipsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var relationship in relationshipsElement.EnumerateArray())
        {
            if (relationship.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!string.Equals(TryGetString(relationship, "type"), "cover_art", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!relationship.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fileName = TryGetString(attributes, "fileName");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var coverBase = string.IsNullOrWhiteSpace(_options.CoverBase)
                ? "https://uploads.mangadex.org/covers"
                : _options.CoverBase!.TrimEnd('/');
            var coverUrl = $"{coverBase}/{mangaId}/{fileName}";
            if (Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri))
            {
                return coverUri;
            }
        }

        return null;
    }

    private static float? ParseChapterNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int NormalizeSearchLimit(int configured)
    {
        if (configured <= 0)
        {
            return 20;
        }

        return Math.Clamp(configured, 1, 100);
    }

    private static int NormalizeChapterLimit(int configured)
    {
        if (configured <= 0)
        {
            return 500;
        }

        return Math.Clamp(configured, 1, 5000);
    }

    private string BuildApiUrl(
        string path,
        IEnumerable<KeyValuePair<string, string>>? queryParams = null,
        string? languageFilterKey = null,
        bool includeContentRatings = false)
    {
        var items = new List<KeyValuePair<string, string>>();
        if (queryParams is not null)
        {
            items.AddRange(queryParams);
        }

        if (!string.IsNullOrWhiteSpace(languageFilterKey) && !string.IsNullOrWhiteSpace(_options.TranslatedLanguage))
        {
            items.Add(new KeyValuePair<string, string>(languageFilterKey, _options.TranslatedLanguage.Trim()));
        }

        if (includeContentRatings)
        {
            items.Add(new KeyValuePair<string, string>("contentRating[]", "safe"));
            items.Add(new KeyValuePair<string, string>("contentRating[]", "suggestive"));
            items.Add(new KeyValuePair<string, string>("contentRating[]", "erotica"));
            if (_options.IncludeNsfw)
            {
                items.Add(new KeyValuePair<string, string>("contentRating[]", "pornographic"));
            }
        }

        var baseUrl = _options.ApiBase!.TrimEnd('/');
        var builder = new StringBuilder(baseUrl);
        if (!path.StartsWith('/'))
        {
            builder.Append('/');
        }

        builder.Append(path);

        if (items.Count > 0)
        {
            builder.Append('?');
            for (var i = 0; i < items.Count; i++)
            {
                var (key, value) = items[i];
                if (i > 0)
                {
                    builder.Append('&');
                }

                builder.Append(Uri.EscapeDataString(key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(value));
            }
        }

        return builder.ToString();
    }

    private HttpRequestMessage BuildRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        if (!string.IsNullOrWhiteSpace(_options.Referer) && Uri.TryCreate(_options.Referer, UriKind.Absolute, out var refererUri))
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

        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
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
                _logger.LogDebug("MangaDex request to {Url} returned HTTP {StatusCode}.", request.RequestUri, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MangaDex request failed for {Url}", request.RequestUri);
            return null;
        }
    }

    private string? ResolveLocalizedMap(JsonElement map)
    {
        foreach (var language in EnumeratePreferredLanguageCodes())
        {
            if (map.TryGetProperty(language, out var exact) && exact.ValueKind == JsonValueKind.String)
            {
                var value = exact.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private IEnumerable<string> EnumeratePreferredLanguageCodes()
    {
        var configured = _options.TranslatedLanguage?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;

            var dashIndex = configured.IndexOf('-');
            if (dashIndex > 0)
            {
                yield return configured[..dashIndex];
            }
        }

        yield return "en";
        yield return "en-us";
    }

    private static JsonElement? GetArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? ExtractUuid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = UuidRegex.Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }
}
