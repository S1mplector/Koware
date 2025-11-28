// Author: Ilgaz Mehmetoğlu
namespace Koware.Domain.Models;

/// <summary>
/// Subtitle track metadata including label, URL, and language.
/// </summary>
/// <param name="Label">Display label (e.g., "English", "Japanese").</param>
/// <param name="Url">URI to the subtitle file (VTT, ASS, SRT, etc.).</param>
/// <param name="Language">Optional ISO language code.</param>
public sealed record SubtitleTrack(string Label, Uri Url, string? Language = null);
