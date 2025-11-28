// Author: Ilgaz Mehmetoğlu
namespace Koware.Cli.Configuration;

/// <summary>
/// Configuration model for CLI defaults such as quality and preferred match index.
/// Bound from the "Defaults" section in appsettings.json or appsettings.user.json.
/// </summary>
public sealed class DefaultCliOptions
{
    /// <summary>Default quality label (e.g., "1080p", "720p").</summary>
    public string? Quality { get; set; }

    /// <summary>Default match index (1-based) to use when multiple results found.</summary>
    public int? PreferredMatchIndex { get; set; }
}
