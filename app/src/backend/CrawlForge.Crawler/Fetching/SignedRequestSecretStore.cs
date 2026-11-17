using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Signing material loaded from the protected store: the decrypted secret
/// plus its registered expiry (the locked contract record carries no expiry).
/// </summary>
/// <param name="Secret">The signing secret with decrypted key material.</param>
/// <param name="ExpiresAtUtc">Optional expiry timestamp (UTC).</param>
public sealed record StoredSignedRequest(SignedRequestSecret Secret, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Persists user signing material encrypted at rest (FS-CRAWL-001 §5.10/§9.1,
/// criterion #34): the secret value is protected through
/// <see cref="ISecretProtector"/> (app-generated entropy, authenticated
/// encryption — the phase-00 security runtime) before it touches disk. Only
/// ciphertext, the key identifier, algorithm, header name, and expiry are
/// stored; no plaintext secret is ever written, and nothing is transmitted.
/// </summary>
public sealed class SignedRequestSecretStore
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    private readonly ISecretProtector _protector;
    private readonly string _storageFilePath;

    /// <summary>Creates a protected store backed by a local file.</summary>
    /// <param name="protector">Secret protector (app-entropy authenticated encryption).</param>
    /// <param name="storageFilePath">Local storage file path.</param>
    /// <exception cref="ArgumentNullException">The protector is null.</exception>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    public SignedRequestSecretStore(ISecretProtector protector, string storageFilePath)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        if (string.IsNullOrWhiteSpace(storageFilePath))
        {
            throw new ArgumentException("The storage path must be a non-empty local path.", nameof(storageFilePath));
        }

        _storageFilePath = Path.GetFullPath(storageFilePath);
    }

    /// <summary>Local storage file path.</summary>
    public string StorageFilePath => _storageFilePath;

    /// <summary>Encrypts and persists the signing material (atomic write).</summary>
    /// <param name="secret">Signing secret; its Secret value is encrypted before storage.</param>
    /// <param name="expiresAtUtc">Optional expiry timestamp (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the material is persisted.</returns>
    /// <exception cref="ArgumentNullException">The secret is null.</exception>
    public async Task SaveAsync(SignedRequestSecret secret, DateTimeOffset? expiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();

        var record = new ProtectedRecord(
            secret.KeyId,
            _protector.Protect(secret.Secret),
            secret.Algorithm,
            secret.HeaderName,
            expiresAtUtc);

        string? directory = Path.GetDirectoryName(_storageFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _storageFilePath + "." + Path.GetRandomFileName() + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(record, FileOptions), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _storageFilePath, overwrite: true);
    }

    /// <summary>Loads and decrypts the signing material.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored material, or null when nothing is stored.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The stored ciphertext was tampered with or the key changed.
    /// </exception>
    public async Task<StoredSignedRequest?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_storageFilePath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(_storageFilePath, cancellationToken).ConfigureAwait(false);
        ProtectedRecord? record = JsonSerializer.Deserialize<ProtectedRecord>(json, FileOptions);
        if (record is null)
        {
            return null;
        }

        var secret = new SignedRequestSecret(
            record.KeyId,
            _protector.Unprotect(record.ProtectedSecret),
            record.Algorithm,
            record.HeaderName);
        return new StoredSignedRequest(secret, record.ExpiresAtUtc);
    }

    /// <summary>Deletes the stored signing material.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the material is deleted.</returns>
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_storageFilePath))
        {
            File.Delete(_storageFilePath);
        }

        return Task.CompletedTask;
    }

    private sealed record ProtectedRecord(
        string KeyId,
        string ProtectedSecret,
        string Algorithm,
        string HeaderName,
        DateTimeOffset? ExpiresAtUtc);
}