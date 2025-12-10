// Author: Ilgaz Mehmetoğlu
namespace Koware.Cli.Commands;

/// <summary>
/// Interface for CLI command handlers.
/// Each command is responsible for parsing its own arguments and executing the operation.
/// </summary>
public interface ICliCommand
{
    /// <summary>
    /// The primary command name (e.g., "search", "play", "history").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Optional aliases for the command (e.g., "watch" for "play").
    /// </summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>
    /// Short description for help text.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Whether this command requires a configured provider to run.
    /// </summary>
    bool RequiresProvider => false;

    /// <summary>
    /// Execute the command with the given arguments.
    /// </summary>
    /// <param name="args">Full command-line arguments (including the command name at index 0).</param>
    /// <param name="context">Execution context with services, logger, and cancellation.</param>
    /// <returns>Exit code: 0 for success, non-zero for errors.</returns>
    Task<int> ExecuteAsync(string[] args, CommandContext context);
}
