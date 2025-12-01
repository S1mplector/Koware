// Author: Ilgaz Mehmetoğlu | Summary: WPF code-behind that initializes the WebView manga reader host with parsed arguments.
using System;
using System.Windows;
using Koware.Reader.Win.Reading;
using Koware.Reader.Win.Startup;

namespace Koware.Reader.Win;

public partial class MainWindow : Window
{
    private readonly ReaderArguments _args;
    private WebViewReaderHost? _host;

    public MainWindow(ReaderArguments args)
    {
        _args = args;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Title = string.IsNullOrWhiteSpace(_args.Title) ? "Koware Reader" : _args.Title;

        try
        {
            _host = new WebViewReaderHost(_args, ReaderView, Dispatcher);
            await _host.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize the reader: {ex.Message}", "Koware Reader", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
