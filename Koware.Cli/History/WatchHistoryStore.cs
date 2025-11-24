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
}

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
        command.CommandText = $
            """
            SELECT provider, anime_id, anime_title, episode_number, episode_title, quality, watched_at_utc
            FROM {TableName}
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
}
