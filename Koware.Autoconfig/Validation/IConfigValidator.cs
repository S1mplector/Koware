// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Validation;

/// <summary>
/// Validates provider configurations by testing them with live requests.
/// </summary>
public interface IConfigValidator
{
    /// <summary>
    /// Validate a provider configuration.
    /// </summary>
    /// <param name="config">The configuration to validate.</param>
    /// <param name="testQuery">Optional test search query (default: "One Piece").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with details about each check.</returns>
    Task<ValidationResult> ValidateAsync(
        DynamicProviderConfig config, 
        string? testQuery = null,
        CancellationToken cancellationToken = default);
}
