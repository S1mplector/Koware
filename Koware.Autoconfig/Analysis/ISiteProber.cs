// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Analysis;

/// <summary>
/// Probes a website to gather initial intelligence about its structure.
/// </summary>
public interface ISiteProber
{
    /// <summary>
    /// Probe a website and gather a profile of its characteristics.
    /// </summary>
    /// <param name="url">The website URL to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Site profile with detected characteristics.</returns>
    Task<SiteProfile> ProbeAsync(Uri url, CancellationToken cancellationToken = default);
}
