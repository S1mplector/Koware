// Author: Ilgaz Mehmetoğlu
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Koware.Application.Abstractions;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Koware.Browser.Services;

/// <summary>
/// Service providing access to anime and manga catalogs for the browser UI.
/// Initializes catalogs using user configuration from ~/.config/koware.
/// </summary>
public sealed class CatalogService
{
    private readonly IAnimeCatalog _animeCatalog;
    private readonly IMangaCatalog _mangaCatalog;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService()
    {
        _logger = NullLogger<CatalogService>.Instance;

        // Load user configuration
        var configPath = GetConfigPath();
        var (animeOptions, mangaOptions, gogoOptions, toggleOptions) = LoadConfiguration(configPath);

        // Create HTTP clients
        var animeHttpClient = CreateHttpClient(animeOptions);
        var mangaHttpClient = CreateHttpClient(mangaOptions);
        var gogoHttpClient = CreateHttpClient();

        // Create catalogs
        var allAnimeCatalog = new AllAnimeCatalog(
            animeHttpClient,
            Options.Create(animeOptions),
            NullLogger<AllAnimeCatalog>.Instance);

        var gogoAnimeCatalog = new GogoAnimeCatalog(
            gogoHttpClient,
            Options.Create(gogoOptions),
            NullLogger<GogoAnimeCatalog>.Instance);

        _animeCatalog = new MultiSourceAnimeCatalog(
            allAnimeCatalog,
            gogoAnimeCatalog,
            Options.Create(toggleOptions),
            NullLogger<MultiSourceAnimeCatalog>.Instance);

        _mangaCatalog = new AllMangaCatalog(
            mangaHttpClient,
            Options.Create(mangaOptions),
            NullLogger<AllMangaCatalog>.Instance);
    }

    public async Task<IReadOnlyCollection<Anime>> SearchAnimeAsync(string query, CancellationToken ct = default)
    {
        try
        {
            return await _animeCatalog.SearchAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search anime for query: {Query}", query);
            return Array.Empty<Anime>();
        }
    }

    public async Task<IReadOnlyCollection<Manga>> SearchMangaAsync(string query, CancellationToken ct = default)
    {
        try
        {
            return await _mangaCatalog.SearchAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search manga for query: {Query}", query);
            return Array.Empty<Manga>();
        }
    }

    public async Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken ct = default)
    {
        try
        {
            return await _animeCatalog.GetEpisodesAsync(anime, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get episodes for anime: {Title}", anime.Title);
            return Array.Empty<Episode>();
        }
    }

    public async Task<IReadOnlyCollection<Chapter>> GetChaptersAsync(Manga manga, CancellationToken ct = default)
    {
        try
        {
            return await _mangaCatalog.GetChaptersAsync(manga, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get chapters for manga: {Title}", manga.Title);
            return Array.Empty<Chapter>();
        }
    }

    public async Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken ct = default)
    {
        try
        {
            return await _animeCatalog.GetStreamsAsync(episode, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get streams for episode: {Title}", episode.Title);
            return Array.Empty<StreamLink>();
        }
    }

    public async Task<IReadOnlyCollection<ChapterPage>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        try
        {
            return await _mangaCatalog.GetPagesAsync(chapter, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pages for chapter: {Title}", chapter.Title);
            return Array.Empty<ChapterPage>();
        }
    }

    private static string GetConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "koware", "appsettings.user.json");
    }

    private static (AllAnimeOptions anime, AllMangaOptions manga, GogoAnimeOptions gogo, ProviderToggleOptions toggles) LoadConfiguration(string path)
    {
        // Initialize with working defaults (same as CLI bundled config)
        var animeOptions = new AllAnimeOptions
        {
            Enabled = true,  // Must be enabled!
            BaseHost = "allanime.to",
            ApiBase = "https://api.allanime.day",
            Referer = "https://allanime.to",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0",
            TranslationType = "sub",
            SearchLimit = 20
        };
        var mangaOptions = new AllMangaOptions
        {
            Enabled = true,  // Must be enabled!
            BaseHost = "allmanga.to",
            ApiBase = "https://api.allanime.day",
            Referer = "https://allmanga.to",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/121.0",
            TranslationType = "sub",
            SearchLimit = 20
        };
        var gogoOptions = new GogoAnimeOptions();
        var toggleOptions = new ProviderToggleOptions();

        if (!File.Exists(path))
        {
            return (animeOptions, mangaOptions, gogoOptions, toggleOptions);
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("AllAnime", out var allAnime))
            {
                // Check Enabled flag from config, default to true if not specified
                animeOptions.Enabled = allAnime.TryGetProperty("Enabled", out var en) ? en.GetBoolean() : true;
                animeOptions.ApiBase = allAnime.TryGetProperty("ApiBase", out var ab) && !string.IsNullOrWhiteSpace(ab.GetString()) ? ab.GetString() : animeOptions.ApiBase;
                animeOptions.BaseHost = allAnime.TryGetProperty("BaseHost", out var bh) && !string.IsNullOrWhiteSpace(bh.GetString()) ? bh.GetString() : animeOptions.BaseHost;
                animeOptions.Referer = allAnime.TryGetProperty("Referer", out var rf) && !string.IsNullOrWhiteSpace(rf.GetString()) ? rf.GetString() : animeOptions.Referer;
                animeOptions.UserAgent = allAnime.TryGetProperty("UserAgent", out var ua) && !string.IsNullOrWhiteSpace(ua.GetString()) ? ua.GetString() : animeOptions.UserAgent;
                animeOptions.TranslationType = allAnime.TryGetProperty("TranslationType", out var tt) ? tt.GetString() ?? "sub" : "sub";
                animeOptions.SearchLimit = allAnime.TryGetProperty("SearchLimit", out var sl) ? sl.GetInt32() : 20;
            }

            if (root.TryGetProperty("AllManga", out var allManga))
            {
                // Check Enabled flag from config, default to true if not specified
                mangaOptions.Enabled = allManga.TryGetProperty("Enabled", out var en) ? en.GetBoolean() : true;
                mangaOptions.ApiBase = allManga.TryGetProperty("ApiBase", out var ab) && !string.IsNullOrWhiteSpace(ab.GetString()) ? ab.GetString() : mangaOptions.ApiBase;
                mangaOptions.BaseHost = allManga.TryGetProperty("BaseHost", out var bh) && !string.IsNullOrWhiteSpace(bh.GetString()) ? bh.GetString() : mangaOptions.BaseHost;
                mangaOptions.Referer = allManga.TryGetProperty("Referer", out var rf) && !string.IsNullOrWhiteSpace(rf.GetString()) ? rf.GetString() : mangaOptions.Referer;
                mangaOptions.UserAgent = allManga.TryGetProperty("UserAgent", out var ua) && !string.IsNullOrWhiteSpace(ua.GetString()) ? ua.GetString() : mangaOptions.UserAgent;
                mangaOptions.TranslationType = allManga.TryGetProperty("TranslationType", out var tt) ? tt.GetString() ?? "sub" : "sub";
                mangaOptions.SearchLimit = allManga.TryGetProperty("SearchLimit", out var sl) ? sl.GetInt32() : 20;
            }

            if (root.TryGetProperty("GogoAnime", out var gogo))
            {
                gogoOptions.ApiBase = gogo.TryGetProperty("ApiBase", out var ab) ? ab.GetString() : null;
                gogoOptions.SiteBase = gogo.TryGetProperty("SiteBase", out var sb) ? sb.GetString() : null;
            }

            if (root.TryGetProperty("Providers", out var providers))
            {
                if (providers.TryGetProperty("DisabledProviders", out var disabled) && disabled.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in disabled.EnumerateArray())
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            toggleOptions.DisabledProviders.Add(name);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors, use defaults
        }

        return (animeOptions, mangaOptions, gogoOptions, toggleOptions);
    }

    private static HttpClient CreateHttpClient(AllAnimeOptions? options = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options?.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }

    private static HttpClient CreateHttpClient(AllMangaOptions options)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler);
        if (!string.IsNullOrWhiteSpace(options.Referer))
        {
            client.DefaultRequestHeaders.Referrer = new Uri(options.Referer);
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }
}
