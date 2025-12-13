// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Runtime;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Validation;

/// <summary>
/// Validates provider configurations by testing them with live requests.
/// </summary>
public sealed class ConfigValidator : IConfigValidator
{
    private readonly HttpClient _httpClient;
    private readonly ITransformEngine _transformEngine;
    private readonly ILogger<ConfigValidator> _logger;

    private static readonly string[] TestQueries = ["One Piece", "Naruto", "Attack on Titan"];

    public ConfigValidator(
        HttpClient httpClient,
        ITransformEngine transformEngine,
        ILogger<ConfigValidator> logger)
    {
        _httpClient = httpClient;
        _transformEngine = transformEngine;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(
        DynamicProviderConfig config,
        string? testQuery = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<ValidationCheck>();
        var query = testQuery ?? TestQueries[0];

        _logger.LogInformation("Validating provider '{Name}' with query '{Query}'", config.Name, query);

        // Check 1: Basic connectivity
        var connectivityCheck = await ValidateConnectivityAsync(config, cancellationToken);
        checks.Add(connectivityCheck);

        if (!connectivityCheck.Passed)
        {
            stopwatch.Stop();
            return ValidationResult.Failure(
                $"Connectivity check failed: {connectivityCheck.ErrorMessage}",
                checks,
                stopwatch.Elapsed);
        }

        // Check 2: Search functionality
        var searchCheck = await ValidateSearchAsync(config, query, cancellationToken);
        checks.Add(searchCheck);

        if (!searchCheck.Passed)
        {
            // Try alternative queries
            foreach (var altQuery in TestQueries.Where(q => q != query))
            {
                var altCheck = await ValidateSearchAsync(config, altQuery, cancellationToken);
                if (altCheck.Passed)
                {
                    checks.Add(altCheck);
                    searchCheck = altCheck;
                    break;
                }
            }
        }

        // Check 3: Content listing (episodes/chapters)
        if (searchCheck.Passed && !string.IsNullOrEmpty(searchCheck.SampleData))
        {
            var contentCheck = await ValidateContentListingAsync(config, searchCheck.SampleData, cancellationToken);
            checks.Add(contentCheck);

            // Check 4: Media resolution (streams/pages)
            if (contentCheck.Passed && !string.IsNullOrEmpty(contentCheck.SampleData))
            {
                var mediaCheck = await ValidateMediaResolutionAsync(config, contentCheck.SampleData, cancellationToken);
                checks.Add(mediaCheck);
            }
        }

        stopwatch.Stop();

        var allPassed = checks.All(c => c.Passed);
        var criticalPassed = checks.Take(2).All(c => c.Passed); // Connectivity and Search

        if (allPassed)
        {
            _logger.LogInformation("Validation successful for '{Name}'", config.Name);
            return ValidationResult.Success(checks, stopwatch.Elapsed);
        }

        if (criticalPassed)
        {
            _logger.LogWarning("Validation partially successful for '{Name}' - some checks failed", config.Name);
            return new ValidationResult
            {
                IsValid = true,
                Checks = checks,
                Duration = stopwatch.Elapsed,
                ErrorMessage = "Some non-critical checks failed - provider may still work"
            };
        }

        var failedChecks = checks.Where(c => !c.Passed).Select(c => c.Name);
        return ValidationResult.Failure(
            $"Validation failed: {string.Join(", ", failedChecks)}",
            checks,
            stopwatch.Elapsed);
    }

    private async Task<ValidationCheck> ValidateConnectivityAsync(
        DynamicProviderConfig config,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var baseUrl = $"https://{config.Hosts.BaseHost}";
            using var request = new HttpRequestMessage(HttpMethod.Head, baseUrl);
            request.Headers.UserAgent.ParseAdd(config.Hosts.UserAgent);

            using var response = await _httpClient.SendAsync(request, ct);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                return ValidationCheck.Pass(
                    "Connectivity",
                    $"Successfully connected to {config.Hosts.BaseHost}",
                    $"Status: {response.StatusCode}",
                    stopwatch.Elapsed);
            }

            return ValidationCheck.Fail(
                "Connectivity",
                $"Server returned {response.StatusCode}",
                $"Connection to {config.Hosts.BaseHost}",
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ValidationCheck.Fail(
                "Connectivity",
                $"Connection failed: {ex.Message}",
                $"Connection to {config.Hosts.BaseHost}",
                stopwatch.Elapsed);
        }
    }

    private async Task<ValidationCheck> ValidateSearchAsync(
        DynamicProviderConfig config,
        string query,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var catalog = new DynamicAnimeCatalog(
                config,
                _httpClient,
                _transformEngine,
                new LoggerFactory().CreateLogger<DynamicAnimeCatalog>());

            var results = await catalog.SearchAsync(query, ct);
            stopwatch.Stop();

            if (results.Count > 0)
            {
                var firstResult = results.First();
                return ValidationCheck.Pass(
                    "Search",
                    $"Found {results.Count} results for '{query}'",
                    firstResult.Id.Value, // Return ID for next check
                    stopwatch.Elapsed);
            }

            return ValidationCheck.Fail(
                "Search",
                $"No results found for '{query}'",
                $"Search query: {query}",
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ValidationCheck.Fail(
                "Search",
                $"Search failed: {ex.Message}",
                $"Search query: {query}",
                stopwatch.Elapsed);
        }
    }

    private async Task<ValidationCheck> ValidateContentListingAsync(
        DynamicProviderConfig config,
        string contentId,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (config.Type == ProviderType.Manga)
            {
                var catalog = new DynamicMangaCatalog(
                    config,
                    _httpClient,
                    _transformEngine,
                    new LoggerFactory().CreateLogger<DynamicMangaCatalog>());

                var manga = new Koware.Domain.Models.Manga(
                    new Koware.Domain.Models.MangaId(contentId),
                    "Test",
                    null, null, null,
                    Array.Empty<Koware.Domain.Models.Chapter>());

                var chapters = await catalog.GetChaptersAsync(manga, ct);
                stopwatch.Stop();

                if (chapters.Count > 0)
                {
                    var firstChapter = chapters.First();
                    return ValidationCheck.Pass(
                        "Chapters",
                        $"Found {chapters.Count} chapters",
                        firstChapter.Id.Value,
                        stopwatch.Elapsed);
                }

                return ValidationCheck.Fail(
                    "Chapters",
                    "No chapters found",
                    duration: stopwatch.Elapsed);
            }
            else
            {
                var catalog = new DynamicAnimeCatalog(
                    config,
                    _httpClient,
                    _transformEngine,
                    new LoggerFactory().CreateLogger<DynamicAnimeCatalog>());

                var anime = new Koware.Domain.Models.Anime(
                    new Koware.Domain.Models.AnimeId(contentId),
                    "Test",
                    null, null, null,
                    Array.Empty<Koware.Domain.Models.Episode>());

                var episodes = await catalog.GetEpisodesAsync(anime, ct);
                stopwatch.Stop();

                if (episodes.Count > 0)
                {
                    var firstEpisode = episodes.First();
                    return ValidationCheck.Pass(
                        "Episodes",
                        $"Found {episodes.Count} episodes",
                        $"{contentId}:ep-{firstEpisode.Number}",
                        stopwatch.Elapsed);
                }

                return ValidationCheck.Fail(
                    "Episodes",
                    "No episodes found",
                    duration: stopwatch.Elapsed);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ValidationCheck.Fail(
                config.Type == ProviderType.Manga ? "Chapters" : "Episodes",
                $"Content listing failed: {ex.Message}",
                duration: stopwatch.Elapsed);
        }
    }

    private async Task<ValidationCheck> ValidateMediaResolutionAsync(
        DynamicProviderConfig config,
        string contentId,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (config.Type == ProviderType.Manga)
            {
                var catalog = new DynamicMangaCatalog(
                    config,
                    _httpClient,
                    _transformEngine,
                    new LoggerFactory().CreateLogger<DynamicMangaCatalog>());

                var chapter = new Koware.Domain.Models.Chapter(
                    new Koware.Domain.Models.ChapterId(contentId),
                    "Test",
                    1,
                    null);

                var pages = await catalog.GetPagesAsync(chapter, ct);
                stopwatch.Stop();

                if (pages.Count > 0)
                {
                    return ValidationCheck.Pass(
                        "Pages",
                        $"Found {pages.Count} pages",
                        pages.First().ImageUrl.ToString(),
                        stopwatch.Elapsed);
                }

                return ValidationCheck.Fail(
                    "Pages",
                    "No pages found",
                    duration: stopwatch.Elapsed);
            }
            else
            {
                var catalog = new DynamicAnimeCatalog(
                    config,
                    _httpClient,
                    _transformEngine,
                    new LoggerFactory().CreateLogger<DynamicAnimeCatalog>());

                var episode = new Koware.Domain.Models.Episode(
                    new Koware.Domain.Models.EpisodeId(contentId),
                    "Test",
                    1,
                    null);

                var streams = await catalog.GetStreamsAsync(episode, ct);
                stopwatch.Stop();

                if (streams.Count > 0)
                {
                    return ValidationCheck.Pass(
                        "Streams",
                        $"Found {streams.Count} streams",
                        streams.First().Url.ToString(),
                        stopwatch.Elapsed);
                }

                return ValidationCheck.Fail(
                    "Streams",
                    "No streams found",
                    duration: stopwatch.Elapsed);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ValidationCheck.Fail(
                config.Type == ProviderType.Manga ? "Pages" : "Streams",
                $"Media resolution failed: {ex.Message}",
                duration: stopwatch.Elapsed);
        }
    }
}
