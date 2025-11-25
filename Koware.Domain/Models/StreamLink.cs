// Author: Ilgaz Mehmetoğlu | Summary: Value object describing a stream link with quality, provider, and optional subtitles.
namespace Koware.Domain.Models;

public sealed record StreamLink(
    Uri Url,
    string Quality,
    string Provider,
    string? Referrer,
    IReadOnlyList<SubtitleTrack>? Subtitles = null)
{
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = Subtitles ?? Array.Empty<SubtitleTrack>();

    public override string ToString() => $"{Quality} - {Provider}";
}
