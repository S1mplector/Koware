// Author: Ilgaz Mehmetoğlu
// Summary: DI registration for infrastructure services including AllAnime client configuration.
using Koware.Application.Abstractions;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;

namespace Koware.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<AllAnimeOptions>(configuration.GetSection("AllAnime"));
            services.Configure<AllMangaOptions>(configuration.GetSection("AllManga"));
        }
        else
        {
            services.Configure<AllAnimeOptions>(_ => { });
            services.Configure<AllMangaOptions>(_ => { });
        }

        services.AddHttpClient<AllAnimeCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
            // Only configure if source is properly set up (user must provide config)
            if (!string.IsNullOrWhiteSpace(options.ApiBase) && Uri.TryCreate(options.ApiBase.Trim(), UriKind.Absolute, out var apiBaseUri))
            {
                client.BaseAddress = apiBaseUri;
            }
            if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer.Trim(), UriKind.Absolute, out var refererUri))
            {
                client.DefaultRequestHeaders.Referrer = refererUri;
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

        services.AddHttpClient<AllMangaCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AllMangaOptions>>().Value;
            // Only configure if source is properly set up (user must provide config)
            if (!string.IsNullOrWhiteSpace(options.ApiBase) && Uri.TryCreate(options.ApiBase.Trim(), UriKind.Absolute, out var apiBaseUri))
            {
                client.BaseAddress = apiBaseUri;
            }
            if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer.Trim(), UriKind.Absolute, out var refererUri))
            {
                client.DefaultRequestHeaders.Referrer = refererUri;
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
        services.AddSingleton<AllMangaCatalog>();
        services.AddSingleton<IAnimeCatalog>(sp => sp.GetRequiredService<AllAnimeCatalog>());
        services.AddSingleton<IMangaCatalog>(sp => sp.GetRequiredService<AllMangaCatalog>());
        services.AddSingleton<IAnimeCatalog>(sp => sp.GetRequiredService<AllAnimeCatalog>());
        return services;
    }
}
