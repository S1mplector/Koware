// Author: Ilgaz Mehmetoğlu
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Koware.Browser.ViewModels;
using Koware.Browser.Services;
using Microsoft.Extensions.DependencyInjection;

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
        // Register catalog service (wraps infrastructure catalogs)
        services.AddSingleton<CatalogService>();
        
        // Register ViewModels
        services.AddSingleton<MainViewModel>();
    }
}
