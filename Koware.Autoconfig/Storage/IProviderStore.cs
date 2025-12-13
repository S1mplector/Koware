// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Storage;

/// <summary>
/// Storage and registry for provider configurations.
/// </summary>
public interface IProviderStore
{
    /// <summary>
    /// List all available providers.
    /// </summary>
    Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Get a provider configuration by slug.
    /// </summary>
    Task<DynamicProviderConfig?> GetAsync(string slug, CancellationToken ct = default);
    
    /// <summary>
    /// Save a provider configuration.
    /// </summary>
    Task SaveAsync(DynamicProviderConfig config, CancellationToken ct = default);
    
    /// <summary>
    /// Delete a provider configuration.
    /// </summary>
    Task<bool> DeleteAsync(string slug, CancellationToken ct = default);
    
    /// <summary>
    /// Set a provider as the active/default for its type.
    /// </summary>
    Task SetActiveAsync(string slug, ProviderType type, CancellationToken ct = default);
    
    /// <summary>
    /// Get the active provider for a type.
    /// </summary>
    Task<DynamicProviderConfig?> GetActiveAsync(ProviderType type, CancellationToken ct = default);
    
    /// <summary>
    /// Check if a provider with the given slug exists.
    /// </summary>
    Task<bool> ExistsAsync(string slug, CancellationToken ct = default);
    
    /// <summary>
    /// Export a provider configuration to JSON.
    /// </summary>
    Task<string> ExportAsync(string slug, CancellationToken ct = default);
    
    /// <summary>
    /// Import a provider configuration from JSON.
    /// </summary>
    Task<DynamicProviderConfig> ImportAsync(string json, CancellationToken ct = default);
}
