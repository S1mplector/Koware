// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation;

/// <summary>
/// A template for generating provider configurations.
/// </summary>
public interface IProviderTemplate
{
    /// <summary>Template identifier.</summary>
    string Id { get; }
    
    /// <summary>Human-readable name.</summary>
    string Name { get; }
    
    /// <summary>Description of what sites this template matches.</summary>
    string Description { get; }
    
    /// <summary>Content types this template supports.</summary>
    ProviderType SupportedTypes { get; }
    
    /// <summary>
    /// Calculate how well this template matches the given site profile and schema.
    /// </summary>
    /// <returns>Score from 0-100, higher is better match.</returns>
    int CalculateMatchScore(SiteProfile profile, ContentSchema schema);
    
    /// <summary>
    /// Apply this template to generate a provider configuration.
    /// </summary>
    DynamicProviderConfig Apply(SiteProfile profile, ContentSchema schema, string providerName);
}
