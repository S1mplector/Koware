// Author: Ilgaz Mehmetoğlu
using System.Reflection;

namespace Koware.Cli.Commands;

/// <summary>
/// Implements the 'koware version' command: displays the CLI version.
/// </summary>
public sealed class VersionCommand : ICliCommand
{
    public string Name => "version";
    public IReadOnlyList<string> Aliases => new[] { "--version", "-v" };
    public string Description => "Display the Koware CLI version";

    public Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        var version = GetVersionLabel();
        System.Console.WriteLine(string.IsNullOrWhiteSpace(version) 
            ? "Koware CLI (unknown version)" 
            : $"Koware CLI {version}");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Read the entry assembly version and return a short label like "v0.4.0".
    /// </summary>
    public static string GetVersionLabel()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null)
        {
            return string.Empty;
        }

        var parts = version.ToString().Split('.');
        var trimmed = parts.Length >= 3 ? string.Join('.', parts.Take(3)) : version.ToString();
        return $"v{trimmed}";
    }

    /// <summary>
    /// Parse a version label like "v0.4.0" or "v0.4.0-beta" into a Version object.
    /// </summary>
    public static Version? TryParseVersionCore(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = label.Trim();

        if (text.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(2);
        }
        else if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(1);
        }

        var separatorIndex = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (separatorIndex >= 0)
        {
            text = text.Substring(0, separatorIndex);
        }

        return Version.TryParse(text, out var parsed) ? parsed : null;
    }
}
