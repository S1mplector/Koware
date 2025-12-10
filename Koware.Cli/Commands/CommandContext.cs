// Author: Ilgaz Mehmetoğlu
using Koware.Cli.Configuration;
using Microsoft.Extensions.Logging;

namespace Koware.Cli.Commands;

/// <summary>
/// Execution context passed to CLI commands.
/// Provides access to services, logging, and cancellation.
/// </summary>
public sealed class CommandContext
{
    public CommandContext(
        IServiceProvider services,
        ILogger logger,
        DefaultCliOptions defaults,
        CancellationToken cancellationToken)
    {
        Services = services;
        Logger = logger;
        Defaults = defaults;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Service provider for resolving dependencies.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Logger for command output.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Default CLI options (mode, quality, etc.).
    /// </summary>
    public DefaultCliOptions Defaults { get; }

    /// <summary>
    /// Cancellation token for async operations.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Resolve a required service from the container.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull
        => (T)Services.GetService(typeof(T))!;

    /// <summary>
    /// Try to resolve an optional service from the container.
    /// </summary>
    public T? GetService<T>() where T : class
        => Services.GetService(typeof(T)) as T;
}
