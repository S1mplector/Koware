// Author: Ilgaz Mehmetoğlu
// Download tracking persistence with SQLite.
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
/// Type of downloaded content.
/// </summary>
public enum DownloadType
{
    /// <summary>Anime episode.</summary>
    Episode,
    /// <summary>Manga chapter.</summary>
    Chapter
}

/// <summary>
/// Persistent download state.
/// </summary>
public enum DownloadState
{
    Completed,
    Partial,
    Failed
}

/// <summary>
/// Represents a downloaded content entry.
/// </summary>
public sealed class DownloadEntry
{
    public long Id { get; init; }
    public DownloadType Type { get; init; }
    public string ContentId { get; init; } = string.Empty;
    public string ContentTitle { get; init; } = string.Empty;
    public double Number { get; init; }
    public string? Quality { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTimeOffset DownloadedAt { get; init; }
    public DownloadState State { get; init; } = DownloadState.Completed;
    public int CompletedItems { get; init; }
    public int TotalItems { get; init; }
    public bool IsComplete => State == DownloadState.Completed;
    public bool Exists => File.Exists(FilePath) || Directory.Exists(FilePath);
}

/// <summary>
/// Download statistics summary.
/// </summary>
public sealed record DownloadStats(
    int TotalEpisodes,
    int TotalChapters,
    int UniqueAnime,
    int UniqueManga,
    long TotalSizeBytes);

/// <summary>
/// Interface for download tracking persistence.
/// </summary>
public interface IDownloadStore
{
    /// <summary>Record a new download.</summary>
    Task<DownloadEntry> AddAsync(DownloadType type, string contentId, string contentTitle, double number, string? quality, string filePath, long fileSizeBytes, CancellationToken cancellationToken = default);

    /// <summary>Record a new download with explicit completion state and item counts.</summary>
    Task<DownloadEntry> AddAsync(DownloadType type, string contentId, string contentTitle, double number, string? quality, string filePath, long fileSizeBytes, DownloadState state, int completedItems, int totalItems, CancellationToken cancellationToken = default);

    /// <summary>Get all downloads for a specific content (anime/manga).</summary>
    Task<IReadOnlyList<DownloadEntry>> GetForContentAsync(string contentId, CancellationToken cancellationToken = default);

    /// <summary>Get all downloads, optionally filtered by type.</summary>
    Task<IReadOnlyList<DownloadEntry>> GetAllAsync(DownloadType? typeFilter = null, CancellationToken cancellationToken = default);

    /// <summary>Check if a specific episode/chapter is downloaded.</summary>
    Task<DownloadEntry?> GetAsync(string contentId, double number, CancellationToken cancellationToken = default);

    /// <summary>Remove a download entry (e.g., when file is deleted).</summary>
    Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Get aggregated download statistics.</summary>
    Task<DownloadStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Verify downloads and remove entries for missing files.</summary>
    Task<int> CleanupMissingAsync(CancellationToken cancellationToken = default);

    /// <summary>Get downloaded episode/chapter numbers for a content ID.</summary>
    Task<IReadOnlySet<double>> GetDownloadedNumbersAsync(string contentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite-backed download tracking store.
/// </summary>
/// <remarks>
/// Stores downloads in %APPDATA%/koware/history.db (same file as other stores).
/// </remarks>
public sealed class SqliteDownloadStore : IDownloadStore
{
    private const string TableName = "downloads";
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteDownloadStore(IDatabaseConnectionFactory connectionFactory)
        : this(GetDefaultDatabasePath(), connectionFactory)
    {
    }

    public SqliteDownloadStore(string dbPath, IDatabaseConnectionFactory? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        _connectionFactory = connectionFactory ?? new DatabaseConnectionFactory();
        _dbPath = Path.GetFullPath(dbPath);

        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Parameterless constructor for backward compatibility.
    /// </summary>
    public SqliteDownloadStore() : this(new DatabaseConnectionFactory())
    {
    }

    private static string GetDefaultDatabasePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var kowareDir = Path.Combine(baseDir, "koware");
        return Path.Combine(kowareDir, "history.db");
    }

    public Task<DownloadEntry> AddAsync(DownloadType type, string contentId, string contentTitle, double number, string? quality, string filePath, long fileSizeBytes, CancellationToken cancellationToken = default)
    {
        return AddAsync(type, contentId, contentTitle, number, quality, filePath, fileSizeBytes, DownloadState.Completed, 0, 0, cancellationToken);
    }

    public async Task<DownloadEntry> AddAsync(DownloadType type, string contentId, string contentTitle, double number, string? quality, string filePath, long fileSizeBytes, DownloadState state, int completedItems, int totalItems, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        // Upsert: update if same content+number exists
        var upsertSql = $@"
            INSERT INTO {TableName} (type, content_id, content_title, number, quality, file_path, file_size_bytes, downloaded_at, state, completed_items, total_items)
            VALUES (@type, @contentId, @contentTitle, @number, @quality, @filePath, @fileSizeBytes, @downloadedAt, @state, @completedItems, @totalItems)
            ON CONFLICT(content_id, number) DO UPDATE SET
                content_title = excluded.content_title,
                quality = excluded.quality,
                file_path = excluded.file_path,
                file_size_bytes = excluded.file_size_bytes,
                downloaded_at = excluded.downloaded_at,
                state = excluded.state,
                completed_items = excluded.completed_items,
                total_items = excluded.total_items
            RETURNING id";

        await using var cmd = new SqliteCommand(upsertSql, connection);
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@type", type.ToString());
        cmd.Parameters.AddWithValue("@contentId", contentId);
        cmd.Parameters.AddWithValue("@contentTitle", contentTitle);
        cmd.Parameters.AddWithValue("@number", number);
        cmd.Parameters.AddWithValue("@quality", (object?)quality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filePath", filePath);
        cmd.Parameters.AddWithValue("@fileSizeBytes", fileSizeBytes);
        cmd.Parameters.AddWithValue("@downloadedAt", now.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@state", state.ToString());
        cmd.Parameters.AddWithValue("@completedItems", completedItems);
        cmd.Parameters.AddWithValue("@totalItems", totalItems);

        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));

        return new DownloadEntry
        {
            Id = id,
            Type = type,
            ContentId = contentId,
            ContentTitle = contentTitle,
            Number = number,
            Quality = quality,
            FilePath = filePath,
            FileSizeBytes = fileSizeBytes,
            DownloadedAt = now,
            State = state,
            CompletedItems = completedItems,
            TotalItems = totalItems
        };
    }

    public async Task<IReadOnlyList<DownloadEntry>> GetForContentAsync(string contentId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sql = $"SELECT id, type, content_id, content_title, number, quality, file_path, file_size_bytes, downloaded_at, state, completed_items, total_items FROM {TableName} WHERE content_id = @contentId ORDER BY number";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@contentId", contentId);

        var results = new List<DownloadEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<DownloadEntry>> GetAllAsync(DownloadType? typeFilter = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sql = typeFilter.HasValue
            ? $"SELECT id, type, content_id, content_title, number, quality, file_path, file_size_bytes, downloaded_at, state, completed_items, total_items FROM {TableName} WHERE type = @type ORDER BY downloaded_at DESC"
            : $"SELECT id, type, content_id, content_title, number, quality, file_path, file_size_bytes, downloaded_at, state, completed_items, total_items FROM {TableName} ORDER BY downloaded_at DESC";

        await using var cmd = new SqliteCommand(sql, connection);
        if (typeFilter.HasValue)
        {
            cmd.Parameters.AddWithValue("@type", typeFilter.Value.ToString());
        }

        var results = new List<DownloadEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task<DownloadEntry?> GetAsync(string contentId, double number, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sql = $"SELECT id, type, content_id, content_title, number, quality, file_path, file_size_bytes, downloaded_at, state, completed_items, total_items FROM {TableName} WHERE content_id = @contentId AND ABS(number - @number) < 0.0001";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@contentId", contentId);
        cmd.Parameters.AddWithValue("@number", number);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadEntry(reader);
        }

        return null;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sql = $"DELETE FROM {TableName} WHERE id = @id";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id);

        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<DownloadStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sql = $@"
            SELECT 
                SUM(CASE WHEN type = 'Episode' THEN 1 ELSE 0 END) as total_episodes,
                SUM(CASE WHEN type = 'Chapter' THEN 1 ELSE 0 END) as total_chapters,
                COUNT(DISTINCT CASE WHEN type = 'Episode' THEN content_id END) as unique_anime,
                COUNT(DISTINCT CASE WHEN type = 'Chapter' THEN content_id END) as unique_manga,
                SUM(file_size_bytes) as total_size
            FROM {TableName}";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            return new DownloadStats(
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4));
        }

        return new DownloadStats(0, 0, 0, 0, 0);
    }

    public async Task<int> CleanupMissingAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var all = await GetAllAsync(cancellationToken: cancellationToken);
        var removed = 0;

        foreach (var entry in all)
        {
            if (!entry.Exists)
            {
                await RemoveAsync(entry.Id, cancellationToken);
                removed++;
            }
        }

        return removed;
    }

    public async Task<IReadOnlySet<double>> GetDownloadedNumbersAsync(string contentId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

        var sql = $"SELECT number FROM {TableName} WHERE content_id = @contentId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@contentId", contentId);

        var numbers = new HashSet<double>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            numbers.Add(reader.GetDouble(0));
        }

        return numbers;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            await using var connection = await _connectionFactory.OpenConnectionAsync(_dbPath, cancellationToken);

            var createTableSql = $@"
                CREATE TABLE IF NOT EXISTS {TableName} (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    type TEXT NOT NULL,
                    content_id TEXT NOT NULL,
                    content_title TEXT NOT NULL,
                    number REAL NOT NULL,
                    quality TEXT,
                    file_path TEXT NOT NULL,
                    file_size_bytes INTEGER NOT NULL DEFAULT 0,
                    downloaded_at TEXT NOT NULL,
                    state TEXT NOT NULL DEFAULT 'Completed',
                    completed_items INTEGER NOT NULL DEFAULT 0,
                    total_items INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(content_id, number)
                )";

            await using var cmd = new SqliteCommand(createTableSql, connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            await EnsureColumnExistsAsync(connection, "state", "TEXT NOT NULL DEFAULT 'Completed'", cancellationToken);
            await EnsureColumnExistsAsync(connection, "completed_items", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnExistsAsync(connection, "total_items", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

            // Create indexes
            var indexSql = $@"
                CREATE INDEX IF NOT EXISTS idx_{TableName}_content_id ON {TableName}(content_id);
                CREATE INDEX IF NOT EXISTS idx_{TableName}_type ON {TableName}(type);";
            await using var indexCmd = new SqliteCommand(indexSql, connection);
            await indexCmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static DownloadEntry ReadEntry(SqliteDataReader reader)
    {
        return new DownloadEntry
        {
            Id = reader.GetInt64(0),
            Type = Enum.Parse<DownloadType>(reader.GetString(1)),
            ContentId = reader.GetString(2),
            ContentTitle = reader.GetString(3),
            Number = reader.GetDouble(4),
            Quality = reader.IsDBNull(5) ? null : reader.GetString(5),
            FilePath = reader.GetString(6),
            FileSizeBytes = reader.GetInt64(7),
            DownloadedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            State = reader.IsDBNull(9) ? DownloadState.Completed : Enum.Parse<DownloadState>(reader.GetString(9)),
            CompletedItems = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
            TotalItems = reader.IsDBNull(11) ? 0 : reader.GetInt32(11)
        };
    }

    private async Task EnsureColumnExistsAsync(SqliteConnection connection, string columnName, string definition, CancellationToken cancellationToken)
    {
        var existsSql = $"PRAGMA table_info({TableName})";
        await using var existsCmd = new SqliteCommand(existsSql, connection);
        await using var reader = await existsCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.CloseAsync();
        var alterSql = $"ALTER TABLE {TableName} ADD COLUMN {columnName} {definition}";
        await using var alterCmd = new SqliteCommand(alterSql, connection);
        await alterCmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
