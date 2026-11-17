using System.Text.Json;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Data.MySql;

/// <summary>
/// Persisted form of the external-database connection settings for the
/// <c>application_settings.database_connection_json</c> payload (schema catalog).
/// Host, port, and database name are plain connection settings; both credential
/// components (user name and password) are stored only in protected form
/// (FS-CRAWL-001 §9.1, mandatory secret protection).
/// </summary>
/// <param name="Host">User-configured server host.</param>
/// <param name="Port">User-configured server port.</param>
/// <param name="Database">User-configured database name.</param>
/// <param name="ProtectedUserId">Encrypted database user name.</param>
/// <param name="ProtectedPassword">Encrypted database password.</param>
public sealed record StoredMySqlSettings(
    string Host,
    int Port,
    string Database,
    string ProtectedUserId,
    string ProtectedPassword);

/// <summary>
/// Encrypts external-database credentials at rest through <see cref="ISecretProtector"/>
/// (FS-CRAWL-001 §9.1). Key material comes exclusively from the app-entropy protector
/// (app-generated random bytes persisted locally) — never from hardware or OS identity
/// (clean-room exclusion row 1). Plaintext credentials exist only in memory while a
/// connection is being opened; nothing protected or plaintext is ever transmitted
/// anywhere except the user-configured server itself.
/// </summary>
public sealed class MySqlCredentialGuard
{
    private readonly ISecretProtector _protector;

    /// <summary>Creates a guard over the given secret protector.</summary>
    /// <param name="protector">App-entropy secret protector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="protector"/> is null.</exception>
    public MySqlCredentialGuard(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
    }

    /// <summary>Protects the credential components of the settings for storage.</summary>
    /// <param name="settings">Plaintext settings held in memory.</param>
    /// <returns>The stored form with encrypted credentials.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The settings are incomplete or out of range.</exception>
    public StoredMySqlSettings Protect(MySqlDatabaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        return new StoredMySqlSettings(
            settings.Host,
            settings.Port,
            settings.Database,
            _protector.Protect(settings.UserId),
            _protector.Protect(settings.Password));
    }

    /// <summary>Restores usable settings from the stored form.</summary>
    /// <param name="stored">Stored form with encrypted credentials.</param>
    /// <returns>Plaintext settings for opening a connection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stored"/> is null.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// A protected credential is malformed, tampered with, or was encrypted under a
    /// different key.
    /// </exception>
    public MySqlDatabaseSettings Restore(StoredMySqlSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return new MySqlDatabaseSettings
        {
            Host = stored.Host,
            Port = stored.Port,
            Database = stored.Database,
            UserId = _protector.Unprotect(stored.ProtectedUserId),
            Password = _protector.Unprotect(stored.ProtectedPassword),
        };
    }

    /// <summary>
    /// Serializes settings into the storage JSON payload with encrypted credentials.
    /// This is the value persisted in <c>application_settings.database_connection_json</c>.
    /// </summary>
    /// <param name="settings">Plaintext settings held in memory.</param>
    /// <returns>The storage JSON payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The settings are incomplete or out of range.</exception>
    public string ToStorageJson(MySqlDatabaseSettings settings) =>
        JsonSerializer.Serialize(Protect(settings));

    /// <summary>Restores settings from a storage JSON payload.</summary>
    /// <param name="storageJson">Storage JSON payload.</param>
    /// <returns>Plaintext settings for opening a connection.</returns>
    /// <exception cref="ArgumentException">The payload is null, empty, or whitespace.</exception>
    /// <exception cref="JsonException">The payload is not valid storage JSON.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// A protected credential is malformed, tampered with, or was encrypted under a
    /// different key.
    /// </exception>
    public MySqlDatabaseSettings FromStorageJson(string storageJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageJson);

        StoredMySqlSettings stored = JsonSerializer.Deserialize<StoredMySqlSettings>(storageJson)
            ?? throw new ArgumentException("The storage payload deserialized to null.", nameof(storageJson));

        return Restore(stored);
    }
}
