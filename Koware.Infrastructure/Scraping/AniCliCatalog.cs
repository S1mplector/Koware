using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.Scraping;

public sealed class AniCliCatalog : IAnimeCatalog
{
    private readonly HttpClient _httpClient;
    private readonly AniCliOptions _options;
    private readonly ILogger<AniCliCatalog> _logger;

    public AniCliCatalog(HttpClient httpClient, IOptions<AniCliOptions> options, ILogger<AniCliCatalog> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null && Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            _httpClient.BaseAddress = baseUri;
        }
    }

    public Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Searching for {Query} with base url {Base}", query, _options.BaseUrl);

        var slug = Slugify(query);
        var anime = new Anime(
            new AnimeId($"ani-cli:{slug}"),
            query.Trim(),
            "Placeholder result generated until real scraping is wired.",
            new Uri($"{ResolveBaseUrl()}/{slug}"),
            Array.Empty<Episode>());

        return Task.FromResult<IReadOnlyCollection<Anime>>(new[] { anime });
    }

    public Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var episodes = Enumerable
            .Range(1, _options.SampleEpisodeCount)
            .Select(number => new Episode(
                new EpisodeId($"{anime.Id}:ep-{number}"),
                $"Episode {number}",
                number,
                new Uri($"{anime.DetailPage.AbsoluteUri}/episode-{number}")))
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<Episode>>(episodes);
    }

    public Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseUrl = ResolveBaseUrl();
        var streams = new[]
        {
            new StreamLink(new Uri($"{baseUrl}/{episode.Id}/1080p.mp4"), "1080p", "ani-cli stub"),
            new StreamLink(new Uri($"{baseUrl}/{episode.Id}/720p.mp4"), "720p", "ani-cli stub"),
            new StreamLink(new Uri($"{baseUrl}/{episode.Id}/480p.mp4"), "480p", "ani-cli stub")
        };

        return Task.FromResult<IReadOnlyCollection<StreamLink>>(streams);
    }

    private string ResolveBaseUrl() => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? "https://ani-cli.example"
        : _options.BaseUrl.TrimEnd('/');

    private static string Slugify(string value) => value
        .Trim()
        .ToLowerInvariant()
        .Replace(' ', '-');
}
