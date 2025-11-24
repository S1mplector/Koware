using Koware.Application.Abstractions;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koware.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<AllAnimeOptions>(configuration.GetSection("AllAnime"));
        }
        else
        {
            services.Configure<AllAnimeOptions>(_ => { });
        }

        services.AddHttpClient<AllAnimeCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
            client.BaseAddress = new Uri(options.ApiBase);
            client.DefaultRequestHeaders.Referrer = new Uri(options.Referer);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });

        services.AddSingleton<IAnimeCatalog, AllAnimeCatalog>();
        return services;
    }
}
