// Author: Ilgaz Mehmetoğlu
using System.Text.Json;
using System.Text.RegularExpressions;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Discovers and analyzes API endpoints on a website.
/// </summary>
public sealed class ApiDiscoveryEngine : IApiDiscoveryEngine
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiDiscoveryEngine> _logger;

    private static readonly string[] CommonApiPaths = 
    [
        "/api", "/api/v1", "/api/v2", "/graphql", "/gql",
        "/api/anime", "/api/manga", "/api/search", "/api/shows"
    ];

    private static readonly string[] SearchQuerySamples = 
        ["naruto", "one piece", "attack on titan"];

    public ApiDiscoveryEngine(HttpClient httpClient, ILogger<ApiDiscoveryEngine> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ApiEndpoint>> DiscoverAsync(SiteProfile profile, CancellationToken cancellationToken = default)
    {
        var endpoints = new List<ApiEndpoint>();
        
        _logger.LogInformation("Discovering APIs for {Url}", profile.BaseUrl);

        // Test known endpoints from probing
        foreach (var path in profile.DetectedApiEndpoints)
        {
            var endpoint = await TestEndpointAsync(profile, path, cancellationToken);
            if (endpoint != null)
            {
                endpoints.Add(endpoint);
            }
        }

        // Try common API paths
        foreach (var path in CommonApiPaths)
        {
            if (profile.DetectedApiEndpoints.Any(e => e.Contains(path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var endpoint = await TestEndpointAsync(profile, path, cancellationToken);
            if (endpoint != null)
            {
                endpoints.Add(endpoint);
            }
        }

        // If GraphQL detected, try introspection
        if (profile.HasGraphQL)
        {
            var graphqlEndpoints = await DiscoverGraphQLAsync(profile, cancellationToken);
            endpoints.AddRange(graphqlEndpoints);
        }

        // Try to find search endpoint
        var searchEndpoint = await DiscoverSearchEndpointAsync(profile, endpoints, cancellationToken);
        if (searchEndpoint != null && !endpoints.Any(e => e.Purpose == EndpointPurpose.Search))
        {
            endpoints.Add(searchEndpoint);
        }

        return endpoints.DistinctBy(e => e.Url.ToString()).ToList();
    }

    private async Task<ApiEndpoint?> TestEndpointAsync(SiteProfile profile, string path, CancellationToken ct)
    {
        try
        {
            var url = BuildUrl(profile.BaseUrl, path);
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request, profile);

            using var response = await _httpClient.SendAsync(request, ct);
            
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            // Determine API type
            var apiType = ApiType.Custom;
            if (contentType.Contains("json"))
            {
                if (content.Contains("__typename") || content.Contains("\"data\":{"))
                    apiType = ApiType.GraphQL;
                else
                    apiType = ApiType.REST;
            }

            // Determine purpose
            var purpose = DeterminePurpose(path, content);

            return new ApiEndpoint
            {
                Url = url,
                Type = apiType,
                Method = HttpMethod.Get,
                Purpose = purpose,
                SampleResponse = content.Length > 500 ? content[..500] : content,
                Confidence = 70
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to test endpoint {Path}", path);
            return null;
        }
    }

    private async Task<IReadOnlyList<ApiEndpoint>> DiscoverGraphQLAsync(SiteProfile profile, CancellationToken ct)
    {
        var endpoints = new List<ApiEndpoint>();
        var graphqlPaths = new[] { "/graphql", "/api/graphql", "/gql", "/api" };

        foreach (var path in graphqlPaths)
        {
            try
            {
                var url = BuildUrl(profile.BaseUrl, path);
                
                // Try introspection query
                var introspectionQuery = "{ __schema { types { name } } }";
                var requestUrl = new Uri($"{url}?query={Uri.EscapeDataString(introspectionQuery)}");

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                AddHeaders(request, profile);

                using var response = await _httpClient.SendAsync(request, ct);
                
                if (!response.IsSuccessStatusCode)
                    continue;

                var content = await response.Content.ReadAsStringAsync(ct);
                
                if (content.Contains("__schema") || content.Contains("types"))
                {
                    _logger.LogInformation("Found GraphQL endpoint at {Path}", path);

                    // Try to discover specific queries
                    var searchEndpoint = await TryGraphQLSearchAsync(profile, url, ct);
                    if (searchEndpoint != null)
                        endpoints.Add(searchEndpoint);

                    var episodeEndpoint = await TryGraphQLEpisodesAsync(profile, url, ct);
                    if (episodeEndpoint != null)
                        endpoints.Add(episodeEndpoint);

                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GraphQL discovery failed for {Path}", path);
            }
        }

        return endpoints;
    }

    private async Task<ApiEndpoint?> TryGraphQLSearchAsync(SiteProfile profile, Uri apiUrl, CancellationToken ct)
    {
        var searchQueries = new[]
        {
            // AllAnime-style
            "query($search: SearchInput) { shows(search: $search) { edges { _id name } } }",
            "query($search: SearchInput) { mangas(search: $search) { edges { _id name } } }",
            // Common patterns
            "query($query: String!) { search(query: $query) { id title } }",
            "query($q: String!) { anime(search: $q) { id title } }",
            "query($q: String!) { manga(search: $q) { id title } }"
        };

        foreach (var query in searchQueries)
        {
            try
            {
                var variables = "{\"search\":{\"query\":\"naruto\"},\"query\":\"naruto\",\"q\":\"naruto\"}";
                var requestUrl = new Uri($"{apiUrl}?query={Uri.EscapeDataString(query)}&variables={Uri.EscapeDataString(variables)}");

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                AddHeaders(request, profile);

                using var response = await _httpClient.SendAsync(request, ct);
                
                if (!response.IsSuccessStatusCode)
                    continue;

                var content = await response.Content.ReadAsStringAsync(ct);
                
                // Check if we got valid results
                if (content.Contains("edges") || content.Contains("results") || 
                    content.Contains("\"id\"") || content.Contains("\"_id\""))
                {
                    return new ApiEndpoint
                    {
                        Url = apiUrl,
                        Type = ApiType.GraphQL,
                        Method = HttpMethod.Get,
                        Purpose = EndpointPurpose.Search,
                        SampleQuery = query,
                        SampleResponse = content.Length > 500 ? content[..500] : content,
                        Confidence = 90
                    };
                }
            }
            catch
            {
                // Continue to next query
            }
        }

        return null;
    }

    private async Task<ApiEndpoint?> TryGraphQLEpisodesAsync(SiteProfile profile, Uri apiUrl, CancellationToken ct)
    {
        var episodeQueries = new[]
        {
            // AllAnime-style
            "query($showId: String!) { show(_id: $showId) { _id availableEpisodesDetail } }",
            "query($mangaId: String!) { manga(_id: $mangaId) { _id availableChaptersDetail } }",
            // Common patterns
            "query($id: ID!) { anime(id: $id) { episodes { number title } } }",
            "query($id: ID!) { manga(id: $id) { chapters { number title } } }"
        };

        foreach (var query in episodeQueries)
        {
            try
            {
                // We need a valid ID, so just check if the query structure is accepted
                var variables = "{\"showId\":\"test\",\"mangaId\":\"test\",\"id\":\"test\"}";
                var requestUrl = new Uri($"{apiUrl}?query={Uri.EscapeDataString(query)}&variables={Uri.EscapeDataString(variables)}");

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                AddHeaders(request, profile);

                using var response = await _httpClient.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);
                
                // If we get a structured response (even with null data), the query is valid
                if (content.Contains("\"data\"") && !content.Contains("Cannot query"))
                {
                    var purpose = query.Contains("episode") || query.Contains("show") 
                        ? EndpointPurpose.Episodes 
                        : EndpointPurpose.Chapters;

                    return new ApiEndpoint
                    {
                        Url = apiUrl,
                        Type = ApiType.GraphQL,
                        Method = HttpMethod.Get,
                        Purpose = purpose,
                        SampleQuery = query,
                        Confidence = 80
                    };
                }
            }
            catch
            {
                // Continue
            }
        }

        return null;
    }

    private async Task<ApiEndpoint?> DiscoverSearchEndpointAsync(
        SiteProfile profile, 
        IReadOnlyList<ApiEndpoint> existingEndpoints, 
        CancellationToken ct)
    {
        var searchPaths = new[]
        {
            "/api/search?q=", "/api/search?query=", "/search?q=",
            "/api/anime/search?q=", "/api/manga/search?q="
        };

        foreach (var path in searchPaths)
        {
            foreach (var sample in SearchQuerySamples)
            {
                try
                {
                    var url = BuildUrl(profile.BaseUrl, path + Uri.EscapeDataString(sample));
                    
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    AddHeaders(request, profile);

                    using var response = await _httpClient.SendAsync(request, ct);
                    
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var content = await response.Content.ReadAsStringAsync(ct);
                    
                    // Check for search results
                    if (content.Contains("results") || content.Contains("data") || 
                        content.Contains("items") || content.Contains("\"id\""))
                    {
                        var basePath = path.Split('?')[0];
                        return new ApiEndpoint
                        {
                            Url = BuildUrl(profile.BaseUrl, basePath),
                            Type = ApiType.REST,
                            Method = HttpMethod.Get,
                            Purpose = EndpointPurpose.Search,
                            SampleQuery = $"?q={sample}",
                            SampleResponse = content.Length > 500 ? content[..500] : content,
                            Confidence = 85
                        };
                    }
                }
                catch
                {
                    // Continue
                }
            }
        }

        return null;
    }

    private static Uri BuildUrl(Uri baseUrl, string path)
    {
        if (path.StartsWith("http"))
            return new Uri(path);
            
        return new Uri(baseUrl, path.StartsWith("/") ? path : "/" + path);
    }

    private static void AddHeaders(HttpRequestMessage request, SiteProfile profile)
    {
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0");
        request.Headers.Referrer = profile.BaseUrl;
        request.Headers.Accept.ParseAdd("application/json, */*");
        
        foreach (var (key, value) in profile.RequiredHeaders)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static EndpointPurpose DeterminePurpose(string path, string content)
    {
        var lowerPath = path.ToLowerInvariant();
        var lowerContent = content.ToLowerInvariant();

        if (lowerPath.Contains("search") || lowerPath.Contains("query"))
            return EndpointPurpose.Search;
        if (lowerPath.Contains("episode") || lowerContent.Contains("episode"))
            return EndpointPurpose.Episodes;
        if (lowerPath.Contains("chapter") || lowerContent.Contains("chapter"))
            return EndpointPurpose.Chapters;
        if (lowerPath.Contains("stream") || lowerPath.Contains("source") || lowerPath.Contains("watch"))
            return EndpointPurpose.Streams;
        if (lowerPath.Contains("page") || lowerPath.Contains("image"))
            return EndpointPurpose.Pages;
        if (lowerPath.Contains("info") || lowerPath.Contains("detail"))
            return EndpointPurpose.Details;

        return EndpointPurpose.Unknown;
    }
}
