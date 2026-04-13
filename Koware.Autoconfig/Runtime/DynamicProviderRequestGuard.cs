// Author: Ilgaz Mehmetoğlu
using System.Net;
using System.Text;
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Failure categories for dynamic provider runtime guardrails.
/// </summary>
public enum DynamicProviderFailureKind
{
    InvalidConfiguration,
    BlockedEndpoint,
    HttpFailure,
    ResponseTooLarge,
    InvalidResponse
}

/// <summary>
/// Exception thrown when a dynamic provider violates runtime guardrails.
/// </summary>
public sealed class DynamicProviderRuntimeException : Exception
{
    public DynamicProviderRuntimeException(
        DynamicProviderFailureKind kind,
        string providerSlug,
        string message,
        Uri? endpoint = null,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ProviderSlug = providerSlug;
        Endpoint = endpoint;
        StatusCode = statusCode;
    }

    public DynamicProviderFailureKind Kind { get; }
    public string ProviderSlug { get; }
    public Uri? Endpoint { get; }
    public HttpStatusCode? StatusCode { get; }
}

internal sealed class DynamicProviderRequestGuard
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> JsonLikeMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/graphql-response+json",
        "text/json",
        "text/plain",
        "application/javascript",
        "text/javascript"
    };

    private readonly DynamicProviderConfig _config;
    private readonly HashSet<string> _allowedHosts;

    public DynamicProviderRequestGuard(DynamicProviderConfig config)
    {
        _config = config;
        RequestBaseUri = BuildRequestBaseUri(config);
        _allowedHosts = BuildAllowedHosts(config);

        if (_allowedHosts.Count == 0)
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidConfiguration,
                "Provider host configuration is empty.");
        }
    }

    public Uri RequestBaseUri { get; }

    public Uri ResolveEndpointUri(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidConfiguration,
                "Provider endpoint is missing.");
        }

        var trimmedEndpoint = endpoint.Trim();
        Uri requestUri;
        if (IsNetworkPathReference(trimmedEndpoint))
        {
            throw CreateException(
                DynamicProviderFailureKind.BlockedEndpoint,
                $"Provider endpoint '{trimmedEndpoint}' cannot override the configured host.");
        }

        if (IsRootRelativeReference(trimmedEndpoint))
        {
            requestUri = new Uri(RequestBaseUri, trimmedEndpoint.TrimStart('/', '\\'));
        }
        else if (Uri.TryCreate(trimmedEndpoint, UriKind.Absolute, out var absolute))
        {
            requestUri = absolute;
        }
        else
        {
            requestUri = new Uri(RequestBaseUri, trimmedEndpoint.TrimStart('/', '\\'));
        }

        ValidateRequestUri(requestUri, "endpoint");
        return requestUri;
    }

    public Uri ResolveGraphQlUri(string endpoint, string query)
    {
        var endpointUri = ResolveEndpointUri(endpoint);
        var builder = new UriBuilder(endpointUri);
        var existingQuery = builder.Query.TrimStart('?');
        var encodedQuery = Uri.EscapeDataString(query);

        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? $"query={encodedQuery}"
            : $"{existingQuery}&query={encodedQuery}";

        ValidateRequestUri(builder.Uri, "GraphQL request");
        return builder.Uri;
    }

    public Uri ResolveRestRequestUri(string endpoint, string requestSuffix)
    {
        var endpointUri = ResolveEndpointUri(endpoint);
        if (string.IsNullOrWhiteSpace(requestSuffix))
        {
            return endpointUri;
        }

        var trimmedSuffix = requestSuffix.Trim();
        if (IsNetworkPathReference(trimmedSuffix))
        {
            throw CreateException(
                DynamicProviderFailureKind.BlockedEndpoint,
                $"REST request suffix '{trimmedSuffix}' cannot override the configured host.",
                endpointUri);
        }

        if (!IsRootRelativeReference(trimmedSuffix) &&
            Uri.TryCreate(trimmedSuffix, UriKind.Absolute, out var absolute))
        {
            throw CreateException(
                DynamicProviderFailureKind.BlockedEndpoint,
                $"Absolute REST request override '{absolute}' is not allowed.",
                absolute);
        }

        if (!Uri.TryCreate(endpointUri.AbsoluteUri + trimmedSuffix, UriKind.Absolute, out var requestUri))
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidConfiguration,
                $"REST request suffix '{trimmedSuffix}' could not be combined with endpoint '{endpointUri}'.",
                endpointUri);
        }

        ValidateRequestUri(requestUri, "REST request");
        return requestUri;
    }

    public async Task<string> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ValidateRequestUri(request.RequestUri, "request");

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(
                DynamicProviderFailureKind.HttpFailure,
                $"Provider returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                request.RequestUri,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > MaxResponseBytes)
        {
            throw CreateException(
                DynamicProviderFailureKind.ResponseTooLarge,
                $"Provider response exceeded the {MaxResponseBytes}-byte safety limit.",
                request.RequestUri);
        }

        var body = await ReadLimitedBodyAsync(response, request.RequestUri, cancellationToken);
        ValidateResponsePayload(body, response.Content.Headers.ContentType?.MediaType, request.RequestUri);
        return body;
    }

    public static Uri? TryCreateHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsHttpOrHttps(uri)
            ? uri
            : null;
    }

    private static Uri BuildRequestBaseUri(DynamicProviderConfig config)
    {
        if (TryCreateHttpUri(config.Hosts.ApiBase) is { } apiBase)
        {
            return EnsureTrailingSlash(apiBase);
        }

        var host = ExtractHost(config.Hosts.BaseHost);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new DynamicProviderRuntimeException(
                DynamicProviderFailureKind.InvalidConfiguration,
                config.Slug,
                "Provider base host is missing or invalid.");
        }

        return new Uri($"https://{host.TrimEnd('/')}/");
    }

    private static HashSet<string> BuildAllowedHosts(DynamicProviderConfig config)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddHost(hosts, config.Hosts.BaseHost);
        AddHost(hosts, config.Hosts.ApiBase);
        AddHost(hosts, config.Hosts.Referer);

        return hosts;
    }

    private static void AddHost(ISet<string> hosts, string? value)
    {
        var host = ExtractHost(value);
        if (!string.IsNullOrWhiteSpace(host))
        {
            hosts.Add(host);
        }
    }

    private static string? ExtractHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return absolute.Host;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        trimmed = trimmed.Trim('/');
        return Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var synthesized)
            ? synthesized.Host
            : null;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var absolute = uri.AbsoluteUri;
        return absolute.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri($"{absolute}/");
    }

    private static bool IsHttpOrHttps(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsRootRelativeReference(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("\\", StringComparison.Ordinal);

    private static bool IsNetworkPathReference(string value) =>
        value.StartsWith("//", StringComparison.Ordinal) ||
        value.StartsWith(@"\\", StringComparison.Ordinal);

    private void ValidateRequestUri(Uri? requestUri, string context)
    {
        if (requestUri is null)
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidConfiguration,
                $"Provider {context} URI is missing.");
        }

        if (!IsHttpOrHttps(requestUri))
        {
            throw CreateException(
                DynamicProviderFailureKind.BlockedEndpoint,
                $"Provider {context} uses unsupported scheme '{requestUri.Scheme}'.",
                requestUri);
        }

        if (!string.IsNullOrEmpty(requestUri.UserInfo))
        {
            throw CreateException(
                DynamicProviderFailureKind.BlockedEndpoint,
                $"Provider {context} cannot include URI user info.",
                requestUri);
        }

        if (!IsAllowedHost(requestUri.Host))
        {
            throw CreateException(
                DynamicProviderFailureKind.BlockedEndpoint,
                $"Provider {context} host '{requestUri.Host}' is outside the configured allowlist.",
                requestUri);
        }
    }

    private bool IsAllowedHost(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.');

        foreach (var allowedHost in _allowedHosts)
        {
            if (normalizedHost.Equals(allowedHost, StringComparison.OrdinalIgnoreCase) ||
                normalizedHost.EndsWith($".{allowedHost}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> ReadLimitedBodyAsync(
        HttpResponseMessage response,
        Uri? endpoint,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = response.Content.Headers.ContentLength is long contentLength &&
                           contentLength > 0 &&
                           contentLength <= MaxResponseBytes
            ? new MemoryStream((int)contentLength)
            : new MemoryStream();

        var chunk = new byte[16 * 1024];
        var totalBytes = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > MaxResponseBytes)
            {
                throw CreateException(
                    DynamicProviderFailureKind.ResponseTooLarge,
                    $"Provider response exceeded the {MaxResponseBytes}-byte safety limit.",
                    endpoint);
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void ValidateResponsePayload(string body, string? mediaType, Uri? endpoint)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidResponse,
                "Provider returned an empty response body.",
                endpoint);
        }

        var trimmed = body.TrimStart();
        if (LooksLikeHtml(trimmed) ||
            (!string.IsNullOrWhiteSpace(mediaType) &&
             mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)))
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidResponse,
                "Provider returned HTML instead of an API payload.",
                endpoint);
        }

        var looksLikeJson = trimmed.StartsWith("{", StringComparison.Ordinal) ||
                            trimmed.StartsWith("[", StringComparison.Ordinal);
        if (!looksLikeJson &&
            !string.IsNullOrWhiteSpace(mediaType) &&
            !JsonLikeMediaTypes.Contains(mediaType))
        {
            throw CreateException(
                DynamicProviderFailureKind.InvalidResponse,
                $"Provider returned unsupported content type '{mediaType}'.",
                endpoint);
        }
    }

    private static bool LooksLikeHtml(string value) =>
        value.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("<body", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("<", StringComparison.Ordinal);

    private DynamicProviderRuntimeException CreateException(
        DynamicProviderFailureKind kind,
        string message,
        Uri? endpoint = null,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null) =>
        new(kind, _config.Slug, message, endpoint, statusCode, innerException);
}
