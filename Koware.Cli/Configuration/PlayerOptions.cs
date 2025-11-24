namespace Koware.Cli.Configuration;

public sealed class PlayerOptions
{
    /// <summary>
    /// Player executable to launch (mpv, vlc, etc.).
    /// </summary>
    public string Command { get; set; } = "mpv";

    /// <summary>
    /// Additional arguments passed before the URL (e.g., --no-terminal).
    /// </summary>
    public string? Args { get; set; } = "--force-window=yes";
}
