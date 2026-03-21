// Author: Ilgaz Mehmetoğlu
using Koware.Application.Abstractions;
using Koware.Autoconfig.Runtime;
using Koware.Autoconfig.Storage;
using Koware.Infrastructure.Configuration;
using Koware.Infrastructure.Scraping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Autoconfig.DependencyInjection;

/// <summary>
/// Registers the shared catalog composition used by Koware frontends.
/// </summary>
public static class KowareCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Register aggregate anime and manga catalogs backed by built-in and dynamic providers.
    /// </summary>
    public static IServiceCollection AddKowareCatalogs(this IServiceCollection services)
    {
        services.AddSingleton<AggregateAnimeCatalog>(sp =>
        {
            var builtIns = new List<AggregateAnimeCatalog.BuiltInAnimeProvider>();
            var allAnimeOptions = sp.GetRequiredService<IOptions<AllAnimeOptions>>().Value;
            var hiAnimeOptions = sp.GetRequiredService<IOptions<HiAnimeOptions>>().Value;
            var nineAnimeOptions = sp.GetRequiredService<IOptions<NineAnimeOptions>>().Value;
            var hanimeOptions = sp.GetRequiredService<IOptions<HanimeOptions>>().Value;

            if (allAnimeOptions.IsConfigured)
            {
                builtIns.Add(new AggregateAnimeCatalog.BuiltInAnimeProvider(
                    "allanime",
                    "AllAnime",
                    sp.GetRequiredService<AllAnimeCatalog>()));
            }

            if (hiAnimeOptions.IsConfigured)
            {
                builtIns.Add(new AggregateAnimeCatalog.BuiltInAnimeProvider(
                    "hianime",
                    "HiAnime",
                    sp.GetRequiredService<HiAnimeCatalog>()));
            }

            if (nineAnimeOptions.IsConfigured)
            {
                builtIns.Add(new AggregateAnimeCatalog.BuiltInAnimeProvider(
                    "9anime",
                    "9anime",
                    sp.GetRequiredService<NineAnimeCatalog>()));
            }

            if (hanimeOptions.IsConfigured)
            {
                builtIns.Add(new AggregateAnimeCatalog.BuiltInAnimeProvider(
                    "hanime",
                    "Hanime",
                    sp.GetRequiredService<HanimeCatalog>()));
            }

            return new AggregateAnimeCatalog(
                builtIns,
                sp.GetRequiredService<IProviderStore>(),
                sp.GetRequiredService<ITransformEngine>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddSingleton<AggregateMangaCatalog>(sp =>
        {
            var builtIns = new List<AggregateMangaCatalog.BuiltInMangaProvider>();
            var allMangaOptions = sp.GetRequiredService<IOptions<AllMangaOptions>>().Value;
            var nhentaiOptions = sp.GetRequiredService<IOptions<NhentaiOptions>>().Value;
            var mangaDexOptions = sp.GetRequiredService<IOptions<MangaDexOptions>>().Value;

            if (allMangaOptions.IsConfigured)
            {
                builtIns.Add(new AggregateMangaCatalog.BuiltInMangaProvider(
                    "allmanga",
                    "AllManga",
                    sp.GetRequiredService<AllMangaCatalog>()));
            }

            if (nhentaiOptions.IsConfigured)
            {
                builtIns.Add(new AggregateMangaCatalog.BuiltInMangaProvider(
                    "nhentai",
                    "nhentai",
                    sp.GetRequiredService<NhentaiCatalog>()));
            }

            if (mangaDexOptions.IsConfigured)
            {
                builtIns.Add(new AggregateMangaCatalog.BuiltInMangaProvider(
                    "mangadex",
                    "MangaDex",
                    sp.GetRequiredService<MangaDexCatalog>()));
            }

            return new AggregateMangaCatalog(
                builtIns,
                sp.GetRequiredService<IProviderStore>(),
                sp.GetRequiredService<ITransformEngine>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddSingleton<IAnimeCatalog>(sp => sp.GetRequiredService<AggregateAnimeCatalog>());
        services.AddSingleton<IMangaCatalog>(sp => sp.GetRequiredService<AggregateMangaCatalog>());
        return services;
    }
}
