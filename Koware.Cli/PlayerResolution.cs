// Author: Ilgaz Mehmetoglu

/// <summary>
/// Represents a resolved player executable and candidate names for logging/fallback.
/// </summary>
/// <param name="Path">Absolute path to the resolved player, or null if not found.</param>
/// <param name="Name">Player name (e.g., "vlc", "mpv", "Koware.Player.Win").</param>
/// <param name="Candidates">List of candidates that were searched.</param>
internal sealed record PlayerResolution(string? Path, string Name, IReadOnlyList<string> Candidates);
