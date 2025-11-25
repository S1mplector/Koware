// Author: Ilgaz Mehmetoğlu | Summary: Options for selecting the external player command and arguments used by the CLI.
namespace Koware.Cli.Configuration;

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
