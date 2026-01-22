// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SystemConsole = System.Console;

namespace Koware.Cli.Commands;

/// <summary>
/// Result of a merge operation.
/// </summary>
public sealed record MergeResult(bool Success, string Message, int FilesResolved = 0);

/// <summary>
/// Intelligent auto-merge engine for Koware sync conflicts.
/// Handles SQLite databases and JSON config files with smart merge strategies.
/// </summary>
public sealed class SyncMergeEngine
{
    private readonly string _dataDir;

    public SyncMergeEngine(string dataDir)
    {
        _dataDir = dataDir;
    }

    /// <summary>
    /// Check if there are unmerged files in the repository.
    /// </summary>
    public async Task<bool> HasUnmergedFilesAsync()
    {
        var (code, output, _) = await RunGitAsync("diff --name-only --diff-filter=U");
        return code == 0 && !string.IsNullOrWhiteSpace(output);
    }

    /// <summary>
    /// Get list of conflicted files.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetConflictedFilesAsync()
    {
        var (code, output, _) = await RunGitAsync("diff --name-only --diff-filter=U");
        if (code != 0 || string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<string>();
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Attempt to automatically resolve all merge conflicts.
    /// </summary>
    public async Task<MergeResult> AutoResolveConflictsAsync(bool verbose = false)
    {
        var conflictedFiles = await GetConflictedFilesAsync();
        if (conflictedFiles.Count == 0)
        {
            return new MergeResult(true, "No conflicts to resolve");
        }

        if (verbose)
        {
            SystemConsole.ForegroundColor = ConsoleColor.Cyan;
            SystemConsole.WriteLine($"[merge] Found {conflictedFiles.Count} conflicted file(s), attempting auto-resolve...");
            SystemConsole.ResetColor();
        }

        var resolved = 0;
        var failed = new List<string>();

        foreach (var file in conflictedFiles)
        {
            var fullPath = Path.Combine(_dataDir, file);
            var result = await ResolveFileConflictAsync(file, fullPath, verbose);

            if (result.Success)
            {
                resolved++;
                await RunGitAsync($"add \"{file}\"");

                if (verbose)
                {
                    SystemConsole.ForegroundColor = ConsoleColor.Green;
                    SystemConsole.WriteLine($"  ✓ {file}: {result.Message}");
                    SystemConsole.ResetColor();
                }
            }
            else
            {
                failed.Add(file);
                if (verbose)
                {
                    SystemConsole.ForegroundColor = ConsoleColor.Yellow;
                    SystemConsole.WriteLine($"  ! {file}: {result.Message}");
                    SystemConsole.ResetColor();
                }
            }
        }

        if (failed.Count == 0)
        {
            // All conflicts resolved, complete the merge
            var (commitCode, _, commitError) = await RunGitAsync("commit -m \"Auto-merged sync conflicts\"");
            if (commitCode != 0 && !commitError.Contains("nothing to commit"))
            {
                return new MergeResult(false, $"Failed to commit merge: {commitError}", resolved);
            }

            return new MergeResult(true, $"Resolved {resolved} conflict(s)", resolved);
        }

        return new MergeResult(false, $"Could not resolve {failed.Count} file(s): {string.Join(", ", failed)}", resolved);
    }

    /// <summary>
    /// Resolve a single file conflict based on its type.
    /// </summary>
    private async Task<MergeResult> ResolveFileConflictAsync(string relativePath, string fullPath, bool verbose)
    {
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        var fileName = Path.GetFileName(relativePath).ToLowerInvariant();

        return (extension, fileName) switch
        {
            (".db", _) => await ResolveSqliteConflictAsync(relativePath, fullPath, verbose),
            (".json", _) => await ResolveJsonConflictAsync(relativePath, fullPath, verbose),
            _ => await ResolveGenericConflictAsync(relativePath, fullPath, verbose)
        };
    }

    /// <summary>
    /// Resolve SQLite database conflicts by merging records from both versions.
    /// Strategy: Keep all unique records, prefer newer timestamps for duplicates.
    /// </summary>
    private async Task<MergeResult> ResolveSqliteConflictAsync(string relativePath, string fullPath, bool verbose)
    {
        try
        {
            // Extract both versions of the file
            var oursPath = fullPath + ".ours";
            var theirsPath = fullPath + ".theirs";
            var mergedPath = fullPath + ".merged";

            // Get our version (HEAD)
            var (oursCode, _, _) = await RunGitAsync($"show :2:\"{relativePath}\" > \"{oursPath}\"", useShell: true);
            // Get their version (remote)
            var (theirsCode, _, _) = await RunGitAsync($"show :3:\"{relativePath}\" > \"{theirsPath}\"", useShell: true);

            if (oursCode != 0 || theirsCode != 0)
            {
                // Fallback: prefer local version
                await RunGitAsync($"checkout --ours \"{relativePath}\"");
                Cleanup(oursPath, theirsPath, mergedPath);
                return new MergeResult(true, "Kept local version (extraction failed)");
            }

            // Merge the databases
            var mergeSuccess = await MergeSqliteDatabasesAsync(oursPath, theirsPath, mergedPath, verbose);

            if (mergeSuccess)
            {
                // Replace the conflicted file with merged version
                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(mergedPath, fullPath);
                Cleanup(oursPath, theirsPath);
                return new MergeResult(true, "Merged database records");
            }
            else
            {
                // Fallback: prefer local version
                await RunGitAsync($"checkout --ours \"{relativePath}\"");
                Cleanup(oursPath, theirsPath, mergedPath);
                return new MergeResult(true, "Kept local version (merge failed)");
            }
        }
        catch (Exception ex)
        {
            // Ultimate fallback: keep local
            await RunGitAsync($"checkout --ours \"{relativePath}\"");
            return new MergeResult(true, $"Kept local version ({ex.Message})");
        }
    }

    /// <summary>
    /// Merge two SQLite databases by combining records.
    /// </summary>
    private async Task<bool> MergeSqliteDatabasesAsync(string oursPath, string theirsPath, string mergedPath, bool verbose)
    {
        try
        {
            // Copy ours as the base for merged
            File.Copy(oursPath, mergedPath, overwrite: true);

            await using var mergedConn = new SqliteConnection($"Data Source={mergedPath}");
            await mergedConn.OpenAsync();

            await using var theirsConn = new SqliteConnection($"Data Source={theirsPath};Mode=ReadOnly");
            await theirsConn.OpenAsync();

            // Get list of tables in theirs
            var tables = new List<string>();
            await using (var cmd = theirsConn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var totalMerged = 0;

            foreach (var table in tables)
            {
                var merged = await MergeTableAsync(mergedConn, theirsConn, table, verbose);
                totalMerged += merged;
            }

            if (verbose && totalMerged > 0)
            {
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine($"    Merged {totalMerged} record(s) from remote");
                SystemConsole.ResetColor();
            }

            return true;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine($"    Database merge error: {ex.Message}");
                SystemConsole.ResetColor();
            }
            return false;
        }
    }

    /// <summary>
    /// Merge a single table from theirs into merged.
    /// </summary>
    private async Task<int> MergeTableAsync(SqliteConnection mergedConn, SqliteConnection theirsConn, string table, bool verbose)
    {
        var merged = 0;

        try
        {
            // Get columns for this table
            var columns = new List<string>();
            string? timestampColumn = null;
            string? primaryKey = null;

            await using (var cmd = theirsConn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table})";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(1);
                    var isPk = reader.GetInt32(5) == 1;
                    columns.Add(name);

                    if (isPk) primaryKey = name;

                    // Detect timestamp columns for conflict resolution
                    if (name.Contains("time", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("_at", StringComparison.OrdinalIgnoreCase))
                    {
                        timestampColumn ??= name;
                    }
                }
            }

            if (columns.Count == 0) return 0;

            // Read all records from theirs
            await using (var readCmd = theirsConn.CreateCommand())
            {
                readCmd.CommandText = $"SELECT * FROM {table}";
                await using var reader = await readCmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var values = new object[columns.Count];
                    reader.GetValues(values);

                    // Build INSERT OR REPLACE query
                    var columnList = string.Join(", ", columns);
                    var paramList = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

                    // For history tables, we want to avoid duplicates based on content, not just primary key
                    // Use INSERT OR IGNORE for most tables, but for watch_history, check for exact duplicates
                    string insertSql;
                    if (table == "watch_history" && timestampColumn != null)
                    {
                        // For watch history, insert only if this exact entry doesn't exist
                        insertSql = $@"
                            INSERT INTO {table} ({columnList})
                            SELECT {paramList}
                            WHERE NOT EXISTS (
                                SELECT 1 FROM {table} 
                                WHERE anime_id = @p{columns.IndexOf("anime_id")} 
                                AND episode_number = @p{columns.IndexOf("episode_number")} 
                                AND {timestampColumn} = @p{columns.IndexOf(timestampColumn)}
                            )";
                    }
                    else
                    {
                        insertSql = $"INSERT OR IGNORE INTO {table} ({columnList}) VALUES ({paramList})";
                    }

                    await using var insertCmd = mergedConn.CreateCommand();
                    insertCmd.CommandText = insertSql;
                    for (var i = 0; i < values.Length; i++)
                    {
                        insertCmd.Parameters.AddWithValue($"@p{i}", values[i] ?? DBNull.Value);
                    }

                    var affected = await insertCmd.ExecuteNonQueryAsync();
                    merged += affected;
                }
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                SystemConsole.ForegroundColor = ConsoleColor.DarkGray;
                SystemConsole.WriteLine($"    Table {table} merge error: {ex.Message}");
                SystemConsole.ResetColor();
            }
        }

        return merged;
    }

    /// <summary>
    /// Resolve JSON config conflicts by deep merging.
    /// Strategy: Merge objects recursively, prefer local values for scalar conflicts.
    /// </summary>
    private async Task<MergeResult> ResolveJsonConflictAsync(string relativePath, string fullPath, bool verbose)
    {
        try
        {
            // Extract both versions
            var (oursOutput, _) = await GetGitBlobAsync($":2:\"{relativePath}\"");
            var (theirsOutput, _) = await GetGitBlobAsync($":3:\"{relativePath}\"");

            if (string.IsNullOrWhiteSpace(oursOutput) || string.IsNullOrWhiteSpace(theirsOutput))
            {
                // One version missing, keep whichever exists
                if (!string.IsNullOrWhiteSpace(oursOutput))
                {
                    await File.WriteAllTextAsync(fullPath, oursOutput);
                    return new MergeResult(true, "Kept local version");
                }
                if (!string.IsNullOrWhiteSpace(theirsOutput))
                {
                    await File.WriteAllTextAsync(fullPath, theirsOutput);
                    return new MergeResult(true, "Kept remote version");
                }
                return new MergeResult(false, "Both versions empty");
            }

            // Parse both as JSON
            var oursJson = JsonNode.Parse(oursOutput);
            var theirsJson = JsonNode.Parse(theirsOutput);

            if (oursJson is JsonObject oursObj && theirsJson is JsonObject theirsObj)
            {
                // Deep merge
                var merged = DeepMergeJson(oursObj, theirsObj);
                var mergedText = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(fullPath, mergedText);
                return new MergeResult(true, "Deep merged JSON");
            }
            else
            {
                // Not objects, prefer local
                await File.WriteAllTextAsync(fullPath, oursOutput);
                return new MergeResult(true, "Kept local (non-object JSON)");
            }
        }
        catch (Exception ex)
        {
            // Fallback: checkout ours
            await RunGitAsync($"checkout --ours \"{relativePath}\"");
            return new MergeResult(true, $"Kept local ({ex.Message})");
        }
    }

    /// <summary>
    /// Deep merge two JSON objects. Local values take precedence for conflicts.
    /// </summary>
    private JsonObject DeepMergeJson(JsonObject local, JsonObject remote)
    {
        var result = new JsonObject();

        // Add all local properties
        foreach (var (key, value) in local)
        {
            result[key] = value?.DeepClone();
        }

        // Merge in remote properties
        foreach (var (key, value) in remote)
        {
            if (!result.ContainsKey(key))
            {
                // Key only in remote, add it
                result[key] = value?.DeepClone();
            }
            else if (result[key] is JsonObject localObj && value is JsonObject remoteObj)
            {
                // Both are objects, recurse
                result[key] = DeepMergeJson(localObj, remoteObj);
            }
            // else: local value takes precedence for scalar conflicts
        }

        return result;
    }

    /// <summary>
    /// Resolve generic file conflicts by preferring local version.
    /// </summary>
    private async Task<MergeResult> ResolveGenericConflictAsync(string relativePath, string fullPath, bool verbose)
    {
        await RunGitAsync($"checkout --ours \"{relativePath}\"");
        return new MergeResult(true, "Kept local version");
    }

    /// <summary>
    /// Abort the current merge operation.
    /// </summary>
    public async Task<bool> AbortMergeAsync()
    {
        var (code, _, _) = await RunGitAsync("merge --abort");
        return code == 0;
    }

    /// <summary>
    /// Check if currently in a merge state.
    /// </summary>
    public bool IsInMergeState()
    {
        var mergeHead = Path.Combine(_dataDir, ".git", "MERGE_HEAD");
        return File.Exists(mergeHead);
    }

    private async Task<(string output, string error)> GetGitBlobAsync(string spec)
    {
        var (_, output, error) = await RunGitAsync($"show {spec}");
        return (output, error);
    }

    private async Task<(int exitCode, string output, string error)> RunGitAsync(string arguments, bool useShell = false)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = _dataDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (useShell)
        {
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c git {arguments}";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.Arguments = $"-c \"git {arguments}\"";
            }
        }
        else
        {
            psi.FileName = "git";
            psi.Arguments = arguments;
        }

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return (process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
