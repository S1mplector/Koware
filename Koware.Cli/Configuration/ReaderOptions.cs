// Author: Ilgaz Mehmetoğlu
namespace Koware.Cli.Configuration;

/// <summary>
/// Options for selecting the external manga reader command and arguments used by the CLI.
/// Bound from the "Reader" section in appsettings.json or appsettings.user.json.
/// </summary>
public sealed class ReaderOptions
{
    /// <summary>
    /// Reader executable to launch (Koware.Reader, etc.).
    /// </summary>
    public string Command { get; set; } = "Koware.Reader.exe";

    /// <summary>
    /// Additional arguments passed before the pages JSON.
    /// </summary>
    public string? Args { get; set; }
}
