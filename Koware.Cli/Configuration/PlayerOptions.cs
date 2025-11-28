// Author: Ilgaz Mehmetoğlu
namespace Koware.Cli.Configuration;

/// <summary>
/// Options for selecting the external player command and arguments used by the CLI.
/// Bound from the "Player" section in appsettings.json or appsettings.user.json.
/// </summary>
public sealed class PlayerOptions
{
    /// <summary>
    /// Player executable to launch (Koware.Player.Win, mpv, vlc, etc.).
    /// </summary>
    public string Command { get; set; } = "Koware.Player.Win.exe";

    /// <summary>
    /// Additional arguments passed before the URL (e.g., --no-terminal).
    /// </summary>
    public string? Args { get; set; } = "--force-window=yes";
}
