using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Koware.Player.Win;

public partial class MainWindow : Window
{
    private readonly PlayerArguments _args;
    private static readonly HttpClient HttpClient = new();

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

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri ?? string.Empty;

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
            if (uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || uri.Contains(".ts", StringComparison.OrdinalIgnoreCase)
                || uri.Contains(".m4s", StringComparison.OrdinalIgnoreCase))
            {
                var deferral = e.GetDeferral();
                try
                {
                    await ProxyHlsRequestAsync(e, uri);
                }
                finally
                {
                    deferral.Complete();
                }

                return;
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task ProxyHlsRequestAsync(CoreWebView2WebResourceRequestedEventArgs e, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(new HttpMethod(e.Request.Method), uri);

            if (!string.IsNullOrWhiteSpace(_args.UserAgent))
            {
                httpRequest.Headers.UserAgent.ParseAdd(_args.UserAgent);
            }

            if (!string.IsNullOrWhiteSpace(_args.Referer) && Uri.TryCreate(_args.Referer, UriKind.Absolute, out var refUri))
            {
                httpRequest.Headers.Referrer = refUri;
                httpRequest.Headers.TryAddWithoutValidation("Origin", _args.Referer.TrimEnd('/'));
            }

            var rangeHeader = e.Request.Headers.GetHeader("Range");
            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                httpRequest.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }

            if (uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                httpRequest.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl,application/x-mpegURL,application/json;q=0.8,*/*;q=0.5");
            }
            else
            {
                httpRequest.Headers.Accept.ParseAdd("video/mp2t,application/octet-stream,*/*;q=0.5");
            }

            httpRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            using var response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

            var statusCode = (int)response.StatusCode;
            var reasonPhrase = response.ReasonPhrase ?? string.Empty;

            var contentBytes = await response.Content.ReadAsByteArrayAsync();
            var contentStream = new MemoryStream(contentBytes);

            var headersBuilder = new StringBuilder();
            foreach (var header in response.Headers)
            {
                headersBuilder.Append(header.Key)
                    .Append(": ")
                    .Append(string.Join(", ", header.Value))
                    .Append("\r\n");
            }

            foreach (var header in response.Content.Headers)
            {
                headersBuilder.Append(header.Key)
                    .Append(": ")
                    .Append(string.Join(", ", header.Value))
                    .Append("\r\n");
            }

            var core = PlayerView.CoreWebView2;
            if (core is null)
            {
                return;
            }

            var env = core.Environment;
            var webResponse = env.CreateWebResourceResponse(contentStream, statusCode, reasonPhrase, headersBuilder.ToString());
            e.Response = webResponse;
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
