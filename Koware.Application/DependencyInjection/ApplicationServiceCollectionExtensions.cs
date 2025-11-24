using Koware.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace Koware.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ScrapeOrchestrator>();
        return services;
    }
}
