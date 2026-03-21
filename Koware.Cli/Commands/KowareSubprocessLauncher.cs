// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Koware.Cli.Commands;

/// <summary>
/// Best-effort launcher for invoking the current Koware CLI as a child process.
/// </summary>
public sealed class KowareSubprocessLauncher : IKowareSubprocessLauncher
{
    public async Task<int?> TryRunAsync(
        IReadOnlyList<string> commandArgs,
        ILogger logger,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        var startInfos = BuildStartInfos(commandArgs, environmentOverrides);

        foreach (var startInfo in startInfos)
        {
            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to start Koware subprocess via {Command}", startInfo.FileName);
            }
        }

        return null;
    }

    private static IReadOnlyList<ProcessStartInfo> BuildStartInfos(
        IReadOnlyList<string> commandArgs,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var startInfos = new List<ProcessStartInfo>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddStartInfo(string fileName, IEnumerable<string>? prefixArgs = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            var keyParts = new List<string> { fileName };
            if (prefixArgs is not null)
            {
                keyParts.AddRange(prefixArgs);
            }

            keyParts.AddRange(commandArgs);
            var key = string.Join('\u001f', keyParts);
            if (!dedupe.Add(key))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false
            };

            if (prefixArgs is not null)
            {
                foreach (var arg in prefixArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }

            foreach (var arg in commandArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            if (environmentOverrides is not null)
            {
                foreach (var pair in environmentOverrides)
                {
                    if (pair.Value is null)
                    {
                        startInfo.Environment.Remove(pair.Key);
                    }
                    else
                    {
                        startInfo.Environment[pair.Key] = pair.Value;
                    }
                }
            }

            startInfos.Add(startInfo);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var processName = Path.GetFileNameWithoutExtension(processPath);
            if (processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrWhiteSpace(entryAssemblyPath) && File.Exists(entryAssemblyPath))
                {
                    AddStartInfo(processPath, new[] { entryAssemblyPath });
                }
            }
            else
            {
                AddStartInfo(processPath);
            }
        }

        var onPath = ResolveOnPath("koware");
        if (!string.IsNullOrWhiteSpace(onPath))
        {
            AddStartInfo(onPath);
        }

        return startInfos;
    }

    private static string? ResolveOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidates = Path.HasExtension(command)
            ? new[] { command }
            : OperatingSystem.IsWindows()
                ? new[] { command, $"{command}.exe", $"{command}.cmd", $"{command}.bat" }
                : new[] { command };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }
}
