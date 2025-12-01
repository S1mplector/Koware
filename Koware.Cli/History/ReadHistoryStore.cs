// Author: Ilgaz Mehmetoğlu
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Koware.Cli.History;

/// <summary>
/// Represents a single read history entry with manga, chapter, and timestamp.
/// </summary>
public sealed class ReadHistoryEntry
{
    public string Provider { get; init; } = string.Empty;

    public string MangaId { get; init; } = string.Empty;

    public string MangaTitle { get; init; } = string.Empty;

    public float ChapterNumber { get; init; }

    public string? ChapterTitle { get; init; }

    public DateTimeOffset ReadAt { get; init; }
}

/// <summary>
/// Interface for read history persistence.
/// </summary>
public interface IReadHistoryStore
{
    /// <summary>Add a new read history entry.</summary>
    Task AddAsync(ReadHistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Get the most recent read entry.</summary>
    Task<ReadHistoryEntry?> GetLastAsync(CancellationToken cancellationToken = default);

    /// <summary>Get the most recent entry for a specific manga title.</summary>
    Task<ReadHistoryEntry?> GetLastForMangaAsync(string mangaTitle, CancellationToken cancellationToken = default);

    /// <summary>Search history by title (fuzzy match) and return the most recent match.</summary>
    Task<ReadHistoryEntry?> SearchLastAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Query history with filters (manga, date range, chapter range, limit).</summary>
    Task<IReadOnlyList<ReadHistoryEntry>> QueryAsync(ReadHistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Get aggregated stats per manga (count, last read).</summary>
    Task<IReadOnlyList<ReadHistoryStat>> GetStatsAsync(string? mangaFilter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Query parameters for filtering read history.
/// </summary>
/// <param name="MangaContains">Filter by manga title (case-insensitive contains).</param>
/// <param name="After">Only entries after this timestamp.</param>
/// <param name="Before">Only entries before this timestamp.</param>
/// <param name="FromChapter">Minimum chapter number.</param>
/// <param name="ToChapter">Maximum chapter number.</param>
/// <param name="Limit">Maximum entries to return.</param>
public sealed record ReadHistoryQuery(
    string? MangaContains,
    DateTimeOffset? After,
    DateTimeOffset? Before,
    float? FromChapter,
    float? ToChapter,
    int Limit);

/// <summary>
/// Aggregated read stats for a manga.
/// </summary>
/// <param name="MangaTitle">Manga title.</param>
/// <param name="Count">Number of read entries.</param>
/// <param name="LastRead">Most recent read timestamp.</param>
public sealed record ReadHistoryStat(
    string MangaTitle,
    int Count,
    DateTimeOffset LastRead);

/// <summary>
/// SQLite-backed read history store for manga.
/// </summary>
/// <remarks>
/// Stores history in %APPDATA%/koware/history.db (same file as watch history, different table).
/// </remarks>
public sealed class SqliteReadHistoryStore : IReadHistoryStore
{
    private const string TableName = "read_history";
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteReadHistoryStore()
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

    public async Task AddAsync(ReadHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await InsertAsync(connection, entry, cancellationToken);
    }

    public async Task<ReadHistoryEntry?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT provider, manga_id, manga_title, chapter_number, chapter_title, read_at_utc
            FROM {TableName}
            ORDER BY read_at_utc DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    public async Task<ReadHistoryEntry?> GetLastForMangaAsync(string mangaTitle, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT provider, manga_id, manga_title, chapter_number, chapter_title, read_at_utc
            FROM {TableName}
            WHERE manga_title = @title COLLATE NOCASE
            ORDER BY read_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@title", mangaTitle);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    public async Task<ReadHistoryEntry?> SearchLastAsync(string query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT provider, manga_id, manga_title, chapter_number, chapter_title, read_at_utc
            FROM {TableName}
            WHERE manga_title LIKE @pattern COLLATE NOCASE
            ORDER BY read_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@pattern", $"%{query}%");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    public async Task<IReadOnlyList<ReadHistoryEntry>> QueryAsync(ReadHistoryQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(query.MangaContains))
        {
            whereClauses.Add("manga_title LIKE @manga COLLATE NOCASE");
            parameters.Add(new SqliteParameter("@manga", $"%{query.MangaContains}%"));
        }

        if (query.After.HasValue)
        {
            whereClauses.Add("read_at_utc >= @after");
            parameters.Add(new SqliteParameter("@after", query.After.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }

        if (query.Before.HasValue)
        {
            whereClauses.Add("read_at_utc <= @before");
            parameters.Add(new SqliteParameter("@before", query.Before.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }

        if (query.FromChapter.HasValue)
        {
            whereClauses.Add("chapter_number >= @fromCh");
            parameters.Add(new SqliteParameter("@fromCh", query.FromChapter.Value));
        }

        if (query.ToChapter.HasValue)
        {
            whereClauses.Add("chapter_number <= @toCh");
            parameters.Add(new SqliteParameter("@toCh", query.ToChapter.Value));
        }

        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT provider, manga_id, manga_title, chapter_number, chapter_title, read_at_utc
            FROM {TableName}
            {whereClause}
            ORDER BY read_at_utc DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", query.Limit);
        foreach (var param in parameters)
        {
            command.Parameters.Add(param);
        }

        var results = new List<ReadHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ReadHistoryStat>> GetStatsAsync(string? mangaFilter, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var whereClause = string.IsNullOrWhiteSpace(mangaFilter)
            ? string.Empty
            : "WHERE manga_title LIKE @filter COLLATE NOCASE";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT manga_title, COUNT(*) as count, MAX(read_at_utc) as last_read
            FROM {TableName}
            {whereClause}
            GROUP BY manga_title
            ORDER BY last_read DESC;
            """;

        if (!string.IsNullOrWhiteSpace(mangaFilter))
        {
            command.Parameters.AddWithValue("@filter", $"%{mangaFilter}%");
        }

        var results = new List<ReadHistoryStat>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var title = reader.GetString(0);
            var count = reader.GetInt32(1);
            var lastReadStr = reader.GetString(2);
            var lastRead = DateTimeOffset.Parse(lastReadStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            results.Add(new ReadHistoryStat(title, count, lastRead));
        }

        return results;
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
                    provider TEXT NOT NULL,
                    manga_id TEXT NOT NULL,
                    manga_title TEXT NOT NULL,
                    chapter_number REAL NOT NULL,
                    chapter_title TEXT,
                    read_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_{TableName}_manga_title ON {TableName}(manga_title COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_{TableName}_read_at ON {TableName}(read_at_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static async Task InsertAsync(SqliteConnection connection, ReadHistoryEntry entry, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName} (provider, manga_id, manga_title, chapter_number, chapter_title, read_at_utc)
            VALUES (@provider, @mangaId, @mangaTitle, @chapterNumber, @chapterTitle, @readAt);
            """;
        command.Parameters.AddWithValue("@provider", entry.Provider);
        command.Parameters.AddWithValue("@mangaId", entry.MangaId);
        command.Parameters.AddWithValue("@mangaTitle", entry.MangaTitle);
        command.Parameters.AddWithValue("@chapterNumber", entry.ChapterNumber);
        command.Parameters.AddWithValue("@chapterTitle", entry.ChapterTitle ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@readAt", entry.ReadAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ReadHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        return new ReadHistoryEntry
        {
            Provider = reader.GetString(0),
            MangaId = reader.GetString(1),
            MangaTitle = reader.GetString(2),
            ChapterNumber = reader.GetFloat(3),
            ChapterTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
            ReadAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
        };
    }
}
