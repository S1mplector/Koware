// Author: Ilgaz Mehmetoğlu
// Shared stub HTTP message handler for autoconfig tests.
using System.Net;

namespace Koware.Tests.Autoconfig;

/// <summary>
/// Stub HTTP message handler for testing autoconfig components.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<Uri, bool> predicate, Func<HttpResponseMessage> responseFactory)> _responses = new();
    private string? _defaultResponse;
    private HttpStatusCode _defaultStatusCode = HttpStatusCode.OK;
    private TimeSpan _delay = TimeSpan.Zero;

    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// Set a default response for all requests.
    /// </summary>
    public void SetResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _defaultResponse = content;
        _defaultStatusCode = statusCode;
    }

    /// <summary>
    /// Set a conditional response based on URL matching.
    /// </summary>
    public void SetResponse(Func<Uri, bool> match, Func<HttpResponseMessage> responseFactory) =>
        _responses.Add((match, responseFactory));

    /// <summary>
    /// Set a delay for all responses.
    /// </summary>
    public void SetDelay(TimeSpan delay) => _delay = delay;

    /// <summary>
    /// Clear all configured responses.
    /// </summary>
    public void Clear()
    {
        _responses.Clear();
        _defaultResponse = null;
        _defaultStatusCode = HttpStatusCode.OK;
        _delay = TimeSpan.Zero;
        Requests.Clear();
        LastRequest = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);

        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken);
        }

        // Check conditional responses first
        foreach (var (predicate, factory) in _responses)
        {
            if (predicate(request.RequestUri!))
            {
                return factory();
            }
        }

        // Fall back to default response
        if (_defaultResponse != null)
        {
            return new HttpResponseMessage(_defaultStatusCode)
            {
                Content = new StringContent(_defaultResponse)
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
