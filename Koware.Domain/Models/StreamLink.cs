// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Value object describing a playable stream with quality, provider, and optional subtitles.
/// </summary>
/// <param name="Url">Direct URL to the stream (HLS, DASH, or direct file).</param>
/// <param name="Quality">Quality label (e.g., "1080p", "720p", "auto").</param>
/// <param name="Provider">Name of the provider/source (e.g., "allanime", "gogoanime").</param>
/// <param name="Referrer">Optional HTTP Referer header required for playback.</param>
/// <param name="Subtitles">Optional list of external subtitle tracks.</param>
/// <param name="RequiresSoftSubSupport">True if player must support external subtitles.</param>
/// <param name="HostPriority">Provider preference score (higher = preferred).</param>
/// <param name="SourceTag">Friendly source identifier for UI/logging.</param>
public sealed record StreamLink(
    Uri Url,
    string Quality,
    string Provider,
    string? Referrer,
    IReadOnlyList<SubtitleTrack>? Subtitles = null,
    bool RequiresSoftSubSupport = false,
    int HostPriority = 0,
    string? SourceTag = null)
{
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = Subtitles ?? Array.Empty<SubtitleTrack>();

    /// <summary>
    /// True if the stream needs a player that can handle external/soft subtitle tracks.
    /// </summary>
    public bool RequiresSoftSubSupport { get; init; } = RequiresSoftSubSupport;

    /// <summary>
    /// Provider/host preference hint (higher is better) used when multiple streams are available.
    /// </summary>
    public int HostPriority { get; init; } = HostPriority;

    /// <summary>
    /// Friendly source identifier (e.g., hianime, gogoanime, wixmp) for UI/logging.
    /// Defaults to Provider when not specified.
    /// </summary>
    public string? SourceTag { get; init; } = SourceTag ?? Provider;

    public override string ToString() => $"{Quality} - {Provider}";
}
