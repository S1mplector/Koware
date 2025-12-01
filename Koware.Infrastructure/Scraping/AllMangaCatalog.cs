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

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var gql = "query( $search: SearchInput $limit: Int $page: Int $translationType: VaildTranslationTypeEnumType $countryOrigin: VaildCountryOriginEnumType ) { mangas( search: $search limit: $limit page: $page translationType: $translationType countryOrigin: $countryOrigin ) { edges { _id name englishName thumbnail description __typename } }}";
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

            var page = new Uri($"{_options.Referer.TrimEnd('/')}/manga/{manga.Id.Value}/chapter-{raw}");
            chapters.Add(new Chapter(new ChapterId($"{manga.Id.Value}:ch-{raw}"), $"Chapter {raw}", num, page));
        }

        return chapters.OrderBy(c => c.Number).ToArray();
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        var (mangaId, chapterNumber) = ParseChapterId(chapter);
        var gql = "query ($mangaId: String!, $translationType: VaildTranslationTypeEnumType!, $chapterString: String!) { chapterPages( mangaId: $mangaId translationType: $translationType chapterString: $chapterString ) { edges { pictureUrls { url } pictureUrlHead } }}";
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

        var pageNum = 1;
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

            foreach (var pic in pictureUrls.EnumerateArray())
            {
                if (!pic.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlProp.GetString();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                // Resolve relative URLs against pictureUrlHead or base
                var absoluteUrl = ResolveImageUrl(url, pictureUrlHead);
                if (Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var imageUri))
                {
                    pages.Add(new ChapterPage(pageNum++, imageUri, _options.Referer));
                }
            }
        }

        return pages;
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
        if (Uri.TryCreate(new Uri(_options.Referer), url, out var resolved))
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
        var query = $"query={Uri.EscapeDataString(gql)}&variables={Uri.EscapeDataString(JsonSerializer.Serialize(variables, _serializerOptions))}";
        return new Uri($"{_options.ApiBase.TrimEnd('/')}/api?{query}");
    }

    private Uri BuildDetailUri(string id) => new($"https://{_options.BaseHost}/manga/{id}");

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
}
