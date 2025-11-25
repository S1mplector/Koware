// Author: Ilgaz Mehmetoğlu
// WebView2 host that configures playback, proxies requests, and bridges messages for the player window.
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Koware.Player.Win.Rendering;
using Koware.Player.Win.Startup;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Koware.Player.Win.Playback;

public sealed class WebViewPlayerHost
{
    private readonly PlayerArguments _args;
    private readonly WebView2 _view;
    private readonly Dispatcher _dispatcher;
    private static readonly HttpClient HttpClient = new();
    private static readonly string[] ProxySkipHosts = { "cdn.jsdelivr.net" };
    private static readonly string[] ProxyExtensions =
    {
        ".m3u8",
        ".mpd",
        ".ts",
        ".m4s",
        ".mp4",
        ".webm",
        ".aac",
        ".mp3",
        ".mov",
        ".cmfv",
        ".cmfa",
        ".vtt",
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".ico",
        ".bmp",
        ".txt",
        ".css",
        ".js",
        ".html"
    };

    public WebViewPlayerHost(PlayerArguments args, WebView2 view, Dispatcher dispatcher)
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
            "Player",
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
        core.WebResourceResponseReceived += OnWebResourceResponseReceived;
        core.WebMessageReceived += OnWebMessageReceived;

        var html = HtmlPageBuilder.Build(_args);
        core.NavigateToString(html);
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

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        var uri = e.Request.Uri;
        if (ShouldProxy(uri))
        {
            _ = _dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var status = e.Response.StatusCode;
                    var reason = e.Response.ReasonPhrase;
                    _view.CoreWebView2?.PostWebMessageAsString($"HTTP {status} {reason} - {uri}");
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
            _view.CoreWebView2?.ExecuteScriptAsync($"window.__log && window.__log({JsonSerializer.Serialize(message)});");
        }
        catch
        {
            // ignore logging failures
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
                httpRequest.Headers.TryAddWithoutValidation("Origin", refUri.GetLeftPart(UriPartial.Authority));
            }

            var rangeHeader = e.Request.Headers.GetHeader("Range");
            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                httpRequest.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }

            var lower = uri.ToLowerInvariant();
            var isPlaylist = lower.Contains(".m3u8");

            if (isPlaylist)
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

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            if (ProxySkipHosts.Any(skip => parsed.Host.Contains(skip, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        var lower = uri.ToLowerInvariant();
        if (ProxyExtensions.Any(ext => lower.Contains(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Many hosts disguise transport segments with arbitrary extensions (seg-*.jpg/.css/etc).
        return lower.Contains("seg-", StringComparison.OrdinalIgnoreCase)
               || lower.Contains("chunk", StringComparison.OrdinalIgnoreCase)
               || lower.Contains("/_v", StringComparison.OrdinalIgnoreCase);
    }
}
