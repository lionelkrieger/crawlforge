using System.Security.Cryptography;

namespace CrawlForge.Security.Secrets;

/// <summary>
/// Locally persisted, app-generated random entropy used as key material for
/// secret protection (FS-CRAWL-001 §9.1, mandatory secret protection).
/// </summary>
/// <remarks>
/// <para>
/// The key is 32 random bytes produced by <see cref="RandomNumberGenerator"/>
/// on first use and persisted verbatim to a local file inside the per-user
/// application data area. It is NEVER derived from hardware identifiers, OS
/// registration stores, management instrumentation, or any machine identity
/// (clean-room exclusion row 1): no registry, WMI, machine-ID, or platform
/// I/O-registry read exists anywhere in this type.
/// </para>
/// <para>
/// The store is thread-safe and lazy: the key file is created on first access
/// and reused afterwards, so secrets remain decryptable across launches.
/// Key bytes are never logged, never transmitted, and never leave the machine.
/// </para>
/// </remarks>
public sealed class EntropyKeyStore
{
    /// <summary>Length of the app-generated key in bytes (256-bit).</summary>
    public const int KeyLengthBytes = 32;

    private readonly object _gate = new();
    private byte[]? _key;

    /// <summary>Creates a key store backed by the given local key file.</summary>
    /// <param name="keyFilePath">
    /// Local path of the key file. Created with fresh random entropy when missing;
    /// loaded verbatim when present. The parent directory is created on demand.
    /// </param>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public EntropyKeyStore(string keyFilePath)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
        {
            throw new ArgumentException("Key file path must be a non-empty local path.", nameof(keyFilePath));
        }

        KeyFilePath = Path.GetFullPath(keyFilePath);
    }

    /// <summary>Absolute local path of the persisted entropy key file.</summary>
    public string KeyFilePath { get; }

    /// <summary>
    /// Returns the 32-byte app-entropy key, generating and persisting it on first
    /// use. The returned array is a defensive copy; callers must never log or
    /// transmit it.
    /// </summary>
    /// <returns>A copy of the key bytes.</returns>
    /// <exception cref="CryptographicException">
    /// The persisted key file exists but does not contain exactly
    /// <see cref="KeyLengthBytes"/> bytes.
    /// </exception>
    internal byte[] GetKey()
    {
        lock (_gate)
        {
            if (_key is null)
            {
                _key = LoadOrCreateKey();
            }

            return (byte[])_key.Clone();
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(KeyFilePath))
        {
            byte[] existing = File.ReadAllBytes(KeyFilePath);
            if (existing.Length != KeyLengthBytes)
            {
                throw new CryptographicException(
                    $"Entropy key file '{KeyFilePath}' has an invalid length ({existing.Length} bytes; expected {KeyLengthBytes}).");
            }

            return existing;
        }

        byte[] fresh = RandomNumberGenerator.GetBytes(KeyLengthBytes);
        PersistKey(fresh);
        return fresh;
    }

    private void PersistKey(byte[] key)
    {
        string? directory = Path.GetDirectoryName(KeyFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic publish: write a sibling temp file, then move it into place so a
        // crash mid-write can never leave a truncated key file behind.
        string tempPath = KeyFilePath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, key);
            File.Move(tempPath, KeyFilePath, overwrite: false);
        }
        catch (IOException)
        {
            // A concurrent first-use creator won the race; adopt the persisted key.
            TryDelete(tempPath);
            if (File.Exists(KeyFilePath))
            {
                byte[] existing = File.ReadAllBytes(KeyFilePath);
                if (existing.Length == KeyLengthBytes)
                {
                    Array.Copy(existing, key, KeyLengthBytes);
                    return;
                }
            }

            throw new CryptographicException($"Failed to persist the entropy key file '{KeyFilePath}'.");
        }
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
        catch (IOException)
        {
            // Best-effort cleanup of a transient temp file; never mask the original flow.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a transient temp file; never mask the original flow.
        }
    }
}