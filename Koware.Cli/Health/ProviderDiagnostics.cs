// Author: Ilgaz Mehmetoğlu
// Connectivity checks for anime provider endpoints (DNS + HTTP).
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Koware.Infrastructure.Configuration;

namespace Koware.Cli.Health;

internal sealed class ProviderDiagnostics
{
    private readonly HttpClient _httpClient;

    public ProviderDiagnostics(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(6);
    }

    public async Task<ProviderCheckResult> CheckAsync(AllAnimeOptions options, CancellationToken cancellationToken)
    {
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

internal sealed record ProviderCheckResult
{
    public string Target { get; init; } = string.Empty;
    public bool DnsResolved { get; set; }
    public string? DnsError { get; set; }
    public bool HttpSuccess { get; set; }
    public int? HttpStatus { get; set; }
    public string? HttpError { get; set; }
    public bool Success { get; set; }
}
