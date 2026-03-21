// Author: Ilgaz Mehmetoğlu
using System.IO;

namespace Koware.Application.Environment;

/// <summary>
/// Resolves user-writable Koware filesystem paths in a cross-platform way.
/// </summary>
public static class KowarePaths
{
    /// <summary>
    /// Get the user configuration directory for Koware.
    /// </summary>
    public static string GetUserConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "koware");
        }

        var configHome = System.Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configHome, "koware");
    }

    /// <summary>
    /// Ensure the user configuration directory exists and return it.
    /// </summary>
    public static string EnsureUserConfigDirectory()
    {
        var directory = GetUserConfigDirectory();
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Get the user configuration file path, creating the directory if needed.
    /// </summary>
    public static string GetUserConfigFilePath()
    {
        return Path.Combine(EnsureUserConfigDirectory(), "appsettings.user.json");
    }
}
