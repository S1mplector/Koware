// Author: Ilgaz Mehmetoğlu
using System.Net;
using System.Net.Http;

namespace Koware.Browser.Services;

/// <summary>
/// Fluent builder for creating pre-configured HttpClient instances.
/// </summary>
internal sealed class HttpClientBuilder
{
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    private const string DefaultAccept = "application/json, */*";
    private const string DefaultAcceptLanguage = "en-US,en;q=0.9";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private string _userAgent = DefaultUserAgent;
    private string? _referer;

    private HttpClientBuilder() { }

    public static HttpClientBuilder Create() => new();

    public HttpClientBuilder WithUserAgent(string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            _userAgent = userAgent;
        }
        return this;
    }

    public HttpClientBuilder WithReferer(string? referer)
    {
        _referer = referer;
        return this;
    }

    public HttpClient Build()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            Timeout = DefaultTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(DefaultAccept);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(DefaultAcceptLanguage);

        if (!string.IsNullOrWhiteSpace(_referer))
        {
            client.DefaultRequestHeaders.Referrer = new Uri(_referer);
        }

        return client;
    }
}
