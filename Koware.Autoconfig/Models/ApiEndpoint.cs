// Author: Ilgaz Mehmetoğlu
namespace Koware.Autoconfig.Models;

/// <summary>
/// Represents a discovered API endpoint on a website.
/// </summary>
public sealed record ApiEndpoint
{
    /// <summary>Full URL of the endpoint.</summary>
    public required Uri Url { get; init; }
    
    /// <summary>Type of API (GraphQL, REST, etc.).</summary>
    public ApiType Type { get; init; } = ApiType.Custom;
    
    /// <summary>HTTP method used.</summary>
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    
    /// <summary>Detected purpose of this endpoint.</summary>
    public EndpointPurpose Purpose { get; init; } = EndpointPurpose.Unknown;
    
    /// <summary>Sample query or request body if discovered.</summary>
    public string? SampleQuery { get; init; }
    
    /// <summary>Sample response snippet for analysis.</summary>
    public string? SampleResponse { get; init; }
    
    /// <summary>Confidence score (0-100) in the endpoint detection.</summary>
    public int Confidence { get; init; }
    
    /// <summary>Additional notes about the endpoint.</summary>
    public string? Notes { get; init; }
}
