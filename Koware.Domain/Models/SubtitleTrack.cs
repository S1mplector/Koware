// Author: Ilgaz Mehmetoğlu
// Summary: Subtitle track metadata including label, URL, and language.
namespace Koware.Domain.Models;

public sealed record SubtitleTrack(string Label, Uri Url, string? Language = null);
