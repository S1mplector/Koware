// Author: Ilgaz Mehmetoğlu
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Default implementation of site probing.
/// Analyzes website structure, detects APIs, frameworks, and content type.
/// </summary>
public sealed class SiteProber : ISiteProber
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SiteProber> _logger;

    private static readonly string[] AnimeKeywords = 
        ["anime", "episode", "watch", "stream", "sub", "dub", "season", "hentai", "video", "play"];
    private static readonly string[] MangaKeywords = 
        ["manga", "chapter", "read", "scan", "webtoon", "comic", "manhwa", "manhua", "doujin", "gallery", "nhentai", "page"];
    private static readonly string[] GraphQLIndicators = 
        ["graphql", "gql", "__schema", "query {", "mutation {"];
    private static readonly string[] SpaFrameworks = 
        ["react", "vue", "angular", "next", "nuxt", "svelte"];
    
    // Known site patterns for specialized detection
    private static readonly Dictionary<string, SiteKnowledge> KnownSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nhentai"] = new SiteKnowledge
        {
            Category = ContentCategory.Manga,
            ApiPatterns = ["/api/gallery/", "/api/galleries/search"],
            SearchEndpoint = "/api/galleries/search?query={query}",
            IdPattern = @"/g/(\d+)",
            ContentEndpoint = "/api/gallery/{id}",
            FieldMappings = new Dictionary<string, string>
            {
                ["Id"] = "$.id",
                ["Title"] = "$.title.english",
                ["TitleAlt"] = "$.title.japanese",
                ["CoverImage"] = "$.images.cover.url",
                ["PageCount"] = "$.num_pages",
                ["Tags"] = "$.tags[*].name"
            }
        },
        ["hanime"] = new SiteKnowledge
        {
            Category = ContentCategory.Anime,
            ApiPatterns = ["/api/v8/", "/rapi/v7/"],
            // hanime API is at search.htv-services.com, uses POST with JSON body
            ApiBase = "https://search.htv-services.com",
            SearchEndpoint = "/api/v8/search",
            SearchMethod = "POST",
            SearchBodyTemplate = """{"search_text": "{query}", "tags": [], "tags_mode": "AND", "brands": [], "blacklist": [], "order_by": "likes", "ordering": "desc", "page": 0}""",
            ResultsPath = "$.hits",
            IdPattern = @"/videos/hentai/([\w-]+)",
            ContentEndpoint = "/api/v8/video?id={id}",
            FieldMappings = new Dictionary<string, string>
            {
                ["Id"] = "$.id",
                ["Title"] = "$.name",
                ["CoverImage"] = "$.cover_url",
                ["Synopsis"] = "$.description"
            }
        },
        ["hentaihaven"] = new SiteKnowledge
        {
            Category = ContentCategory.Anime,
            ApiPatterns = ["/wp-json/", "/api/"],
            SearchEndpoint = "/wp-json/wp/v2/posts?search={query}",
            IdPattern = @"/watch/([\w-]+)"
        },
        ["mangadex"] = new SiteKnowledge
        {
            Category = ContentCategory.Manga,
            ApiPatterns = ["/api/v5/"],
            SearchEndpoint = "/api/v5/manga?title={query}",
            IdPattern = @"/title/([a-f0-9-]+)",
            ContentEndpoint = "/api/v5/manga/{id}"
        },
        ["gogoanime"] = new SiteKnowledge
        {
            Category = ContentCategory.Anime,
            ApiPatterns = ["/ajax/", "/api/"],
            SearchEndpoint = "/search.html?keyword={query}",
            IdPattern = @"/category/([\w-]+)"
        }
    };

    public SiteProber(HttpClient httpClient, ILogger<SiteProber> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SiteProfile> ProbeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var apiEndpoints = new List<string>();
        var cdnHosts = new List<string>();
        var headers = new Dictionary<string, string>();

        _logger.LogInformation("Probing site: {Url}", url);

        // Normalize URL
        var baseUrl = new Uri($"{url.Scheme}://{url.Host}");
        
        // Check for known site patterns
        var knownSite = DetectKnownSite(url.Host);

        // Fetch main page
        string? htmlContent = null;
        string? serverSoftware = null;
        bool hasCloudflare = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            
            // Check response headers
            serverSoftware = response.Headers.Server?.ToString();
            hasCloudflare = response.Headers.Contains("CF-RAY") || 
                           (serverSoftware?.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false);

            if (response.Headers.TryGetValues("X-Powered-By", out var poweredBy))
            {
                headers["X-Powered-By"] = string.Join(", ", poweredBy);
            }

            htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            errors.Add($"Failed to fetch main page: {ex.Message}");
            _logger.LogWarning(ex, "Failed to fetch {Url}", baseUrl);
        }

        // Try to fetch robots.txt
        string? robotsTxt = null;
        try
        {
            var robotsUrl = new Uri(baseUrl, "/robots.txt");
            robotsTxt = await _httpClient.GetStringAsync(robotsUrl, cancellationToken);
            
            // Extract potential API paths from robots.txt
            var apiPaths = ExtractApiPathsFromRobots(robotsTxt);
            apiEndpoints.AddRange(apiPaths);
        }
        catch
        {
            // robots.txt not available
        }

        // Parse HTML content
        var siteTitle = "";
        var siteDescription = "";
        var jsFramework = "";
        var hasGraphQL = false;
        var requiresJs = false;
        var category = ContentCategory.Unknown;
        var siteType = SiteType.Unknown;

        if (!string.IsNullOrEmpty(htmlContent))
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = await context.OpenAsync(req => req.Content(htmlContent), cancellationToken);

            // Extract metadata
            siteTitle = document.Title ?? "";
            siteDescription = document.QuerySelector("meta[name='description']")?.GetAttribute("content") ?? "";

            // Detect JS framework
            jsFramework = DetectJsFramework(document, htmlContent);
            requiresJs = !string.IsNullOrEmpty(jsFramework) || 
                        htmlContent.Contains("__NEXT_DATA__") ||
                        htmlContent.Contains("__NUXT__") ||
                        document.QuerySelector("#app, #root, [data-reactroot]") != null;

            // Detect content category
            category = DetectContentCategory(document, htmlContent, siteTitle, siteDescription);

            // Detect site type
            siteType = requiresJs ? SiteType.SPA : SiteType.Static;
            if (requiresJs && htmlContent.Contains("__NEXT_DATA__"))
                siteType = SiteType.Hybrid;

            // Find API endpoints in scripts
            var scriptEndpoints = ExtractApiEndpointsFromHtml(document, htmlContent, baseUrl);
            apiEndpoints.AddRange(scriptEndpoints);

            // Check for GraphQL
            hasGraphQL = htmlContent.Contains("graphql", StringComparison.OrdinalIgnoreCase) ||
                        apiEndpoints.Any(e => e.Contains("graphql", StringComparison.OrdinalIgnoreCase)) ||
                        htmlContent.Contains("__typename");

            // Find CDN hosts
            var cdns = ExtractCdnHosts(document, htmlContent);
            cdnHosts.AddRange(cdns);
        }

        // Required headers for requests
        headers["Referer"] = baseUrl.ToString();
        headers["Origin"] = baseUrl.ToString().TrimEnd('/');
        
        // Add known site API endpoints
        if (knownSite != null)
        {
            apiEndpoints.AddRange(knownSite.ApiPatterns);
            if (category == ContentCategory.Unknown)
                category = knownSite.Category;
        }

        return new SiteProfile
        {
            BaseUrl = baseUrl,
            Type = siteType,
            Category = category,
            RequiresJavaScript = requiresJs,
            HasCloudflareProtection = hasCloudflare,
            HasGraphQL = hasGraphQL,
            ServerSoftware = serverSoftware,
            JsFramework = jsFramework,
            DetectedApiEndpoints = apiEndpoints.Distinct().ToList(),
            DetectedCdnHosts = cdnHosts.Distinct().ToList(),
            RequiredHeaders = headers,
            SiteTitle = siteTitle,
            SiteDescription = siteDescription,
            RobotsTxt = robotsTxt,
            KnownSiteInfo = knownSite,
            Errors = errors
        };
    }
    
    private static SiteKnowledge? DetectKnownSite(string host)
    {
        foreach (var (siteName, knowledge) in KnownSites)
        {
            if (host.Contains(siteName, StringComparison.OrdinalIgnoreCase))
            {
                return knowledge;
            }
        }
        return null;
    }

    private static string DetectJsFramework(IDocument document, string html)
    {
        // React
        if (html.Contains("react") || html.Contains("_reactRoot") || 
            document.QuerySelector("[data-reactroot]") != null)
            return "React";

        // Vue
        if (html.Contains("vue") || html.Contains("__vue__") ||
            document.QuerySelector("[data-v-]") != null)
            return "Vue";

        // Angular
        if (html.Contains("ng-version") || document.QuerySelector("[ng-app]") != null)
            return "Angular";

        // Next.js
        if (html.Contains("__NEXT_DATA__") || html.Contains("_next/"))
            return "Next.js";

        // Nuxt
        if (html.Contains("__NUXT__") || html.Contains("_nuxt/"))
            return "Nuxt";

        // Svelte
        if (html.Contains("svelte"))
            return "Svelte";

        return "";
    }

    private static ContentCategory DetectContentCategory(IDocument document, string html, string title, string description)
    {
        var fullText = $"{title} {description} {html}".ToLowerInvariant();

        var animeScore = AnimeKeywords.Count(k => fullText.Contains(k));
        var mangaScore = MangaKeywords.Count(k => fullText.Contains(k));

        // Check for specific patterns
        if (document.QuerySelector("video, .video-player, .player-container") != null)
            animeScore += 3;
        if (document.QuerySelector(".manga-reader, .chapter-images, .page-container") != null)
            mangaScore += 3;

        // Check URL patterns in links
        var links = document.QuerySelectorAll("a[href]");
        foreach (var link in links.Take(100))
        {
            var href = link.GetAttribute("href") ?? "";
            if (href.Contains("/anime/") || href.Contains("/watch/"))
                animeScore++;
            if (href.Contains("/manga/") || href.Contains("/chapter/") || href.Contains("/read/"))
                mangaScore++;
        }

        if (animeScore > 0 && mangaScore > 0 && Math.Abs(animeScore - mangaScore) < 3)
            return ContentCategory.Both;
        if (animeScore > mangaScore)
            return ContentCategory.Anime;
        if (mangaScore > animeScore)
            return ContentCategory.Manga;

        return ContentCategory.Unknown;
    }

    private static List<string> ExtractApiPathsFromRobots(string robotsTxt)
    {
        var paths = new List<string>();
        var lines = robotsTxt.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase) ||
                line.TrimStart().StartsWith("Allow:", StringComparison.OrdinalIgnoreCase))
            {
                var path = line.Split(':', 2).LastOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(path) && 
                    (path.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                     path.Contains("graphql", StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    private static List<string> ExtractApiEndpointsFromHtml(IDocument document, string html, Uri baseUrl)
    {
        var endpoints = new List<string>();

        // Look for API URLs in script contents
        var apiPatterns = new[]
        {
            @"['""]([^'""]*?/api[^'""]*?)['""]",
            @"['""]([^'""]*?/graphql[^'""]*?)['""]",
            @"['""]([^'""]*?/v\d+/[^'""]*?)['""]",
            @"fetch\s*\(\s*['""]([^'""]+)['""]",
            @"axios\s*\.\s*\w+\s*\(\s*['""]([^'""]+)['""]"
        };

        foreach (var pattern in apiPatterns)
        {
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var endpoint = match.Groups[1].Value;
                    if (!endpoint.Contains("{{") && !endpoint.Contains("${"))
                    {
                        endpoints.Add(endpoint);
                    }
                }
            }
        }

        // Check script src attributes for API hints
        var scripts = document.QuerySelectorAll("script[src]");
        foreach (var script in scripts)
        {
            var src = script.GetAttribute("src") ?? "";
            if (src.Contains("api") || src.Contains("graphql"))
            {
                endpoints.Add(src);
            }
        }

        // Look for data attributes with API info
        var apiDataElements = document.QuerySelectorAll("[data-api], [data-endpoint], [data-url]");
        foreach (var element in apiDataElements)
        {
            var apiUrl = element.GetAttribute("data-api") ?? 
                        element.GetAttribute("data-endpoint") ?? 
                        element.GetAttribute("data-url");
            if (!string.IsNullOrEmpty(apiUrl))
            {
                endpoints.Add(apiUrl);
            }
        }

        return endpoints;
    }

    private static List<string> ExtractCdnHosts(IDocument document, string html)
    {
        var cdnHosts = new List<string>();
        var cdnPatterns = new[]
        {
            "cloudfront", "akamai", "fastly", "bunnycdn", "cloudflare", 
            "cdn", "static", "assets", "media", "img", "images"
        };

        // Extract from img src
        var images = document.QuerySelectorAll("img[src]");
        foreach (var img in images)
        {
            var src = img.GetAttribute("src") ?? "";
            if (Uri.TryCreate(src, UriKind.Absolute, out var uri))
            {
                if (cdnPatterns.Any(p => uri.Host.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    cdnHosts.Add(uri.Host);
                }
            }
        }

        // Extract from URLs in HTML
        var urlPattern = @"https?://([a-zA-Z0-9\-\.]+)";
        var matches = Regex.Matches(html, urlPattern);
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                var host = match.Groups[1].Value;
                if (cdnPatterns.Any(p => host.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    cdnHosts.Add(host);
                }
            }
        }

        return cdnHosts;
    }
}
