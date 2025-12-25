// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Analysis;
using Koware.Autoconfig.Generation;
using Koware.Autoconfig.Orchestration;
using Koware.Autoconfig.Runtime;
using Koware.Autoconfig.Storage;
using Koware.Autoconfig.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Koware.Autoconfig.DependencyInjection;

/// <summary>
/// Extension methods for registering Autoconfig services.
/// </summary>
public static class AutoconfigServiceCollectionExtensions
{
    /// <summary>
    /// Add Autoconfig services to the service collection.
    /// </summary>
    public static IServiceCollection AddAutoconfigServices(this IServiceCollection services)
    {
        // Storage
        services.AddSingleton<IProviderStore, ProviderStore>();
        
        // Runtime
        services.AddSingleton<ITransformEngine, TransformEngine>();
        
        // Analysis
        services.AddTransient<ISiteProber, SiteProber>();
        services.AddTransient<IApiDiscoveryEngine, ApiDiscoveryEngine>();
        services.AddTransient<IContentPatternMatcher, ContentPatternMatcher>();
        services.AddTransient<IIntelligentPatternEngine, IntelligentPatternEngine>();
        services.AddTransient<IGraphQLIntrospector, GraphQLIntrospector>();
        
        // Generation
        services.AddSingleton<IProviderTemplateLibrary, ProviderTemplateLibrary>();
        services.AddTransient<ISchemaGenerator, SchemaGenerator>();
        
        // Validation
        services.AddTransient<IConfigValidator, ConfigValidator>();
        
        // Orchestration
        services.AddTransient<IAutoconfigOrchestrator, AutoconfigOrchestrator>();
        
        return services;
    }
}
