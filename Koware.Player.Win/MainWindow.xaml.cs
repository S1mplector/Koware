using System;
using System.IO;
using System.Text.Json;
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

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Koware",
            "Player",
            "WebView2");

        PlayerView.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            UserDataFolder = userDataFolder,
            AdditionalBrowserArguments = "--disable-web-security --disable-features=IsolateOrigins,site-per-process --ignore-certificate-errors"
        };

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

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;
            core.WebMessageReceived += OnWebMessageReceived;

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
        try
        {
            if (!string.IsNullOrWhiteSpace(_args.UserAgent))
            {
                e.Request.Headers.SetHeader("User-Agent", _args.UserAgent);
            }

            if (!string.IsNullOrWhiteSpace(_args.Referer))
            {
                e.Request.Headers.SetHeader("Referer", _args.Referer);
                e.Request.Headers.SetHeader("Origin", _args.Referer);
            }

            // Strengthen accept headers for HLS manifests/segments.
            var uri = e.Request.Uri ?? string.Empty;
            if (uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                e.Request.Headers.SetHeader("Accept", "application/vnd.apple.mpegurl,application/x-mpegURL,application/json;q=0.8,*/*;q=0.5");
            }
            else if (uri.Contains(".ts", StringComparison.OrdinalIgnoreCase) || uri.Contains(".m4s", StringComparison.OrdinalIgnoreCase))
            {
                e.Request.Headers.SetHeader("Accept", "video/mp2t,application/octet-stream,*/*;q=0.5");
            }
        }
        catch
        {
            // ignored
        }
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        // Log manifest and segment responses to help debug CORS/403 issues.
        var uri = e.Request.Uri;
        if (uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || uri.Contains(".ts", StringComparison.OrdinalIgnoreCase))
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var status = e.Response.StatusCode;
                    var reason = e.Response.ReasonPhrase;
                    PlayerView.CoreWebView2.PostWebMessageAsString($"HTTP {status} {reason} - {uri}");
                }
                catch
                {
                    // ignore logging failures
                }
            });
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Bridge WebView messages into the JS log (useful for status from native side).
        var message = e.TryGetWebMessageAsString();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            PlayerView.CoreWebView2.ExecuteScriptAsync($"window.__log && window.__log({JsonSerializer.Serialize(message)});");
        }
        catch
        {
            // ignore logging failures
        }
    }
}
