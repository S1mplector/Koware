// Author: Ilgaz Mehmetoğlu
using Microsoft.Extensions.Logging;

namespace Koware.Cli.Commands;

/// <summary>
/// Starts another Koware CLI process when a workflow needs process isolation or relaunch.
/// </summary>
public interface IKowareSubprocessLauncher
{
    /// <summary>
    /// Try to launch Koware with the provided command-line arguments.
    /// </summary>
    Task<int?> TryRunAsync(
        IReadOnlyList<string> commandArgs,
        ILogger logger,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null);
}
