using System;
using System.Windows;
using Koware.Player.Win.Playback;
using Koware.Player.Win.Startup;

namespace Koware.Player.Win;

public partial class MainWindow : Window
{
    private readonly PlayerArguments _args;
    private WebViewPlayerHost? _host;

    public MainWindow(PlayerArguments args)
    {
        _args = args;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Title = string.IsNullOrWhiteSpace(_args.Title) ? "Koware Player" : _args.Title;

        try
        {
            _host = new WebViewPlayerHost(_args, PlayerView, Dispatcher);
            await _host.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize the player: {ex.Message}", "Koware Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
