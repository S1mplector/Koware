// Author: Ilgaz Mehmetoğlu
// Configuration options for database encryption and storage settings.
namespace Koware.Cli.Configuration;

/// <summary>
/// Configuration options for database storage.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// Whether to enable database encryption using SQLCipher.
    /// When enabled, all SQLite databases will be encrypted at rest.
    /// </summary>
    public bool EnableEncryption { get; set; } = false;

    /// <summary>
    /// The encryption key for the database. Required when EnableEncryption is true.
    /// If not provided and encryption is enabled, a key will be derived from machine-specific data.
    /// For maximum security, set this to a strong, unique passphrase.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>
    /// Whether to use a machine-derived key when no explicit key is provided.
    /// This provides transparent encryption without user-managed keys.
    /// Default: true
    /// </summary>
    public bool UseMachineDerivedKey { get; set; } = true;

    /// <summary>
    /// SQLCipher cipher settings. Default uses SQLCipher 4 defaults.
    /// </summary>
    public string CipherSettings { get; set; } = "PRAGMA cipher_compatibility = 4;";
}
