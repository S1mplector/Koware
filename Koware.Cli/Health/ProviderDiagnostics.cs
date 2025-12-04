// Author: Ilgaz Mehmetoğlu
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Koware.Infrastructure.Configuration;

namespace Koware.Cli.Health;

/// <summary>
/// Connectivity diagnostics for anime provider endpoints (DNS + HTTP).
/// Used by the "koware doctor" command.
/// </summary>
internal sealed class ProviderDiagnostics
{
    private readonly HttpClient _httpClient;

    /// <summary>Create a new diagnostics instance with the given HTTP client.</summary>
    public ProviderDiagnostics(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(6);
    }

    /// <summary>
    /// Check DNS resolution and HTTP connectivity for a provider.
    /// </summary>
    /// <param name="options">Provider configuration with API base URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with DNS and HTTP status.</returns>
    public async Task<ProviderCheckResult> CheckAsync(AllAnimeOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiBase))
        {
            return new ProviderCheckResult
            {
                Target = "(not configured)",
                DnsResolved = false,
                DnsError = "ApiBase not configured",
                HttpError = "ApiBase not configured",
                Success = false
            };
        }

        var baseUri = new Uri(options.ApiBase.EndsWith("/") ? options.ApiBase : options.ApiBase + "/");
        var result = new ProviderCheckResult { Target = baseUri.Host };

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(baseUri.Host);
            result.DnsResolved = addresses.Length > 0;
        }
        catch (Exception ex)
        {
            result.DnsError = ex.Message;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/api"));
            if (!string.IsNullOrWhiteSpace(options.UserAgent))
            {
                request.Headers.UserAgent.ParseAdd(options.UserAgent);
            }
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            result.HttpStatus = (int)response.StatusCode;
            result.HttpSuccess = response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            result.HttpError = ex.Message;
        }

        result.Success = result.DnsResolved && (result.HttpSuccess || result.HttpStatus.HasValue);
        return result;
    }
}

/// <summary>
/// Result of a provider connectivity check.
/// </summary>
internal sealed record ProviderCheckResult
{
    /// <summary>Target hostname checked.</summary>
    public string Target { get; init; } = string.Empty;
    /// <summary>True if DNS resolution succeeded.</summary>
    public bool DnsResolved { get; set; }
    /// <summary>DNS error message if resolution failed.</summary>
    public string? DnsError { get; set; }
    /// <summary>True if HTTP request returned a success status.</summary>
    public bool HttpSuccess { get; set; }
    /// <summary>HTTP status code if request completed.</summary>
    public int? HttpStatus { get; set; }
    /// <summary>HTTP error message if request failed.</summary>
    public string? HttpError { get; set; }
    /// <summary>Overall success (DNS resolved and HTTP reachable).</summary>
    public bool Success { get; set; }
}
