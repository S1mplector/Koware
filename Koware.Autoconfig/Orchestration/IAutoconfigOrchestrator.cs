// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Orchestration;

/// <summary>
/// Orchestrates the complete autoconfig process from URL to working provider.
/// </summary>
public interface IAutoconfigOrchestrator
{
    /// <summary>
    /// Analyze a website and generate a provider configuration.
    /// </summary>
    /// <param name="url">Website URL to analyze.</param>
    /// <param name="options">Configuration options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the generated configuration or error details.</returns>
    Task<AutoconfigResult> AnalyzeAndConfigureAsync(
        Uri url,
        AutoconfigOptions? options = null,
        IProgress<AutoconfigProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for the autoconfig process.
/// </summary>
public sealed record AutoconfigOptions
{
    /// <summary>Custom provider name (default: derived from domain).</summary>
    public string? ProviderName { get; init; }
    
    /// <summary>Force a specific content type.</summary>
    public ProviderType? ForceType { get; init; }
    
    /// <summary>Custom search query for validation.</summary>
    public string? TestQuery { get; init; }
    
    /// <summary>Skip validation step.</summary>
    public bool SkipValidation { get; init; }
    
    /// <summary>Don't save the configuration.</summary>
    public bool DryRun { get; init; }
    
    /// <summary>Analysis timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Progress update during autoconfig.
/// </summary>
public sealed record AutoconfigProgress
{
    /// <summary>Current phase name.</summary>
    public required string Phase { get; init; }
    
    /// <summary>Current step within the phase.</summary>
    public required string Step { get; init; }
    
    /// <summary>Progress percentage (0-100).</summary>
    public int Percentage { get; init; }
    
    /// <summary>Whether the current step succeeded.</summary>
    public bool? Succeeded { get; init; }
    
    /// <summary>Additional message.</summary>
    public string? Message { get; init; }
}
