// Author: Ilgaz Mehmetoğlu
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Koware.Application.DependencyInjection;
using Koware.Application.Environment;
using Koware.Autoconfig.DependencyInjection;
using Koware.Browser.Services;
using Koware.Browser.ViewModels;
using Koware.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Koware.Browser;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(KowarePaths.GetUserConfigFilePath(), optional: true, reloadOnChange: true)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.AddAutoconfigServices();
        services.AddKowareCatalogs();
        services.AddHttpClient();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddSingleton<CatalogService>();
        services.AddSingleton<MainViewModel>();
    }
}
