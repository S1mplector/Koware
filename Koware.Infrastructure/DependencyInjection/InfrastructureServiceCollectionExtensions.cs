using Koware.Application.Abstractions;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace Koware.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<AniCliOptions>(configuration.GetSection("Scraper"));
        }
        else
        {
            services.Configure<AniCliOptions>(_ => { });
        }

        services.AddHttpClient<AniCliCatalog>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AniCliOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }

            if (!string.IsNullOrWhiteSpace(options.UserAgent))
            {
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            }
        });

        services.AddSingleton<IAnimeCatalog, AniCliCatalog>();
        return services;
    }
}
