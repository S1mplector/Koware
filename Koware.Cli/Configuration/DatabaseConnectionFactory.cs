// Author: Ilgaz Mehmetoğlu
// Factory for creating SQLite database connections with optional encryption support.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Koware.Cli.Configuration;

/// <summary>
/// Factory for creating SQLite database connections with optional encryption.
/// </summary>
public interface IDatabaseConnectionFactory
{
    /// <summary>
    /// Creates a connection string for the specified database file.
    /// </summary>
    /// <param name="databasePath">Full path to the database file.</param>
    /// <returns>A connection string, optionally with encryption parameters.</returns>
    string CreateConnectionString(string databasePath);

    /// <summary>
    /// Opens a new connection to the specified database.
    /// </summary>
    /// <param name="databasePath">Full path to the database file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open SQLite connection.</returns>
    Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether encryption is currently enabled.
    /// </summary>
    bool IsEncryptionEnabled { get; }

    /// <summary>
    /// Encrypts an existing unencrypted database.
    /// </summary>
    /// <param name="databasePath">Path to the database to encrypt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EncryptDatabaseAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts an existing encrypted database.
    /// </summary>
    /// <param name="databasePath">Path to the database to decrypt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DecryptDatabaseAsync(string databasePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a database file is encrypted.
    /// </summary>
    /// <param name="databasePath">Path to the database file.</param>
    /// <returns>True if the database appears to be encrypted.</returns>
    bool IsDatabaseEncrypted(string databasePath);
}

/// <summary>
/// SQLite connection factory with SQLCipher encryption support.
/// </summary>
public sealed class DatabaseConnectionFactory : IDatabaseConnectionFactory
{
    private readonly DatabaseOptions _options;
    private readonly string? _derivedKey;

    public DatabaseConnectionFactory(IOptions<DatabaseOptions> options)
    {
        _options = options.Value;
        
        if (_options.EnableEncryption && string.IsNullOrWhiteSpace(_options.EncryptionKey) && _options.UseMachineDerivedKey)
        {
            _derivedKey = DeriveKeyFromMachine();
        }
    }

    /// <summary>
    /// Parameterless constructor for backward compatibility when DI is not available.
    /// </summary>
    public DatabaseConnectionFactory() : this(Options.Create(new DatabaseOptions()))
    {
    }

    public bool IsEncryptionEnabled => _options.EnableEncryption;

    public string CreateConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        if (_options.EnableEncryption)
        {
            var key = GetEffectiveKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                builder.Password = key;
            }
        }

        return builder.ToString();
    }

    public async Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var connectionString = CreateConnectionString(databasePath);
        var connection = new SqliteConnection(connectionString);
        
        await connection.OpenAsync(cancellationToken);

        if (_options.EnableEncryption && !string.IsNullOrWhiteSpace(_options.CipherSettings))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = _options.CipherSettings;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }

    public async Task EncryptDatabaseAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        var key = GetEffectiveKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Cannot encrypt database: no encryption key configured.");
        }

        var tempPath = databasePath + ".encrypting";
        
        try
        {
            // Open unencrypted database
            var unencryptedConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var sourceConn = new SqliteConnection(unencryptedConnStr);
            await sourceConn.OpenAsync(cancellationToken);

            // Create encrypted database
            var encryptedConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Password = key,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            await using var destConn = new SqliteConnection(encryptedConnStr);
            await destConn.OpenAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.CipherSettings))
            {
                await using var cmd = destConn.CreateCommand();
                cmd.CommandText = _options.CipherSettings;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Export from source and import to destination
            await using (var exportCmd = sourceConn.CreateCommand())
            {
                exportCmd.CommandText = $"ATTACH DATABASE '{tempPath}' AS encrypted KEY '{EscapeSqlString(key)}';";
                await exportCmd.ExecuteNonQueryAsync(cancellationToken);
                
                exportCmd.CommandText = "SELECT sqlcipher_export('encrypted');";
                await exportCmd.ExecuteNonQueryAsync(cancellationToken);
                
                exportCmd.CommandText = "DETACH DATABASE encrypted;";
                await exportCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Close connections before file operations
            await sourceConn.CloseAsync();
            await destConn.CloseAsync();

            // Replace original with encrypted version
            var backupPath = databasePath + ".unencrypted.bak";
            File.Move(databasePath, backupPath, overwrite: true);
            File.Move(tempPath, databasePath, overwrite: true);
        }
        catch
        {
            // Cleanup temp file on failure
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }
    }

    public async Task DecryptDatabaseAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        var key = GetEffectiveKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Cannot decrypt database: no encryption key configured.");
        }

        var tempPath = databasePath + ".decrypting";

        try
        {
            // Open encrypted database
            var encryptedConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Password = key,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var sourceConn = new SqliteConnection(encryptedConnStr);
            await sourceConn.OpenAsync(cancellationToken);

            // Export to unencrypted database
            await using (var cmd = sourceConn.CreateCommand())
            {
                cmd.CommandText = $"ATTACH DATABASE '{tempPath}' AS plaintext KEY '';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                
                cmd.CommandText = "SELECT sqlcipher_export('plaintext');";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                
                cmd.CommandText = "DETACH DATABASE plaintext;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await sourceConn.CloseAsync();

            // Replace original with decrypted version
            var backupPath = databasePath + ".encrypted.bak";
            File.Move(databasePath, backupPath, overwrite: true);
            File.Move(tempPath, databasePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }
    }

    public bool IsDatabaseEncrypted(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            // Read first 16 bytes - SQLite header starts with "SQLite format 3\0"
            var header = new byte[16];
            using var fs = File.OpenRead(databasePath);
            if (fs.Read(header, 0, 16) < 16)
            {
                return false;
            }

            // Unencrypted SQLite databases start with this signature
            var sqliteHeader = "SQLite format 3\0"u8;
            return !header.AsSpan().StartsWith(sqliteHeader);
        }
        catch
        {
            return false;
        }
    }

    private string? GetEffectiveKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.EncryptionKey))
        {
            return _options.EncryptionKey;
        }

        return _derivedKey;
    }

    private static string DeriveKeyFromMachine()
    {
        // Derive a key from machine-specific data for transparent encryption
        var machineData = new StringBuilder();
        
        // Use machine name and user name as entropy sources
        machineData.Append(Environment.MachineName);
        machineData.Append(Environment.UserName);
        
        // Add a static salt specific to Koware
        machineData.Append("Koware-DB-Encryption-v1");

        // Hash the combined data to create a key
        var dataBytes = Encoding.UTF8.GetBytes(machineData.ToString());
        var hashBytes = SHA256.HashData(dataBytes);
        
        // Convert to base64 for use as password
        return Convert.ToBase64String(hashBytes);
    }

    private static string EscapeSqlString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
