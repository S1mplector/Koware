using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Koware.Cli.Configuration;
using Microsoft.Data.Sqlite;

namespace Koware.Tests;

internal sealed class TestDatabaseConnectionFactory : IDatabaseConnectionFactory, IDisposable
{
    public TestDatabaseConnectionFactory()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"koware-tests-{Guid.NewGuid():N}.db");
    }

    public string DatabasePath { get; }

    public bool IsEncryptionEnabled => false;

    public string CreateConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        return builder.ToString();
    }

    public async Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(CreateConnectionString(DatabasePath));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public Task EncryptDatabaseAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task DecryptDatabaseAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public bool IsDatabaseEncrypted(string databasePath) => false;

    public void Dispose()
    {
        TryDelete(DatabasePath);
        TryDelete($"{DatabasePath}-shm");
        TryDelete($"{DatabasePath}-wal");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
