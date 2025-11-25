// Author: Ilgaz Mehmetoğlu
// Summary: SQLite-backed watch history store with legacy JSON migration and query helpers.
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Koware.Cli.History;

public sealed class WatchHistoryEntry
{
    public string Provider { get; init; } = string.Empty;

    public string AnimeId { get; init; } = string.Empty;

    public string AnimeTitle { get; init; } = string.Empty;

    public int EpisodeNumber { get; init; }

    public string? EpisodeTitle { get; init; }

    public string? Quality { get; init; }

    public DateTimeOffset WatchedAt { get; init; }
}

public interface IWatchHistoryStore
{
    Task AddAsync(WatchHistoryEntry entry, CancellationToken cancellationToken = default);

    Task<WatchHistoryEntry?> GetLastAsync(CancellationToken cancellationToken = default);

    Task<WatchHistoryEntry?> GetLastForAnimeAsync(string animeTitle, CancellationToken cancellationToken = default);

    Task<WatchHistoryEntry?> SearchLastAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchHistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryStat>> GetStatsAsync(string? animeFilter, CancellationToken cancellationToken = default);
}

public sealed record HistoryQuery(
    string? AnimeContains,
    DateTimeOffset? After,
    DateTimeOffset? Before,
    int? FromEpisode,
    int? ToEpisode,
    int Limit);

public sealed record HistoryStat(
    string AnimeTitle,
    int Count,
    DateTimeOffset LastWatched);

public sealed class SqliteWatchHistoryStore : IWatchHistoryStore
{
    private const string TableName = "watch_history";
    private readonly string _connectionString;
    private readonly string _legacyFilePath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly JsonSerializerOptions _legacySerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private bool _initialized;

    public SqliteWatchHistoryStore()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.CurrentDirectory;
        }

        var dir = Path.Combine(baseDir, "koware");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        _legacyFilePath = Path.Combine(dir, "history.json");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public async Task AddAsync(WatchHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await InsertAsync(connection, entry, null, cancellationToken);
    }

    public async Task<WatchHistoryEntry?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT provider, anime_id, anime_title, episode_number, episode_title, quality, watched_at_utc
            FROM {TableName}
            ORDER BY watched_at_utc DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchHistoryEntry
        {
            Provider = reader.GetString(0),
            AnimeId = reader.GetString(1),
            AnimeTitle = reader.GetString(2),
            EpisodeNumber = reader.GetInt32(3),
            EpisodeTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
            Quality = reader.IsDBNull(5) ? null : reader.GetString(5),
            WatchedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<WatchHistoryEntry?> GetLastForAnimeAsync(string animeTitle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(animeTitle))
        {
            throw new ArgumentException("Anime title is required", nameof(animeTitle));
        }

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider, anime_id, anime_title, episode_number, episode_title, quality, watched_at_utc
            FROM watch_history
            WHERE anime_title = $animeTitle COLLATE NOCASE
            ORDER BY watched_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$animeTitle", animeTitle);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchHistoryEntry
        {
            Provider = reader.GetString(0),
            AnimeId = reader.GetString(1),
            AnimeTitle = reader.GetString(2),
            EpisodeNumber = reader.GetInt32(3),
            EpisodeTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
            Quality = reader.IsDBNull(5) ? null : reader.GetString(5),
            WatchedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<WatchHistoryEntry?> SearchLastAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required", nameof(query));
        }

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider, anime_id, anime_title, episode_number, episode_title, quality, watched_at_utc
            FROM watch_history
            WHERE anime_title LIKE $pattern ESCAPE '\' COLLATE NOCASE OR anime_id = $exactId
            ORDER BY watched_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$exactId", query);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WatchHistoryEntry
        {
            Provider = reader.GetString(0),
            AnimeId = reader.GetString(1),
            AnimeTitle = reader.GetString(2),
            EpisodeNumber = reader.GetInt32(3),
            EpisodeTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
            Quality = reader.IsDBNull(5) ? null : reader.GetString(5),
            WatchedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<IReadOnlyList<WatchHistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var where = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(query.AnimeContains))
        {
            where.Add("(anime_title LIKE $title ESCAPE '\\' COLLATE NOCASE OR anime_id = $exactId)");
            parameters.Add(new SqliteParameter("$title", $"%{EscapeLike(query.AnimeContains)}%"));
            parameters.Add(new SqliteParameter("$exactId", query.AnimeContains));
        }

        if (query.After.HasValue)
        {
            where.Add("watched_at_utc >= $after");
            parameters.Add(new SqliteParameter("$after", query.After.Value.UtcDateTime.ToString("O")));
        }

        if (query.Before.HasValue)
        {
            where.Add("watched_at_utc <= $before");
            parameters.Add(new SqliteParameter("$before", query.Before.Value.UtcDateTime.ToString("O")));
        }

        if (query.FromEpisode.HasValue)
        {
            where.Add("episode_number >= $fromEp");
            parameters.Add(new SqliteParameter("$fromEp", query.FromEpisode.Value));
        }

        if (query.ToEpisode.HasValue)
        {
            where.Add("episode_number <= $toEp");
            parameters.Add(new SqliteParameter("$toEp", query.ToEpisode.Value));
        }

        var whereClause = where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
        var limit = query.Limit <= 0 ? 10 : query.Limit;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT provider, anime_id, anime_title, episode_number, episode_title, quality, watched_at_utc
            FROM {TableName}
            {whereClause}
            ORDER BY watched_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.Add(new SqliteParameter("$limit", limit));
        foreach (var p in parameters)
        {
            command.Parameters.Add(p);
        }

        var results = new List<WatchHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new WatchHistoryEntry
            {
                Provider = reader.GetString(0),
                AnimeId = reader.GetString(1),
                AnimeTitle = reader.GetString(2),
                EpisodeNumber = reader.GetInt32(3),
                EpisodeTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
                Quality = reader.IsDBNull(5) ? null : reader.GetString(5),
                WatchedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<HistoryStat>> GetStatsAsync(string? animeFilter, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var where = string.Empty;
        var parameters = new List<SqliteParameter>();
        if (!string.IsNullOrWhiteSpace(animeFilter))
        {
            where = "WHERE anime_title LIKE $title ESCAPE '\\' COLLATE NOCASE OR anime_id = $exactId";
            parameters.Add(new SqliteParameter("$title", $"%{EscapeLike(animeFilter)}%"));
            parameters.Add(new SqliteParameter("$exactId", animeFilter));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT anime_title, COUNT(*) as count, MAX(watched_at_utc) as last
            FROM {TableName}
            {where}
            GROUP BY anime_title
            ORDER BY count DESC, last DESC;
            """;
        foreach (var p in parameters)
        {
            command.Parameters.Add(p);
        }

        var stats = new List<HistoryStat>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stats.Add(new HistoryStat(
                reader.GetString(0),
                reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            ));
        }

        return stats;
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

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS {TableName} (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        provider TEXT NOT NULL,
                        anime_id TEXT NOT NULL,
                        anime_title TEXT NOT NULL,
                        episode_number INTEGER NOT NULL,
                        episode_title TEXT NULL,
                        quality TEXT NULL,
                        watched_at_utc TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_history_title_time ON {TableName} (anime_title COLLATE NOCASE, watched_at_utc DESC);
                """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await MigrateLegacyFileAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task InsertAsync(SqliteConnection connection, WatchHistoryEntry entry, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = $"""
            INSERT INTO {TableName} (provider, anime_id, anime_title, episode_number, episode_title, quality, watched_at_utc)
            VALUES ($provider, $animeId, $animeTitle, $episodeNumber, $episodeTitle, $quality, $watchedAt);
            """;

        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$animeId", entry.AnimeId);
        command.Parameters.AddWithValue("$animeTitle", entry.AnimeTitle);
        command.Parameters.AddWithValue("$episodeNumber", entry.EpisodeNumber);
        command.Parameters.AddWithValue("$episodeTitle", (object?)entry.EpisodeTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$quality", (object?)entry.Quality ?? DBNull.Value);
        command.Parameters.AddWithValue("$watchedAt", entry.WatchedAt.UtcDateTime.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MigrateLegacyFileAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!File.Exists(_legacyFilePath))
        {
            return;
        }

        List<WatchHistoryEntry>? entries;
        try
        {
            await using var stream = File.OpenRead(_legacyFilePath);
            entries = await JsonSerializer.DeserializeAsync<List<WatchHistoryEntry>>(stream, _legacySerializerOptions, cancellationToken);
        }
        catch
        {
            return;
        }

        if (entries is null || entries.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in entries)
        {
            await InsertAsync(connection, entry, transaction, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        try
        {
            var backupPath = Path.ChangeExtension(_legacyFilePath, ".bak");
            File.Move(_legacyFilePath, backupPath, overwrite: true);
        }
        catch
        {
            // Best-effort migration; ignore if backup fails.
        }
    }

    private static string EscapeLike(string input)
    {
        return input
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
