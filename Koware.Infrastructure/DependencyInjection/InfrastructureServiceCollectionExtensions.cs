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
            services.Configure<HiAnimeOptions>(configuration.GetSection("HiAnime"));
            services.Configure<NineAnimeOptions>(configuration.GetSection("NineAnime"));
        }
        else
        {
            services.Configure<AllAnimeOptions>(_ => { });
            services.Configure<AllMangaOptions>(_ => { });
            services.Configure<HiAnimeOptions>(_ => { });
            services.Configure<NineAnimeOptions>(_ => { });
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
            ConfigureCommonClient(client, options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        }).ConfigurePrimaryHttpMessageHandler(CreateDefaultHandler);

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
            ConfigureCommonClient(client, options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        }).ConfigurePrimaryHttpMessageHandler(CreateDefaultHandler);

        services.AddHttpClient<HiAnimeCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<HiAnimeOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl) && Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }
            if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer.Trim(), UriKind.Absolute, out var refererUri))
            {
                client.DefaultRequestHeaders.Referrer = refererUri;
            }
            ConfigureCommonClient(client, options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }).ConfigurePrimaryHttpMessageHandler(CreateDefaultHandler);

        services.AddHttpClient<NineAnimeCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<NineAnimeOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl) && Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }
            if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer.Trim(), UriKind.Absolute, out var refererUri))
            {
                client.DefaultRequestHeaders.Referrer = refererUri;
            }
            ConfigureCommonClient(client, options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }).ConfigurePrimaryHttpMessageHandler(CreateDefaultHandler);

        services.AddSingleton<AllAnimeCatalog>();
        services.AddSingleton<AllMangaCatalog>();
        services.AddSingleton<HiAnimeCatalog>();
        services.AddSingleton<NineAnimeCatalog>();
        services.AddSingleton<IAnimeCatalog>(sp => sp.GetRequiredService<AllAnimeCatalog>());
        services.AddSingleton<IMangaCatalog>(sp => sp.GetRequiredService<AllMangaCatalog>());
        return services;
    }

    private static void ConfigureCommonClient(HttpClient client, string userAgent)
    {
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }

    private static HttpMessageHandler CreateDefaultHandler()
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
    }
}
