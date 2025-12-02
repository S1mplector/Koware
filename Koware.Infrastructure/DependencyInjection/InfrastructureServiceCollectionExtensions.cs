// Author: Ilgaz Mehmetoğlu
// Summary: DI registration for infrastructure services including AllAnime client configuration.
using Koware.Application.Abstractions;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Koware.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<AllAnimeOptions>(configuration.GetSection("AllAnime"));
            services.Configure<GogoAnimeOptions>(configuration.GetSection("GogoAnime"));
            services.Configure<AllMangaOptions>(configuration.GetSection("AllManga"));
            services.Configure<ProviderToggleOptions>(configuration.GetSection("Providers"));
        }
        else
        {
            services.Configure<AllAnimeOptions>(_ => { });
            services.Configure<GogoAnimeOptions>(_ => { });
            services.Configure<AllMangaOptions>(_ => { });
            services.Configure<ProviderToggleOptions>(_ => { });
        }

        services.AddHttpClient<AllAnimeCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
            // Only configure if source is properly set up (user must provide config)
            if (!string.IsNullOrWhiteSpace(options.ApiBase))
            {
                client.BaseAddress = new Uri(options.ApiBase);
            }
            if (!string.IsNullOrWhiteSpace(options.Referer))
            {
                client.DefaultRequestHeaders.Referrer = new Uri(options.Referer);
            }
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            client.DefaultRequestVersion = HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });

        services.AddHttpClient<GogoAnimeCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<GogoAnimeOptions>>().Value;
            // Only configure if source is properly set up (user must provide config)
            if (!string.IsNullOrWhiteSpace(options.ApiBase))
            {
                client.BaseAddress = new Uri(options.ApiBase);
            }
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        });

        services.AddHttpClient<AllMangaCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AllMangaOptions>>().Value;
            // Only configure if source is properly set up (user must provide config)
            if (!string.IsNullOrWhiteSpace(options.ApiBase))
            {
                client.BaseAddress = new Uri(options.ApiBase);
            }
            if (!string.IsNullOrWhiteSpace(options.Referer))
            {
                client.DefaultRequestHeaders.Referrer = new Uri(options.Referer);
            }
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            client.DefaultRequestVersion = HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });

        services.AddSingleton<AllAnimeCatalog>();
        services.AddSingleton<GogoAnimeCatalog>();
        services.AddSingleton<AllMangaCatalog>();
        services.AddSingleton<IMangaCatalog>(sp => sp.GetRequiredService<AllMangaCatalog>());
        services.AddSingleton<IAnimeCatalog>(sp =>
        {
            var primary = sp.GetRequiredService<AllAnimeCatalog>();
            var secondary = sp.GetRequiredService<GogoAnimeCatalog>();
            var toggles = sp.GetRequiredService<IOptions<ProviderToggleOptions>>();
            var logger = sp.GetRequiredService<ILogger<MultiSourceAnimeCatalog>>();
            return new MultiSourceAnimeCatalog(primary, secondary, toggles, logger);
        });
        return services;
    }
}
