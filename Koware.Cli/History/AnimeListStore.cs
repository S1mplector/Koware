// Author: Ilgaz Mehmetoğlu
// Anime list/tracking persistence with SQLite.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Koware.Cli.History;

/// <summary>
/// Watch status for an anime in the user's list.
/// </summary>
public enum AnimeWatchStatus
{
    /// <summary>Currently watching.</summary>
    Watching,
    /// <summary>Finished all episodes.</summary>
    Completed,
    /// <summary>Planning to watch in the future.</summary>
    PlanToWatch,
    /// <summary>Temporarily paused.</summary>
    OnHold,
    /// <summary>Stopped watching, not planning to finish.</summary>
    Dropped
}

/// <summary>
/// Represents an anime entry in the user's tracking list.
/// </summary>
public sealed class AnimeListEntry
{
    public long Id { get; init; }
    public string AnimeId { get; init; } = string.Empty;
    public string AnimeTitle { get; init; } = string.Empty;
    public AnimeWatchStatus Status { get; init; }
    public int? TotalEpisodes { get; init; }
    public int EpisodesWatched { get; init; }
    public int? Score { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Aggregated stats for the anime list.
/// </summary>
public sealed record AnimeListStats(
    int Watching,
    int Completed,
    int PlanToWatch,
    int OnHold,
    int Dropped,
    int TotalEpisodesWatched);

/// <summary>
/// Interface for anime list persistence.
/// </summary>
public interface IAnimeListStore
{
    /// <summary>Add an anime to the list.</summary>
    Task<AnimeListEntry> AddAsync(string animeId, string animeTitle, AnimeWatchStatus status, int? totalEpisodes = null, CancellationToken cancellationToken = default);

    /// <summary>Update an existing anime entry.</summary>
    Task<bool> UpdateAsync(string animeTitle, AnimeWatchStatus? status = null, int? episodesWatched = null, int? totalEpisodes = null, int? score = null, string? notes = null, CancellationToken cancellationToken = default);

    /// <summary>Remove an anime from the list.</summary>
    Task<bool> RemoveAsync(string animeTitle, CancellationToken cancellationToken = default);

    /// <summary>Get an anime by title (case-insensitive).</summary>
    Task<AnimeListEntry?> GetByTitleAsync(string animeTitle, CancellationToken cancellationToken = default);

    /// <summary>Search for an anime by partial title match.</summary>
    Task<AnimeListEntry?> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Get all anime matching a status filter.</summary>
    Task<IReadOnlyList<AnimeListEntry>> GetAllAsync(AnimeWatchStatus? statusFilter = null, CancellationToken cancellationToken = default);

    /// <summary>Get aggregated stats.</summary>
    Task<AnimeListStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Record that an episode was watched; updates episodes_watched count and may auto-complete.</summary>
    Task RecordEpisodeWatchedAsync(string animeId, string animeTitle, int episodeNumber, int? totalEpisodes = null, CancellationToken cancellationToken = default);

    /// <summary>Mark an anime as completed.</summary>
    Task MarkCompletedAsync(string animeTitle, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite-backed anime list store.
/// </summary>
/// <remarks>
/// Stores list in %APPDATA%/koware/history.db (same file as watch history).
/// </remarks>
public sealed class SqliteAnimeListStore : IAnimeListStore
{
    private const string TableName = "anime_list";
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteAnimeListStore()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.CurrentDirectory;
        }

        var dir = Path.Combine(baseDir, "koware");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public async Task<AnimeListEntry> AddAsync(string animeId, string animeTitle, AnimeWatchStatus status, int? totalEpisodes = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Check if already exists
        var existing = await GetByTitleInternalAsync(connection, animeTitle, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Anime '{animeTitle}' is already in your list.");
        }

        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName} (anime_id, anime_title, status, total_episodes, episodes_watched, score, notes, added_at, updated_at, completed_at)
            VALUES ($animeId, $animeTitle, $status, $totalEpisodes, 0, NULL, NULL, $now, $now, NULL);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$animeId", animeId);
        command.Parameters.AddWithValue("$animeTitle", animeTitle);
        command.Parameters.AddWithValue("$status", StatusToString(status));
        command.Parameters.AddWithValue("$totalEpisodes", totalEpisodes.HasValue ? totalEpisodes.Value : DBNull.Value);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        return new AnimeListEntry
        {
            Id = id,
            AnimeId = animeId,
            AnimeTitle = animeTitle,
            Status = status,
            TotalEpisodes = totalEpisodes,
            EpisodesWatched = 0,
            Score = null,
            Notes = null,
            AddedAt = now,
            UpdatedAt = now,
            CompletedAt = null
        };
    }

    public async Task<bool> UpdateAsync(string animeTitle, AnimeWatchStatus? status = null, int? episodesWatched = null, int? totalEpisodes = null, int? score = null, string? notes = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sets = new List<string> { "updated_at = $now" };
        var parameters = new List<SqliteParameter>
        {
            new("$animeTitle", animeTitle),
            new("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"))
        };

        if (status.HasValue)
        {
            sets.Add("status = $status");
            parameters.Add(new SqliteParameter("$status", StatusToString(status.Value)));

            if (status.Value == AnimeWatchStatus.Completed)
            {
                sets.Add("completed_at = $completedAt");
                parameters.Add(new SqliteParameter("$completedAt", DateTimeOffset.UtcNow.UtcDateTime.ToString("O")));
            }
        }

        if (episodesWatched.HasValue)
        {
            sets.Add("episodes_watched = $episodesWatched");
            parameters.Add(new SqliteParameter("$episodesWatched", episodesWatched.Value));
        }

        if (totalEpisodes.HasValue)
        {
            sets.Add("total_episodes = $totalEpisodes");
            parameters.Add(new SqliteParameter("$totalEpisodes", totalEpisodes.Value));
        }

        if (score.HasValue)
        {
            sets.Add("score = $score");
            parameters.Add(new SqliteParameter("$score", score.Value));
        }

        if (notes is not null)
        {
            sets.Add("notes = $notes");
            parameters.Add(new SqliteParameter("$notes", notes));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {TableName}
            SET {string.Join(", ", sets)}
            WHERE anime_title = $animeTitle COLLATE NOCASE;
            """;

        foreach (var p in parameters)
        {
            command.Parameters.Add(p);
        }

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<bool> RemoveAsync(string animeTitle, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE anime_title = $animeTitle COLLATE NOCASE;";
        command.Parameters.AddWithValue("$animeTitle", animeTitle);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<AnimeListEntry?> GetByTitleAsync(string animeTitle, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        return await GetByTitleInternalAsync(connection, animeTitle, cancellationToken);
    }

    public async Task<AnimeListEntry?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, anime_id, anime_title, status, total_episodes, episodes_watched, score, notes, added_at, updated_at, completed_at
            FROM {TableName}
            WHERE anime_title LIKE $pattern ESCAPE '\' COLLATE NOCASE
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");

        return await ReadSingleEntryAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AnimeListEntry>> GetAllAsync(AnimeWatchStatus? statusFilter = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var where = statusFilter.HasValue ? "WHERE status = $status" : string.Empty;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, anime_id, anime_title, status, total_episodes, episodes_watched, score, notes, added_at, updated_at, completed_at
            FROM {TableName}
            {where}
            ORDER BY 
                CASE status 
                    WHEN 'watching' THEN 1 
                    WHEN 'plan_to_watch' THEN 2 
                    WHEN 'on_hold' THEN 3 
                    WHEN 'completed' THEN 4 
                    WHEN 'dropped' THEN 5 
                END,
                updated_at DESC;
            """;

        if (statusFilter.HasValue)
        {
            command.Parameters.AddWithValue("$status", StatusToString(statusFilter.Value));
        }

        var results = new List<AnimeListEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task<AnimeListStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 
                COALESCE(SUM(CASE WHEN status = 'watching' THEN 1 ELSE 0 END), 0) as watching,
                COALESCE(SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END), 0) as completed,
                COALESCE(SUM(CASE WHEN status = 'plan_to_watch' THEN 1 ELSE 0 END), 0) as plan_to_watch,
                COALESCE(SUM(CASE WHEN status = 'on_hold' THEN 1 ELSE 0 END), 0) as on_hold,
                COALESCE(SUM(CASE WHEN status = 'dropped' THEN 1 ELSE 0 END), 0) as dropped,
                COALESCE(SUM(episodes_watched), 0) as total_episodes_watched
            FROM {TableName};
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new AnimeListStats(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5));
        }

        return new AnimeListStats(0, 0, 0, 0, 0, 0);
    }

    public async Task RecordEpisodeWatchedAsync(string animeId, string animeTitle, int episodeNumber, int? totalEpisodes = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var existing = await GetByTitleInternalAsync(connection, animeTitle, cancellationToken);

        if (existing is null)
        {
            // Auto-add to list as "watching"
            await using var insertCmd = connection.CreateCommand();
            var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
            insertCmd.CommandText = $"""
                INSERT INTO {TableName} (anime_id, anime_title, status, total_episodes, episodes_watched, score, notes, added_at, updated_at, completed_at)
                VALUES ($animeId, $animeTitle, 'watching', $totalEpisodes, $episodesWatched, NULL, NULL, $now, $now, NULL);
                """;
            insertCmd.Parameters.AddWithValue("$animeId", animeId);
            insertCmd.Parameters.AddWithValue("$animeTitle", animeTitle);
            insertCmd.Parameters.AddWithValue("$totalEpisodes", totalEpisodes.HasValue ? totalEpisodes.Value : DBNull.Value);
            insertCmd.Parameters.AddWithValue("$episodesWatched", episodeNumber);
            insertCmd.Parameters.AddWithValue("$now", now);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        // Update episodes watched if this is a higher episode
        var newEpisodesWatched = Math.Max(existing.EpisodesWatched, episodeNumber);
        var newTotalEpisodes = totalEpisodes ?? existing.TotalEpisodes;
        var now2 = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");

        // Check if should auto-complete
        var shouldComplete = newTotalEpisodes.HasValue && 
                            newEpisodesWatched >= newTotalEpisodes.Value && 
                            existing.Status == AnimeWatchStatus.Watching;

        await using var updateCmd = connection.CreateCommand();
        if (shouldComplete)
        {
            updateCmd.CommandText = $"""
                UPDATE {TableName}
                SET episodes_watched = $episodesWatched, 
                    total_episodes = $totalEpisodes,
                    status = 'completed',
                    completed_at = $now,
                    updated_at = $now
                WHERE id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$id", existing.Id);
            updateCmd.Parameters.AddWithValue("$episodesWatched", newEpisodesWatched);
            updateCmd.Parameters.AddWithValue("$totalEpisodes", newTotalEpisodes.HasValue ? newTotalEpisodes.Value : DBNull.Value);
            updateCmd.Parameters.AddWithValue("$now", now2);
        }
        else
        {
            updateCmd.CommandText = $"""
                UPDATE {TableName}
                SET episodes_watched = $episodesWatched, 
                    total_episodes = COALESCE($totalEpisodes, total_episodes),
                    updated_at = $now
                WHERE id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$id", existing.Id);
            updateCmd.Parameters.AddWithValue("$episodesWatched", newEpisodesWatched);
            updateCmd.Parameters.AddWithValue("$totalEpisodes", newTotalEpisodes.HasValue ? newTotalEpisodes.Value : DBNull.Value);
            updateCmd.Parameters.AddWithValue("$now", now2);
        }

        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(string animeTitle, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(animeTitle, status: AnimeWatchStatus.Completed, cancellationToken: cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {TableName} (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    anime_id TEXT NOT NULL,
                    anime_title TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'plan_to_watch',
                    total_episodes INTEGER NULL,
                    episodes_watched INTEGER NOT NULL DEFAULT 0,
                    score INTEGER NULL,
                    notes TEXT NULL,
                    added_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_animelist_title ON {TableName} (anime_title COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_animelist_status ON {TableName} (status);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<AnimeListEntry?> GetByTitleInternalAsync(SqliteConnection connection, string animeTitle, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, anime_id, anime_title, status, total_episodes, episodes_watched, score, notes, added_at, updated_at, completed_at
            FROM {TableName}
            WHERE anime_title = $animeTitle COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$animeTitle", animeTitle);

        return await ReadSingleEntryAsync(command, cancellationToken);
    }

    private static async Task<AnimeListEntry?> ReadSingleEntryAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    private static AnimeListEntry ReadEntry(SqliteDataReader reader)
    {
        return new AnimeListEntry
        {
            Id = reader.GetInt64(0),
            AnimeId = reader.GetString(1),
            AnimeTitle = reader.GetString(2),
            Status = ParseStatus(reader.GetString(3)),
            TotalEpisodes = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            EpisodesWatched = reader.GetInt32(5),
            Score = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
            AddedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CompletedAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    private static string StatusToString(AnimeWatchStatus status) => status switch
    {
        AnimeWatchStatus.Watching => "watching",
        AnimeWatchStatus.Completed => "completed",
        AnimeWatchStatus.PlanToWatch => "plan_to_watch",
        AnimeWatchStatus.OnHold => "on_hold",
        AnimeWatchStatus.Dropped => "dropped",
        _ => "plan_to_watch"
    };

    private static AnimeWatchStatus ParseStatus(string status) => status switch
    {
        "watching" => AnimeWatchStatus.Watching,
        "completed" => AnimeWatchStatus.Completed,
        "plan_to_watch" => AnimeWatchStatus.PlanToWatch,
        "on_hold" => AnimeWatchStatus.OnHold,
        "dropped" => AnimeWatchStatus.Dropped,
        _ => AnimeWatchStatus.PlanToWatch
    };

    private static string EscapeLike(string input)
    {
        return input
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}

/// <summary>
/// Extension methods for parsing status from CLI arguments.
/// </summary>
public static class AnimeWatchStatusExtensions
{
    public static AnimeWatchStatus? ParseStatusArg(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return arg.ToLowerInvariant() switch
        {
            "watching" or "w" => AnimeWatchStatus.Watching,
            "completed" or "complete" or "c" or "done" => AnimeWatchStatus.Completed,
            "plan" or "plan_to_watch" or "ptw" or "plantowatch" => AnimeWatchStatus.PlanToWatch,
            "hold" or "on_hold" or "onhold" or "paused" => AnimeWatchStatus.OnHold,
            "dropped" or "drop" or "d" => AnimeWatchStatus.Dropped,
            _ => null
        };
    }

    public static string ToDisplayString(this AnimeWatchStatus status) => status switch
    {
        AnimeWatchStatus.Watching => "Watching",
        AnimeWatchStatus.Completed => "Completed",
        AnimeWatchStatus.PlanToWatch => "Plan to Watch",
        AnimeWatchStatus.OnHold => "On Hold",
        AnimeWatchStatus.Dropped => "Dropped",
        _ => "Unknown"
    };

    public static ConsoleColor ToColor(this AnimeWatchStatus status) => status switch
    {
        AnimeWatchStatus.Watching => ConsoleColor.Green,
        AnimeWatchStatus.Completed => ConsoleColor.Blue,
        AnimeWatchStatus.PlanToWatch => ConsoleColor.Cyan,
        AnimeWatchStatus.OnHold => ConsoleColor.Yellow,
        AnimeWatchStatus.Dropped => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray
    };
}
