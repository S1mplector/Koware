// Author: Ilgaz Mehmetoğlu
// WebView2 host that configures the manga reader, proxies image requests, and bridges messages.
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Koware.Reader.Win.Rendering;
using Koware.Reader.Win.Startup;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Koware.Reader.Win.Reading;

public sealed class WebViewReaderHost
{
    private readonly ReaderArguments _args;
    private readonly WebView2 _view;
    private readonly Dispatcher _dispatcher;
    private static readonly HttpClient HttpClient = new();
    private static readonly string[] ProxyExtensions =
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".bmp",
        ".ico",
        ".avif",
        ".svg"
    };

    public WebViewReaderHost(ReaderArguments args, WebView2 view, Dispatcher dispatcher)
    {
        _args = args;
        _view = view;
        _dispatcher = dispatcher;
    }

    public async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Koware",
            "Reader",
            "WebView2");

        _view.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = userDataFolder,
            AdditionalBrowserArguments = "--disable-web-security --disable-features=IsolateOrigins,site-per-process --ignore-certificate-errors"
        };

        await _view.EnsureCoreWebView2Async();
        var core = _view.CoreWebView2 ?? throw new InvalidOperationException("WebView2 core could not be initialized.");

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        if (!string.IsNullOrWhiteSpace(_args.UserAgent))
        {
            core.Settings.UserAgent = _args.UserAgent;
        }

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        var html = ReaderHtmlBuilder.Build(_args);
        core.NavigateToString(html);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "nav")
            {
                var direction = root.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() : null;
                var path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                
                if (!string.IsNullOrWhiteSpace(direction) && !string.IsNullOrWhiteSpace(path))
                {
                    File.WriteAllText(path, direction);
                    _dispatcher.Invoke(() => Application.Current.MainWindow?.Close());
                }
            }
        }
        catch
        {
            // Ignore parsing errors
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
                if (Uri.TryCreate(_args.Referer, UriKind.Absolute, out var refererUri))
                {
                    e.Request.Headers.SetHeader("Origin", refererUri.GetLeftPart(UriPartial.Authority));
                }
            }

            if (ShouldProxy(uri))
            {
                var deferral = e.GetDeferral();
                try
                {
                    await ProxyImageRequestAsync(e, uri);
                }
                finally
                {
                    deferral.Complete();
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task ProxyImageRequestAsync(CoreWebView2WebResourceRequestedEventArgs e, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);

            if (!string.IsNullOrWhiteSpace(_args.UserAgent))
            {
                httpRequest.Headers.UserAgent.ParseAdd(_args.UserAgent);
            }

            if (!string.IsNullOrWhiteSpace(_args.Referer) && Uri.TryCreate(_args.Referer, UriKind.Absolute, out var refUri))
            {
                httpRequest.Headers.Referrer = refUri;
                httpRequest.Headers.TryAddWithoutValidation("Origin", refUri.GetLeftPart(UriPartial.Authority));
            }

            httpRequest.Headers.Accept.ParseAdd("image/webp,image/apng,image/*,*/*;q=0.8");
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

            var core = _view.CoreWebView2;
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

    private static bool ShouldProxy(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        var lower = uri.ToLowerInvariant();
        
        // Proxy all image requests
        if (ProxyExtensions.Any(ext => lower.Contains(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Also proxy URLs that look like manga image CDN paths
        return lower.Contains("/manga/", StringComparison.OrdinalIgnoreCase)
               || lower.Contains("/chapter/", StringComparison.OrdinalIgnoreCase)
               || lower.Contains("/img/", StringComparison.OrdinalIgnoreCase);
    }
}
