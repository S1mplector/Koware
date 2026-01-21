// Author: Ilgaz Mehmetoğlu
namespace Koware.Cli.Commands;

/// <summary>
/// Registry of all available CLI commands.
/// Provides command lookup by name or alias.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICliCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a command. Registers both the name and any aliases.
    /// </summary>
    public void Register(ICliCommand command)
    {
        _commands[command.Name] = command;
        foreach (var alias in command.Aliases)
        {
            _commands[alias] = command;
        }
    }

    /// <summary>
    /// Try to find a command by name or alias.
    /// </summary>
    public ICliCommand? Find(string name)
    {
        _commands.TryGetValue(name, out var command);
        return command;
    }

    /// <summary>
    /// Get all unique commands (excludes aliases pointing to same command).
    /// </summary>
    public IEnumerable<ICliCommand> GetAll()
        => _commands.Values.Distinct();

    /// <summary>
    /// Check if a command name requires a configured provider.
    /// </summary>
    public bool RequiresProvider(string name)
        => Find(name)?.RequiresProvider ?? false;

    /// <summary>
    /// Create a registry with all built-in commands.
    /// </summary>
    public static CommandRegistry CreateDefault()
    {
        var registry = new CommandRegistry();
        
        // Register all commands
        registry.Register(new LastCommand());
        registry.Register(new VersionCommand());
        registry.Register(new SyncCommand());
        // More commands will be added as they are extracted from Program.cs
        
        return registry;
    }
}
