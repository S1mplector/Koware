// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Analyzes content structure and patterns on a website.
/// </summary>
public interface IContentPatternMatcher
{
    /// <summary>
    /// Analyze the content structure based on site profile and discovered endpoints.
    /// </summary>
    Task<ContentSchema> AnalyzeAsync(
        SiteProfile profile, 
        IReadOnlyList<ApiEndpoint> endpoints, 
        CancellationToken cancellationToken = default);
}
