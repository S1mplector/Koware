using Koware.Cli.Commands;
using Koware.Cli.Configuration;
using Koware.Cli.History;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Koware.Tests;

public class LastCommandTests
{
    [Fact]
    public async Task ExecuteAsync_PlayFlag_RelaunchesWatchCommand()
    {
        var history = new StubWatchHistoryStore(new WatchHistoryEntry
        {
            AnimeTitle = "Bleach",
            EpisodeNumber = 2,
            Quality = "720p"
        });
        var launcher = new RecordingLauncher(exitCode: 7);
        var services = new ServiceCollection()
            .AddSingleton<IWatchHistoryStore>(history)
            .AddSingleton<IKowareSubprocessLauncher>(launcher)
            .BuildServiceProvider();
        var context = new CommandContext(
            services,
            NullLogger.Instance,
            new DefaultCliOptions { Mode = "anime" },
            CancellationToken.None);

        var command = new LastCommand();
        var exitCode = await command.ExecuteAsync(["last", "--play"], context);

        Assert.Equal(7, exitCode);
        Assert.Equal(
            ["watch", "Bleach", "--episode", "2", "--non-interactive", "--quality", "720p"],
            launcher.LastArgs);
    }

    private sealed class RecordingLauncher : IKowareSubprocessLauncher
    {
        private readonly int _exitCode;

        public RecordingLauncher(int exitCode)
        {
            _exitCode = exitCode;
        }

        public IReadOnlyList<string> LastArgs { get; private set; } = Array.Empty<string>();

        public Task<int?> TryRunAsync(
            IReadOnlyList<string> commandArgs,
            Microsoft.Extensions.Logging.ILogger logger,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string?>? environmentOverrides = null)
        {
            LastArgs = commandArgs.ToArray();
            return Task.FromResult<int?>(_exitCode);
        }
    }

    private sealed class StubWatchHistoryStore : IWatchHistoryStore
    {
        private readonly WatchHistoryEntry? _entry;

        public StubWatchHistoryStore(WatchHistoryEntry? entry)
        {
            _entry = entry;
        }

        public Task AddAsync(WatchHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WatchHistoryEntry?> GetLastAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entry);
        }

        public Task<WatchHistoryEntry?> GetLastForAnimeAsync(string animeTitle, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WatchHistoryEntry?> SearchLastAsync(string query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<WatchHistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<HistoryStat>> GetStatsAsync(string? animeFilter, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> ClearAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> ClearForAnimeAsync(string animeTitle, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
