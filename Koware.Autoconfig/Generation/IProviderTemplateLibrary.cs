// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Generation;

/// <summary>
/// Library of provider templates for matching and applying to sites.
/// </summary>
public interface IProviderTemplateLibrary
{
    /// <summary>
    /// Find the best matching template for a site.
    /// </summary>
    IProviderTemplate? FindBestMatch(SiteProfile profile, ContentSchema schema);
    
    /// <summary>
    /// Get all available templates.
    /// </summary>
    IReadOnlyList<IProviderTemplate> GetAll();
    
    /// <summary>
    /// Get a template by ID.
    /// </summary>
    IProviderTemplate? GetById(string id);
}
