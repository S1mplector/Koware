using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Koware.Player.Win;

public partial class MainWindow : Window
{
    private readonly PlayerArguments _args;

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
            await PlayerView.EnsureCoreWebView2Async();
            var core = PlayerView.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            if (!string.IsNullOrWhiteSpace(_args.UserAgent))
            {
                core.Settings.UserAgent = _args.UserAgent;
            }

            if (!string.IsNullOrWhiteSpace(_args.Referer))
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnWebResourceRequested;
            }

            var html = HtmlPageBuilder.Build(_args);
            core.NavigateToString(html);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize the player: {ex.Message}", "Koware Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_args.Referer))
        {
            return;
        }

        try
        {
            e.Request.Headers.SetHeader("Referer", _args.Referer);
        }
        catch
        {
            // ignored
        }
    }
}
