// Author: Ilgaz Mehmetoglu
using Con = System.Console;

namespace Koware.Cli.Console;

public static class ErrorDisplay
{
    public static void UnknownCommand(string command)
    {
        Con.WriteLine();
        WriteError($"Unknown command: '{command}'");
        Con.WriteLine();
        var suggestions = GetCommandSuggestions(command);
        if (suggestions.Length > 0)
        {
            Con.ForegroundColor = ConsoleColor.Cyan;
            Con.WriteLine("Did you mean:");
            Con.ResetColor();
            foreach (var s in suggestions)
                Con.WriteLine($"  koware {s}");
            Con.WriteLine();
        }
        Con.WriteLine("Run 'koware help' to see available commands.");
    }

    public static void NetworkError(string? message = null, string? provider = null)
    {
        Con.WriteLine();
        WriteError("Network error");
        Con.WriteLine();
        if (!string.IsNullOrWhiteSpace(message))
        {
            Con.ForegroundColor = ConsoleColor.DarkGray;
            Con.WriteLine($"  {message}");
            Con.ResetColor();
            Con.WriteLine();
        }
        Con.WriteLine("Possible causes:");
        WriteBullet("No internet connection");
        WriteBullet("The provider may be temporarily down");
        WriteBullet("Your network may be blocking the connection");
        if (!string.IsNullOrWhiteSpace(provider))
            WriteBullet($"The {provider} API endpoint may have changed");
        Con.WriteLine();
        Con.WriteLine("Try:");
        WriteHint("koware doctor", "Check provider connectivity");
        WriteHint("koware provider test", "Test provider endpoints");
    }

    public static void ProviderNotConfigured(string mode)
    {
        Con.WriteLine();
        WriteError($"No {mode} provider configured");
        Con.WriteLine();
        Con.WriteLine("Quick setup:");
        WriteHint("koware provider autoconfig", "Auto-configure from remote repository");
        Con.WriteLine();
        Con.WriteLine("Manual setup:");
        WriteHint("koware provider add", "Configure a provider interactively");
        WriteHint("koware provider init", "Create a template config file");
    }

    public static void NoResults(string query, string contentType = "content")
    {
        Con.WriteLine();
        WriteWarning($"No {contentType} found for: '{query}'");
        Con.WriteLine();
        Con.WriteLine("Suggestions:");
        WriteBullet("Check your spelling");
        WriteBullet("Try a shorter or more general search term");
        WriteBullet("Use the original Japanese/English title");
        Con.WriteLine();
        WriteHint("koware search --help", "See search options and filters");
    }

    public static void Generic(string message, string? details = null, string? hint = null)
    {
        Con.WriteLine();
        WriteError(message);
        if (!string.IsNullOrWhiteSpace(details))
        {
            Con.WriteLine();
            Con.ForegroundColor = ConsoleColor.DarkGray;
            Con.WriteLine($"  {details}");
            Con.ResetColor();
        }
        if (!string.IsNullOrWhiteSpace(hint))
        {
            Con.WriteLine();
            Con.WriteLine(hint);
        }
    }

    public static void Timeout(string operation = "operation")
    {
        Con.WriteLine();
        WriteError($"The {operation} timed out");
        Con.WriteLine();
        Con.WriteLine("Suggestions:");
        WriteBullet("Check your internet connection");
        WriteBullet("The server may be slow - try again later");
        WriteBullet("Try a different provider if available");
    }

    public static void Cancelled()
    {
        Con.WriteLine();
        Con.ForegroundColor = ConsoleColor.DarkGray;
        Con.WriteLine("Operation cancelled.");
        Con.ResetColor();
    }

    private static void WriteError(string message)
    {
        Con.ForegroundColor = ConsoleColor.Red;
        Con.Write($"{Icons.Error} ");
        Con.ResetColor();
        Con.WriteLine(message);
    }

    private static void WriteWarning(string message)
    {
        Con.ForegroundColor = ConsoleColor.Yellow;
        Con.Write($"{Icons.Warning} ");
        Con.ResetColor();
        Con.WriteLine(message);
    }

    private static void WriteBullet(string text)
    {
        Con.ForegroundColor = ConsoleColor.DarkGray;
        Con.Write("  * ");
        Con.ResetColor();
        Con.WriteLine(text);
    }

    private static void WriteHint(string command, string description)
    {
        Con.ForegroundColor = ConsoleColor.Cyan;
        Con.Write($"  {command,-35}");
        Con.ForegroundColor = ConsoleColor.DarkGray;
        Con.WriteLine(description);
        Con.ResetColor();
    }

    private static string[] GetCommandSuggestions(string input)
    {
        var commands = new[] { "search", "watch", "play", "stream", "download", "read", "last", "continue", "history", "list", "offline", "config", "mode", "provider", "doctor", "update", "recommend", "help", "version" };
        return commands.Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase) || Levenshtein(input.ToLower(), c) <= 2).Take(3).ToArray();
    }

    private static int Levenshtein(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
        if (string.IsNullOrEmpty(s2)) return s1.Length;
        var d = new int[s1.Length + 1, s2.Length + 1];
        for (var i = 0; i <= s1.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= s2.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s1.Length; i++)
            for (var j = 1; j <= s2.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s1[i - 1] == s2[j - 1] ? 0 : 1));
        return d[s1.Length, s2.Length];
    }
}
