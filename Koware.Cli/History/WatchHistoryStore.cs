using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
}

public sealed class FileWatchHistoryStore : IWatchHistoryStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileWatchHistoryStore()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.CurrentDirectory;
        }

        var dir = Path.Combine(baseDir, "koware");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "history.json");
    }

    public async Task AddAsync(WatchHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var entries = await LoadAllAsync(cancellationToken);
        entries.Add(entry);
        await SaveAllAsync(entries, cancellationToken);
    }

    public async Task<WatchHistoryEntry?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        var entries = await LoadAllAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return null;
        }

        return entries.OrderByDescending(e => e.WatchedAt).FirstOrDefault();
    }

    private async Task<List<WatchHistoryEntry>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<WatchHistoryEntry>();
        }

        await using var stream = File.OpenRead(_filePath);
        var entries = await JsonSerializer.DeserializeAsync<List<WatchHistoryEntry>>(stream, _serializerOptions, cancellationToken);
        return entries ?? new List<WatchHistoryEntry>();
    }

    private async Task SaveAllAsync(List<WatchHistoryEntry> entries, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, entries, _serializerOptions, cancellationToken);
    }
}
