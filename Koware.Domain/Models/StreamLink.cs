// Author: Ilgaz Mehmetoğlu | Summary: Value object describing a stream link with quality, provider, and optional subtitles.
namespace Koware.Domain.Models;

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
