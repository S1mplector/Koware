// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation;

/// <summary>
/// Generates provider configurations from analyzed site data.
/// </summary>
public interface ISchemaGenerator
{
    /// <summary>
    /// Generate a provider configuration from the site profile and content schema.
    /// </summary>
    DynamicProviderConfig Generate(
        SiteProfile profile, 
        ContentSchema schema, 
        string? providerName = null);
}
