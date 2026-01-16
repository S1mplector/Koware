// Author: Ilgaz Mehmetoğlu
using System.Net;
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Dynamic IMangaCatalog implementation that executes any DynamicProviderConfig.
/// </summary>
public sealed class DynamicMangaCatalog : IMangaCatalog
{
    private readonly DynamicProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ITransformEngine _transforms;
    private readonly ILogger<DynamicMangaCatalog> _logger;

    public DynamicMangaCatalog(
        DynamicProviderConfig config,
        HttpClient httpClient,
        ITransformEngine transforms,
        ILogger<DynamicMangaCatalog> logger)
    {
        _config = config;
        _httpClient = httpClient;
        _transforms = transforms;
        _logger = logger;
    }

    public string ProviderName => _config.Name;
    public string ProviderSlug => _config.Slug;

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        return await SearchAsync(query, SearchFilters.Empty, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Manga>> SearchAsync(string query, SearchFilters filters, CancellationToken cancellationToken = default)
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

            var results = _transforms.ExtractAll(response, searchConfig.ResultMapping, searchConfig.ResultsPath);
            var baseUrl = _config.Hosts.ApiBase ?? $"https://{_config.Hosts.BaseHost}";
            
            return results.Select(r => {
                var id = GetString(r, "Id") ?? Guid.NewGuid().ToString();
                var detailPage = TryParseUri(GetString(r, "DetailPage")) 
                    ?? new Uri($"{baseUrl.TrimEnd('/')}/g/{id}");
                
                return new Manga(
                    new MangaId(id),
                    GetString(r, "Title") ?? "Unknown",
                    synopsis: GetString(r, "Synopsis"),
                    coverImage: TryParseUri(GetString(r, "CoverImage")),
                    detailPage: detailPage,
                    chapters: Array.Empty<Chapter>()
                );
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for provider {Provider}", _config.Slug);
            return Array.Empty<Manga>();
        }
    }

    public async Task<IReadOnlyCollection<Manga>> BrowsePopularAsync(SearchFilters? filters = null, CancellationToken cancellationToken = default)
    {
        return await SearchAsync("", filters ?? SearchFilters.Empty, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        var chapterConfig = _config.Content.Chapters;
        
        // For sites like nhentai where galleries ARE the content (no chapters),
        // create a synthetic chapter representing the whole gallery
        if (chapterConfig == null || chapterConfig.Endpoint == "/info")
        {
            _logger.LogDebug("Using gallery-as-chapter mode for provider {Provider}", _config.Slug);
            var baseUrl = _config.Hosts.ApiBase ?? $"https://{_config.Hosts.BaseHost}";
            return new List<Chapter>
            {
                new Chapter(
                    new ChapterId(manga.Id.Value),
                    manga.Title,
                    1f,
                    new Uri($"{baseUrl.TrimEnd('/')}/g/{manga.Id.Value}")
                )
            };
        }

        try
        {
            var requestBody = BuildRequest(chapterConfig.QueryTemplate, new Dictionary<string, string>
            {
                ["mangaId"] = manga.Id.Value,
                ["id"] = manga.Id.Value
            });

            var response = await ExecuteRequestAsync(
                chapterConfig.Endpoint,
                chapterConfig.Method,
                requestBody,
                cancellationToken);

            var results = _transforms.ExtractAll(response, chapterConfig.ResultMapping);
            
            // If no chapters found, treat the manga itself as a single chapter (gallery mode)
            if (results.Count == 0)
            {
                _logger.LogDebug("No chapters found, using gallery-as-chapter mode for {MangaId}", manga.Id.Value);
                var baseUrl = _config.Hosts.ApiBase ?? $"https://{_config.Hosts.BaseHost}";
                return new List<Chapter>
                {
                    new Chapter(
                        new ChapterId(manga.Id.Value),
                        manga.Title,
                        1f,
                        new Uri($"{baseUrl.TrimEnd('/')}/g/{manga.Id.Value}")
                    )
                };
            }
            
            var chapters = new List<Chapter>();
            var number = 1f;
            
            foreach (var r in results)
            {
                var chNum = GetFloat(r, "Number") ?? number;
                var chId = GetString(r, "Id") ?? $"{manga.Id.Value}:ch-{chNum}";
                var title = GetString(r, "Title") ?? $"Chapter {chNum}";
                var page = TryParseUri(GetString(r, "Page"));
                
                chapters.Add(new Chapter(new ChapterId(chId), title, chNum, page ?? manga.DetailPage));
                number++;
            }
            
            return chapters.OrderBy(c => c.Number).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetChapters failed for manga {MangaId}, using gallery-as-chapter mode", manga.Id.Value);
            // Fall back to gallery-as-chapter mode on error
            var baseUrl = _config.Hosts.ApiBase ?? $"https://{_config.Hosts.BaseHost}";
            return new List<Chapter>
            {
                new Chapter(
                    new ChapterId(manga.Id.Value),
                    manga.Title,
                    1f,
                    new Uri($"{baseUrl.TrimEnd('/')}/g/{manga.Id.Value}")
                )
            };
        }
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        var pageConfig = _config.Media.Pages;
        
        // For nhentai-style galleries, fetch from the gallery API endpoint
        if (pageConfig == null || pageConfig.Endpoint == "/read")
        {
            return await GetPagesFromGalleryApiAsync(chapter, cancellationToken);
        }

        try
        {
            var (mangaId, chNumber) = ParseChapterId(chapter.Id.Value);
            
            var requestBody = BuildRequest(pageConfig.QueryTemplate, new Dictionary<string, string>
            {
                ["mangaId"] = mangaId,
                ["chapterId"] = chapter.Id.Value,
                ["chapterString"] = chNumber
            });

            var response = await ExecuteRequestAsync(
                pageConfig.Endpoint,
                pageConfig.Method,
                requestBody,
                cancellationToken);

            var results = _transforms.ExtractAll(response, pageConfig.ResultMapping);
            
            var pages = new List<ChapterPage>();
            var pageNum = 1;
            
            foreach (var r in results)
            {
                var urlString = GetString(r, "Url");
                if (string.IsNullOrEmpty(urlString))
                    continue;
                    
                // Prepend image base URL if configured
                if (!Uri.IsWellFormedUriString(urlString, UriKind.Absolute) && 
                    !string.IsNullOrEmpty(pageConfig.ImageBaseUrl))
                {
                    urlString = pageConfig.ImageBaseUrl.TrimEnd('/') + "/" + urlString.TrimStart('/');
                }
                
                if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
                    continue;
                    
                var num = GetInt(r, "Number") ?? pageNum;
                pages.Add(new ChapterPage(num, url, _config.Hosts.Referer));
                pageNum++;
            }
            
            return pages.OrderBy(p => p.PageNumber).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPages failed for chapter {ChapterId}", chapter.Id.Value);
            return Array.Empty<ChapterPage>();
        }
    }

    private async Task<IReadOnlyCollection<ChapterPage>> GetPagesFromGalleryApiAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = _config.Hosts.ApiBase ?? $"https://{_config.Hosts.BaseHost}";
            // Strip provider prefix if present (e.g., "nhentai:617766" -> "617766")
            var galleryId = chapter.Id.Value;
            if (galleryId.Contains(':'))
            {
                galleryId = galleryId.Split(':').Last();
            }
            
            // Fetch gallery details from nhentai API
            var response = await ExecuteRequestAsync(
                $"/api/gallery/{galleryId}",
                SearchMethod.REST,
                "",
                cancellationToken);
            
            // Parse the response to extract images
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            var pages = new List<ChapterPage>();
            var pageNum = 1;
            
            // nhentai structure: images.pages[] with t (type) field
            if (root.TryGetProperty("images", out var images) && 
                images.TryGetProperty("pages", out var pagesArray))
            {
                // Get media_id for constructing image URLs
                var mediaId = root.TryGetProperty("media_id", out var mid) 
                    ? mid.GetString() 
                    : galleryId;
                
                foreach (var page in pagesArray.EnumerateArray())
                {
                    var ext = "jpg";
                    if (page.TryGetProperty("t", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        ext = type switch
                        {
                            "j" => "jpg",
                            "p" => "png",
                            "g" => "gif",
                            "w" => "webp",
                            _ => "jpg"
                        };
                    }
                    
                    // nhentai image URL format: https://i.nhentai.net/galleries/{media_id}/{page}.{ext}
                    var imageUrl = new Uri($"https://i.nhentai.net/galleries/{mediaId}/{pageNum}.{ext}");
                    pages.Add(new ChapterPage(pageNum, imageUrl, _config.Hosts.Referer));
                    pageNum++;
                }
            }
            
            return pages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPages from gallery API failed for chapter {ChapterId}", chapter.Id.Value);
            return Array.Empty<ChapterPage>();
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
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri(requestBody.StartsWith("http") ? requestBody : fullUrl + requestBody);
        }
        else
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri(fullUrl);
        }

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        ApplyRequestHeaders(request);

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

    private static (string mangaId, string chNumber) ParseChapterId(string chapterId)
    {
        var marker = ":ch-";
        var idx = chapterId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        
        if (idx > 0)
        {
            return (chapterId[..idx], chapterId[(idx + marker.Length)..]);
        }
        
        return (chapterId, "1");
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
        if (Uri.TryCreate(_config.Hosts.Referer, UriKind.Absolute, out var referer))
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

    private static float? GetFloat(Dictionary<string, object?> dict, string key)
    {
        var str = GetString(dict, key);
        return float.TryParse(str, System.Globalization.NumberStyles.Float, 
            System.Globalization.CultureInfo.InvariantCulture, out var num) ? num : null;
    }

    private static Uri? TryParseUri(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }
}
