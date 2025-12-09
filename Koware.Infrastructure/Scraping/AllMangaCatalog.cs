// Author: Ilgaz Mehmetoğlu | Summary: AllManga implementation of IMangaCatalog handling search, chapters, and page fetching.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class AllMangaCatalog : IMangaCatalog
{
    private readonly HttpClient _httpClient;
    private readonly AllMangaOptions _options;
    private readonly ILogger<AllMangaCatalog> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public AllMangaCatalog(HttpClient httpClient, IOptions<AllMangaOptions> options, ILogger<AllMangaCatalog> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if this provider is properly configured.
    /// </summary>
    public bool IsConfigured => _options.IsConfigured;

    public Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, SearchFilters.Empty, cancellationToken);

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning("AllManga source not configured. Add configuration to ~/.config/koware/appsettings.user.json");
            return Array.Empty<Manga>();
        }

        // Note: manga search does not use translationType parameter (unlike anime/shows)
        var gql = "query( $search: SearchInput $limit: Int $page: Int $countryOrigin: VaildCountryOriginEnumType ) { mangas( search: $search limit: $limit page: $page countryOrigin: $countryOrigin ) { edges { _id name englishName thumbnail description __typename } }}";
        
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
                ContentStatus.Hiatus => "Hiatus",
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
        
        // Map sort order
        var sortBy = filters.Sort switch
        {
            SearchSort.Popularity => "popularity_desc",
            SearchSort.Score => "score_desc",
            SearchSort.Recent => "recent",
            SearchSort.Title => "name_asc",
            _ => (string?)null
        };
        
        if (sortBy != null)
        {
            searchInput["sortBy"] = sortBy;
        }
        
        var variables = new
        {
            search = searchInput,
            limit = _options.SearchLimit,
            page = 1,
            countryOrigin
        };

        var uri = BuildApiUri(gql, variables);
        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var edges = json.RootElement
            .GetProperty("data")
            .GetProperty("mangas")
            .GetProperty("edges");

        var results = new List<Manga>();
        foreach (var edge in edges.EnumerateArray())
        {
            var id = edge.GetProperty("_id").GetString()!;
            var title = edge.TryGetProperty("englishName", out var eng) && eng.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(eng.GetString())
                ? eng.GetString()!
                : edge.GetProperty("name").GetString() ?? id;
            var synopsis = edge.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                ? desc.GetString()
                : null;
            Uri? coverImage = null;
            if (edge.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.String)
            {
                var thumbUrl = thumb.GetString();
                if (!string.IsNullOrWhiteSpace(thumbUrl) && Uri.TryCreate(thumbUrl, UriKind.Absolute, out var parsedThumb))
                {
                    coverImage = parsedThumb;
                }
            }

            results.Add(new Manga(
                new MangaId(id),
                title,
                synopsis,
                coverImage,
                detailPage: BuildDetailUri(id),
                chapters: Array.Empty<Chapter>()));
        }

        return results;
    }

    public async Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return Array.Empty<Manga>();
        }

        // Use search with popularity sort and empty query
        var browseFilters = (filters ?? SearchFilters.Empty) with { Sort = SearchSort.Popularity };
        return await SearchAsync("", browseFilters, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        var gql = "query ($mangaId: String!) { manga( _id: $mangaId ) { _id availableChaptersDetail }}";
        var variables = new { mangaId = manga.Id.Value };
        var uri = BuildApiUri(gql, variables);

        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("manga", out var mangaElement) ||
            !mangaElement.TryGetProperty("availableChaptersDetail", out var available))
        {
            _logger.LogWarning("Chapter response missing expected fields for manga {MangaId}.", manga.Id.Value);
            return Array.Empty<Chapter>();
        }

        var translationKey = _options.TranslationType ?? "sub";
        JsonElement chaptersElement;
        if (!available.TryGetProperty(translationKey, out chaptersElement))
        {
            var matched = available.EnumerateObject()
                .FirstOrDefault(prop => string.Equals(prop.Name, translationKey, StringComparison.OrdinalIgnoreCase));
            chaptersElement = matched.Value;
        }

        if (chaptersElement.ValueKind == JsonValueKind.Undefined)
        {
            _logger.LogWarning("Translation type '{Translation}' not found for manga {MangaId}.", translationKey, manga.Id.Value);
            return Array.Empty<Chapter>();
        }

        var chapters = new List<Chapter>();
        foreach (var ch in chaptersElement.EnumerateArray())
        {
            var raw = ch.GetString();
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) || num <= 0)
            {
                _logger.LogDebug("Skipping invalid chapter '{Chapter}' for manga {MangaId}", raw, manga.Id.Value);
                continue;
            }

            var refererBase = string.IsNullOrWhiteSpace(_options.Referer) ? "https://example.com" : _options.Referer;
            var page = new Uri($"{refererBase.TrimEnd('/')}/manga/{manga.Id.Value}/chapter-{raw}");
            chapters.Add(new Chapter(new ChapterId($"{manga.Id.Value}:ch-{raw}"), $"Chapter {raw}", num, page));
        }

        return chapters.OrderBy(c => c.Number).ToArray();
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        var (mangaId, chapterNumber) = ParseChapterId(chapter);
        // Note: manga uses VaildTranslationTypeMangaEnumType (not VaildTranslationTypeEnumType)
        // and pictureUrls is now a scalar [Object] type without subfields
        var gql = "query ($mangaId: String!, $translationType: VaildTranslationTypeMangaEnumType!, $chapterString: String!) { chapterPages( mangaId: $mangaId translationType: $translationType chapterString: $chapterString ) { edges { pictureUrls pictureUrlHead } }}";
        var variables = new
        {
            mangaId = mangaId,
            translationType = _options.TranslationType,
            chapterString = chapterNumber
        };

        var uri = BuildApiUri(gql, variables);
        using var response = await SendWithRetryAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var pages = new List<ChapterPage>();
        var chapterPages = json.RootElement
            .GetProperty("data")
            .GetProperty("chapterPages");

        if (!chapterPages.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("No pages found for chapter {ChapterId}", chapter.Id.Value);
            return Array.Empty<ChapterPage>();
        }

        foreach (var edge in edges.EnumerateArray())
        {
            string? pictureUrlHead = null;
            if (edge.TryGetProperty("pictureUrlHead", out var headProp) && headProp.ValueKind == JsonValueKind.String)
            {
                pictureUrlHead = headProp.GetString();
            }

            if (!edge.TryGetProperty("pictureUrls", out var pictureUrls) || pictureUrls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // pictureUrls is now an array of objects with "num" and "url" properties
            foreach (var pic in pictureUrls.EnumerateArray())
            {
                string? url = null;
                int pageNum = 0;

                // Handle both old format (object with url property) and new format (object with num and url)
                if (pic.ValueKind == JsonValueKind.Object)
                {
                    if (pic.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    {
                        url = urlProp.GetString();
                    }
                    if (pic.TryGetProperty("num", out var numProp) && numProp.ValueKind == JsonValueKind.Number)
                    {
                        pageNum = numProp.GetInt32();
                    }
                }
                else if (pic.ValueKind == JsonValueKind.String)
                {
                    url = pic.GetString();
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                // Resolve relative URLs against pictureUrlHead or base
                var absoluteUrl = ResolveImageUrl(url, pictureUrlHead);
                if (Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var imageUri))
                {
                    // Use the page number from the API if available, otherwise use array index + 1
                    var effectivePageNum = pageNum > 0 ? pageNum : pages.Count + 1;
                    pages.Add(new ChapterPage(effectivePageNum, imageUri, _options.Referer));
                }
            }
        }

        // Sort by page number to ensure correct order
        return pages.OrderBy(p => p.PageNumber).ToArray();
    }

    private string ResolveImageUrl(string url, string? baseUrl)
    {
        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
        {
            return url;
        }

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            // baseUrl typically contains the CDN prefix
            var combined = baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
            if (Uri.IsWellFormedUriString(combined, UriKind.Absolute))
            {
                return combined;
            }
        }

        // Fallback: resolve against referer
        var refererBase = string.IsNullOrWhiteSpace(_options.Referer) ? "https://example.com/" : _options.Referer;
        if (Uri.TryCreate(new Uri(refererBase), url, out var resolved))
        {
            return resolved.ToString();
        }

        return url;
    }

    private static (string mangaId, string chapterNumber) ParseChapterId(Chapter chapter)
    {
        var marker = ":ch-";
        var idValue = chapter.Id.Value;
        var idx = idValue.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

        var mangaId = idx > 0 ? idValue[..idx] : idValue;
        var number = chapter.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (idx >= 0 && idx + marker.Length < idValue.Length)
        {
            number = idValue[(idx + marker.Length)..];
        }

        return (mangaId, number);
    }

    private Uri BuildApiUri(string gql, object variables)
    {
        var apiBase = string.IsNullOrWhiteSpace(_options.ApiBase) ? "https://example.com" : _options.ApiBase;
        var query = $"query={Uri.EscapeDataString(gql)}&variables={Uri.EscapeDataString(JsonSerializer.Serialize(variables, _serializerOptions))}";
        return new Uri($"{apiBase.TrimEnd('/')}/api?{query}");
    }

    private Uri BuildDetailUri(string id) => new($"https://{_options.BaseHost}/manga/{id}");

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
}
