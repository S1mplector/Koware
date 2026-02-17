// Author: Ilgaz Mehmetoğlu
// nHentai provider implementation using the public gallery/search APIs.
using System.Net;
using System.Text.Json;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class NhentaiCatalog : IMangaCatalog
{
    private readonly HttpClient _httpClient;
    private readonly NhentaiOptions _options;
    private readonly ILogger<NhentaiCatalog> _logger;

    public NhentaiCatalog(HttpClient httpClient, IOptions<NhentaiOptions> options, ILogger<NhentaiCatalog> logger)
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
            _logger.LogWarning("nHentai source not configured. Add configuration to ~/.config/koware/appsettings.user.json");
            return Array.Empty<Manga>();
        }

        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return await BrowsePopularAsync(filters, cancellationToken);
        }

        var endpoint = $"{_options.EffectiveApiBase.TrimEnd('/')}/galleries/search?query={Uri.EscapeDataString(trimmed)}&page=1";
        using var request = BuildRequest(endpoint, _options.EffectiveReferer);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<Manga>();
        }

        return ParseSearchResults(payload)
            .Take(_options.SearchLimit > 0 ? _options.SearchLimit : 20)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<Manga>();
        }

        var endpoint = $"{_options.EffectiveApiBase.TrimEnd('/')}/galleries/search?query=&page=1";
        using var request = BuildRequest(endpoint, _options.EffectiveReferer);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<Manga>();
        }

        return ParseSearchResults(payload)
            .Take(_options.SearchLimit > 0 ? _options.SearchLimit : 20)
            .ToArray();
    }

    public Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Task.FromResult<IReadOnlyCollection<Chapter>>(Array.Empty<Chapter>());
        }

        var galleryId = ExtractGalleryId(manga.Id.Value, manga.DetailPage);
        if (galleryId == null)
        {
            return Task.FromResult<IReadOnlyCollection<Chapter>>(Array.Empty<Chapter>());
        }

        var chapter = new Chapter(
            new ChapterId(galleryId),
            "Chapter 1",
            1,
            manga.DetailPage);

        return Task.FromResult<IReadOnlyCollection<Chapter>>(new[] { chapter });
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<ChapterPage>();
        }

        var galleryId = ExtractGalleryId(chapter.Id.Value, chapter.PageUrl);
        if (galleryId == null)
        {
            _logger.LogWarning("Could not parse nHentai gallery id from chapter id '{ChapterId}'", chapter.Id.Value);
            return Array.Empty<ChapterPage>();
        }

        var endpoint = $"{_options.EffectiveApiBase.TrimEnd('/')}/gallery/{galleryId}";
        using var request = BuildRequest(endpoint, _options.EffectiveReferer);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        var payload = await SendAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<ChapterPage>();
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var mediaId = root.TryGetProperty("media_id", out var mediaIdElement) && mediaIdElement.ValueKind == JsonValueKind.String
                ? mediaIdElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(mediaId) ||
                !root.TryGetProperty("images", out var imagesElement) ||
                !imagesElement.TryGetProperty("pages", out var pagesElement) ||
                pagesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ChapterPage>();
            }

            var pages = new List<ChapterPage>();
            var pageIndex = 1;
            foreach (var page in pagesElement.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var extensionType = page.TryGetProperty("t", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
                var extension = ExtensionFromType(extensionType);

                var imageUrl = $"https://i.nhentai.net/galleries/{mediaId}/{pageIndex}.{extension}";
                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
                {
                    pageIndex++;
                    continue;
                }

                pages.Add(new ChapterPage(pageIndex, imageUri, _options.EffectiveReferer));
                pageIndex++;
            }

            return pages;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "nHentai gallery payload was not valid JSON.");
            return Array.Empty<ChapterPage>();
        }
    }

    private IEnumerable<Manga> ParseSearchResults(string payload)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "nHentai search payload was not valid JSON.");
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in resultElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = item.TryGetProperty("id", out var idElement)
                    ? idElement.GetRawText().Trim('"')
                    : null;

                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var title = ResolveTitle(item, id);
                var detailPage = BuildGalleryUri(id);

                Uri? coverImage = null;
                if (item.TryGetProperty("media_id", out var mediaIdElement) && mediaIdElement.ValueKind == JsonValueKind.String)
                {
                    var mediaId = mediaIdElement.GetString();
                    var coverExtType = item.TryGetProperty("images", out var imagesElement) &&
                                       imagesElement.ValueKind == JsonValueKind.Object &&
                                       imagesElement.TryGetProperty("cover", out var coverElement) &&
                                       coverElement.ValueKind == JsonValueKind.Object &&
                                       coverElement.TryGetProperty("t", out var typeElement) &&
                                       typeElement.ValueKind == JsonValueKind.String
                        ? typeElement.GetString()
                        : null;

                    var coverExt = ExtensionFromType(coverExtType);
                    var coverUrl = $"https://t.nhentai.net/galleries/{mediaId}/cover.{coverExt}";
                    if (Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri))
                    {
                        coverImage = coverUri;
                    }
                }

                yield return new Manga(
                    new MangaId(id),
                    title,
                    synopsis: null,
                    coverImage: coverImage,
                    detailPage: detailPage,
                    chapters: Array.Empty<Chapter>());
            }
        }
    }

    private static string ResolveTitle(JsonElement item, string fallback)
    {
        if (item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.Object)
        {
            var english = TryGetString(titleElement, "english");
            if (!string.IsNullOrWhiteSpace(english))
            {
                return english.Trim();
            }

            var pretty = TryGetString(titleElement, "pretty");
            if (!string.IsNullOrWhiteSpace(pretty))
            {
                return pretty.Trim();
            }

            var japanese = TryGetString(titleElement, "japanese");
            if (!string.IsNullOrWhiteSpace(japanese))
            {
                return japanese.Trim();
            }
        }

        return fallback;
    }

    private Uri BuildGalleryUri(string galleryId)
    {
        return new Uri($"{_options.BaseUrl!.TrimEnd('/')}/g/{galleryId}/", UriKind.Absolute);
    }

    private static string ExtensionFromType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "j" => "jpg",
            "p" => "png",
            "g" => "gif",
            "w" => "webp",
            _ => "jpg"
        };
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? ExtractGalleryId(string rawId, Uri? pageUrl)
    {
        if (!string.IsNullOrWhiteSpace(rawId))
        {
            var direct = new string(rawId.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
        }

        if (pageUrl is not null)
        {
            var segments = pageUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var gIndex = Array.FindIndex(segments, s => s.Equals("g", StringComparison.OrdinalIgnoreCase));
            if (gIndex >= 0 && gIndex + 1 < segments.Length)
            {
                var maybeId = segments[gIndex + 1];
                if (maybeId.All(char.IsDigit))
                {
                    return maybeId;
                }
            }
        }

        return null;
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
                _logger.LogDebug("nHentai request to {Url} failed with HTTP {Status}", request.RequestUri, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nHentai request failed for {Url}", request.RequestUri);
            return null;
        }
    }
}
