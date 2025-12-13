// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Discovers and analyzes API endpoints on a website.
/// </summary>
public interface IApiDiscoveryEngine
{
    /// <summary>
    /// Discover API endpoints from a site profile.
    /// </summary>
    /// <param name="profile">Site profile from initial probing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered API endpoints.</returns>
    Task<IReadOnlyList<ApiEndpoint>> DiscoverAsync(SiteProfile profile, CancellationToken cancellationToken = default);
}
