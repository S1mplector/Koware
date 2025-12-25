// Author: Ilgaz Mehmetoğlu
// Manga list/tracking persistence with SQLite.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Koware.Cli.Configuration;

namespace Koware.Cli.History;

/// <summary>
/// Read status for a manga in the user's list.
/// </summary>
public enum MangaReadStatus
{
    /// <summary>Currently reading.</summary>
    Reading,
    /// <summary>Finished all chapters.</summary>
    Completed,
    /// <summary>Planning to read in the future.</summary>
    PlanToRead,
    /// <summary>Temporarily paused.</summary>
    OnHold,
    /// <summary>Stopped reading, not planning to finish.</summary>
    Dropped
}

/// <summary>
/// Represents a manga entry in the user's tracking list.
/// </summary>
public sealed class MangaListEntry
{
    public long Id { get; init; }
    public string MangaId { get; init; } = string.Empty;
    public string MangaTitle { get; init; } = string.Empty;
    public MangaReadStatus Status { get; init; }
    public int? TotalChapters { get; init; }
    public int ChaptersRead { get; init; }
    public int? Score { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Aggregated stats for the manga list.
/// </summary>
public sealed record MangaListStats(
    int Reading,
    int Completed,
    int PlanToRead,
    int OnHold,
    int Dropped,
    int TotalChaptersRead);

/// <summary>
/// Interface for manga list persistence.
/// </summary>
public interface IMangaListStore
{
    /// <summary>Add a manga to the list.</summary>
    Task<MangaListEntry> AddAsync(string mangaId, string mangaTitle, MangaReadStatus status, int? totalChapters = null, CancellationToken cancellationToken = default);

    /// <summary>Update an existing manga entry.</summary>
    Task<bool> UpdateAsync(string mangaTitle, MangaReadStatus? status = null, int? chaptersRead = null, int? totalChapters = null, int? score = null, string? notes = null, CancellationToken cancellationToken = default);

    /// <summary>Remove a manga from the list.</summary>
    Task<bool> RemoveAsync(string mangaTitle, CancellationToken cancellationToken = default);

    /// <summary>Get a manga by title (case-insensitive).</summary>
    Task<MangaListEntry?> GetByTitleAsync(string mangaTitle, CancellationToken cancellationToken = default);

    /// <summary>Search for a manga by partial title match.</summary>
    Task<MangaListEntry?> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Get all manga matching a status filter.</summary>
    Task<IReadOnlyList<MangaListEntry>> GetAllAsync(MangaReadStatus? statusFilter = null, CancellationToken cancellationToken = default);

    /// <summary>Get aggregated stats.</summary>
    Task<MangaListStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Record that a chapter was read; updates chapters_read count and may auto-complete.</summary>
    Task RecordChapterReadAsync(string mangaId, string mangaTitle, float chapterNumber, int? totalChapters = null, CancellationToken cancellationToken = default);

    /// <summary>Mark a manga as completed.</summary>
    Task MarkCompletedAsync(string mangaTitle, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite-backed manga list store.
/// </summary>
/// <remarks>
/// Stores list in %APPDATA%/koware/history.db (same file as read history).
/// </remarks>
public sealed class SqliteMangaListStore : IMangaListStore
{
    private const string TableName = "manga_list";
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteMangaListStore(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.CurrentDirectory;
        }

        var dir = Path.Combine(baseDir, "koware");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "history.db");
    }

    /// <summary>
    /// Parameterless constructor for backward compatibility.
    /// </summary>
    public SqliteMangaListStore() : this(new DatabaseConnectionFactory())
    {
    }

    public async Task<MangaListEntry> AddAsync(string mangaId, string mangaTitle, MangaReadStatus status, int? totalChapters = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        // Check if already exists
        var existing = await GetByTitleInternalAsync(connection, mangaTitle, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Manga '{mangaTitle}' is already in your list.");
        }

        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName} (manga_id, manga_title, status, total_chapters, chapters_read, score, notes, added_at, updated_at, completed_at)
            VALUES ($mangaId, $mangaTitle, $status, $totalChapters, 0, NULL, NULL, $now, $now, NULL);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$mangaId", mangaId);
        command.Parameters.AddWithValue("$mangaTitle", mangaTitle);
        command.Parameters.AddWithValue("$status", StatusToString(status));
        command.Parameters.AddWithValue("$totalChapters", totalChapters.HasValue ? totalChapters.Value : DBNull.Value);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        return new MangaListEntry
        {
            Id = id,
            MangaId = mangaId,
            MangaTitle = mangaTitle,
            Status = status,
            TotalChapters = totalChapters,
            ChaptersRead = 0,
            Score = null,
            Notes = null,
            AddedAt = now,
            UpdatedAt = now,
            CompletedAt = null
        };
    }

    public async Task<bool> UpdateAsync(string mangaTitle, MangaReadStatus? status = null, int? chaptersRead = null, int? totalChapters = null, int? score = null, string? notes = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sets = new List<string> { "updated_at = $now" };
        var parameters = new List<SqliteParameter>
        {
            new("$mangaTitle", mangaTitle),
            new("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"))
        };

        if (status.HasValue)
        {
            sets.Add("status = $status");
            parameters.Add(new SqliteParameter("$status", StatusToString(status.Value)));

            if (status.Value == MangaReadStatus.Completed)
            {
                sets.Add("completed_at = $completedAt");
                parameters.Add(new SqliteParameter("$completedAt", DateTimeOffset.UtcNow.UtcDateTime.ToString("O")));
            }
        }

        if (chaptersRead.HasValue)
        {
            sets.Add("chapters_read = $chaptersRead");
            parameters.Add(new SqliteParameter("$chaptersRead", chaptersRead.Value));
        }

        if (totalChapters.HasValue)
        {
            sets.Add("total_chapters = $totalChapters");
            parameters.Add(new SqliteParameter("$totalChapters", totalChapters.Value));
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
            WHERE manga_title = $mangaTitle COLLATE NOCASE;
            """;

        foreach (var p in parameters)
        {
            command.Parameters.Add(p);
        }

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<bool> RemoveAsync(string mangaTitle, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE manga_title = $mangaTitle COLLATE NOCASE;";
        command.Parameters.AddWithValue("$mangaTitle", mangaTitle);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<MangaListEntry?> GetByTitleAsync(string mangaTitle, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        return await GetByTitleInternalAsync(connection, mangaTitle, cancellationToken);
    }

    public async Task<MangaListEntry?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, manga_id, manga_title, status, total_chapters, chapters_read, score, notes, added_at, updated_at, completed_at
            FROM {TableName}
            WHERE manga_title LIKE $pattern ESCAPE '\' COLLATE NOCASE
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");

        return await ReadSingleEntryAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<MangaListEntry>> GetAllAsync(MangaReadStatus? statusFilter = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var where = statusFilter.HasValue ? "WHERE status = $status" : string.Empty;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, manga_id, manga_title, status, total_chapters, chapters_read, score, notes, added_at, updated_at, completed_at
            FROM {TableName}
            {where}
            ORDER BY 
                CASE status 
                    WHEN 'reading' THEN 1 
                    WHEN 'plan_to_read' THEN 2 
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

        var results = new List<MangaListEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task<MangaListStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 
                COALESCE(SUM(CASE WHEN status = 'reading' THEN 1 ELSE 0 END), 0) as reading,
                COALESCE(SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END), 0) as completed,
                COALESCE(SUM(CASE WHEN status = 'plan_to_read' THEN 1 ELSE 0 END), 0) as plan_to_read,
                COALESCE(SUM(CASE WHEN status = 'on_hold' THEN 1 ELSE 0 END), 0) as on_hold,
                COALESCE(SUM(CASE WHEN status = 'dropped' THEN 1 ELSE 0 END), 0) as dropped,
                COALESCE(SUM(chapters_read), 0) as total_chapters_read
            FROM {TableName};
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new MangaListStats(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5));
        }

        return new MangaListStats(0, 0, 0, 0, 0, 0);
    }

    public async Task RecordChapterReadAsync(string mangaId, string mangaTitle, float chapterNumber, int? totalChapters = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var existing = await GetByTitleInternalAsync(connection, mangaTitle, cancellationToken);
        var chapterInt = (int)Math.Ceiling(chapterNumber);

        if (existing is null)
        {
            // Auto-add to list as "reading"
            await using var insertCmd = connection.CreateCommand();
            var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
            insertCmd.CommandText = $"""
                INSERT INTO {TableName} (manga_id, manga_title, status, total_chapters, chapters_read, score, notes, added_at, updated_at, completed_at)
                VALUES ($mangaId, $mangaTitle, 'reading', $totalChapters, $chaptersRead, NULL, NULL, $now, $now, NULL);
                """;
            insertCmd.Parameters.AddWithValue("$mangaId", mangaId);
            insertCmd.Parameters.AddWithValue("$mangaTitle", mangaTitle);
            insertCmd.Parameters.AddWithValue("$totalChapters", totalChapters.HasValue ? totalChapters.Value : DBNull.Value);
            insertCmd.Parameters.AddWithValue("$chaptersRead", chapterInt);
            insertCmd.Parameters.AddWithValue("$now", now);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        // Update chapters read if this is a higher chapter
        var newChaptersRead = Math.Max(existing.ChaptersRead, chapterInt);
        var newTotalChapters = totalChapters ?? existing.TotalChapters;
        var now2 = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");

        // Determine status transitions
        var shouldStartReading = existing.Status is MangaReadStatus.PlanToRead or MangaReadStatus.OnHold;
        var shouldComplete = newTotalChapters.HasValue && 
                            newChaptersRead >= newTotalChapters.Value && 
                            (existing.Status == MangaReadStatus.Reading || shouldStartReading);

        await using var updateCmd = connection.CreateCommand();
        if (shouldComplete)
        {
            updateCmd.CommandText = $"""
                UPDATE {TableName}
                SET chapters_read = $chaptersRead, 
                    total_chapters = $totalChapters,
                    status = 'completed',
                    completed_at = $now,
                    updated_at = $now
                WHERE id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$id", existing.Id);
            updateCmd.Parameters.AddWithValue("$chaptersRead", newChaptersRead);
            updateCmd.Parameters.AddWithValue("$totalChapters", newTotalChapters.HasValue ? newTotalChapters.Value : DBNull.Value);
            updateCmd.Parameters.AddWithValue("$now", now2);
        }
        else if (shouldStartReading)
        {
            updateCmd.CommandText = $"""
                UPDATE {TableName}
                SET chapters_read = $chaptersRead, 
                    total_chapters = COALESCE($totalChapters, total_chapters),
                    status = 'reading',
                    updated_at = $now
                WHERE id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$id", existing.Id);
            updateCmd.Parameters.AddWithValue("$chaptersRead", newChaptersRead);
            updateCmd.Parameters.AddWithValue("$totalChapters", newTotalChapters.HasValue ? newTotalChapters.Value : DBNull.Value);
            updateCmd.Parameters.AddWithValue("$now", now2);
        }
        else
        {
            updateCmd.CommandText = $"""
                UPDATE {TableName}
                SET chapters_read = $chaptersRead, 
                    total_chapters = COALESCE($totalChapters, total_chapters),
                    updated_at = $now
                WHERE id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$id", existing.Id);
            updateCmd.Parameters.AddWithValue("$chaptersRead", newChaptersRead);
            updateCmd.Parameters.AddWithValue("$totalChapters", newTotalChapters.HasValue ? newTotalChapters.Value : DBNull.Value);
            updateCmd.Parameters.AddWithValue("$now", now2);
        }

        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(string mangaTitle, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(mangaTitle, status: MangaReadStatus.Completed, cancellationToken: cancellationToken);
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

            await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {TableName} (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    manga_id TEXT NOT NULL,
                    manga_title TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'plan_to_read',
                    total_chapters INTEGER NULL,
                    chapters_read INTEGER NOT NULL DEFAULT 0,
                    score INTEGER NULL,
                    notes TEXT NULL,
                    added_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    completed_at TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_mangalist_title ON {TableName} (manga_title COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_mangalist_status ON {TableName} (status);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<MangaListEntry?> GetByTitleInternalAsync(SqliteConnection connection, string mangaTitle, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, manga_id, manga_title, status, total_chapters, chapters_read, score, notes, added_at, updated_at, completed_at
            FROM {TableName}
            WHERE manga_title = $mangaTitle COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$mangaTitle", mangaTitle);

        return await ReadSingleEntryAsync(command, cancellationToken);
    }

    private static async Task<MangaListEntry?> ReadSingleEntryAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    private static MangaListEntry ReadEntry(SqliteDataReader reader)
    {
        return new MangaListEntry
        {
            Id = reader.GetInt64(0),
            MangaId = reader.GetString(1),
            MangaTitle = reader.GetString(2),
            Status = ParseStatus(reader.GetString(3)),
            TotalChapters = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            ChaptersRead = reader.GetInt32(5),
            Score = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
            AddedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CompletedAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    private static string StatusToString(MangaReadStatus status) => status switch
    {
        MangaReadStatus.Reading => "reading",
        MangaReadStatus.Completed => "completed",
        MangaReadStatus.PlanToRead => "plan_to_read",
        MangaReadStatus.OnHold => "on_hold",
        MangaReadStatus.Dropped => "dropped",
        _ => "plan_to_read"
    };

    private static MangaReadStatus ParseStatus(string status) => status switch
    {
        "reading" => MangaReadStatus.Reading,
        "completed" => MangaReadStatus.Completed,
        "plan_to_read" => MangaReadStatus.PlanToRead,
        "on_hold" => MangaReadStatus.OnHold,
        "dropped" => MangaReadStatus.Dropped,
        _ => MangaReadStatus.PlanToRead
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
/// Extension methods for parsing manga status from CLI arguments.
/// </summary>
public static class MangaReadStatusExtensions
{
    public static MangaReadStatus? ParseStatusArg(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return arg.ToLowerInvariant() switch
        {
            "reading" or "r" => MangaReadStatus.Reading,
            "completed" or "complete" or "c" or "done" => MangaReadStatus.Completed,
            "plan" or "plan_to_read" or "ptr" or "plantoread" => MangaReadStatus.PlanToRead,
            "hold" or "on_hold" or "onhold" or "paused" => MangaReadStatus.OnHold,
            "dropped" or "drop" or "d" => MangaReadStatus.Dropped,
            _ => null
        };
    }

    public static string ToDisplayString(this MangaReadStatus status) => status switch
    {
        MangaReadStatus.Reading => "Reading",
        MangaReadStatus.Completed => "Completed",
        MangaReadStatus.PlanToRead => "Plan to Read",
        MangaReadStatus.OnHold => "On Hold",
        MangaReadStatus.Dropped => "Dropped",
        _ => "Unknown"
    };

    public static ConsoleColor ToColor(this MangaReadStatus status) => status switch
    {
        MangaReadStatus.Reading => ConsoleColor.Green,
        MangaReadStatus.Completed => ConsoleColor.Blue,
        MangaReadStatus.PlanToRead => ConsoleColor.Cyan,
        MangaReadStatus.OnHold => ConsoleColor.Yellow,
        MangaReadStatus.Dropped => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray
    };
}
