using System.Security.Cryptography;
using System.Text;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Security.Secrets;

/// <summary>
/// Authenticated encryption at rest for every credential class (FS-CRAWL-001
/// §9.1): site credentials, destination credentials, proxy credentials,
/// external-database credentials, and local automation tokens/signing secrets
/// (acceptance criterion #17).
/// </summary>
/// <remarks>
/// <para>
/// Scheme <c>cfsec1</c>: AES-256-GCM with a fresh 12-byte random nonce per
/// operation and a 16-byte authentication tag. The protected representation is
/// <c>cfsec1:</c> followed by Base64 of <c>nonce ‖ tag ‖ ciphertext</c>. The
/// scheme prefix is authenticated as additional data, so version or scheme
/// tampering is detected. Any modification of the protected text — ciphertext,
/// tag, nonce, or prefix — fails decryption with
/// <see cref="CryptographicException"/>; no plaintext is ever returned for a
/// tampered payload.
/// </para>
/// <para>
/// Key material comes exclusively from <see cref="EntropyKeyStore"/>:
/// app-generated random bytes persisted locally. Nothing is derived from
/// hardware or OS identity (clean-room exclusion row 1), and no protected or
/// plaintext material is ever transmitted (network boundary, §2.6).
/// </para>
/// </remarks>
public sealed class AppEntropySecretProtector : ISecretProtector
{
    /// <summary>Prefix identifying the cfsec1 protection scheme.</summary>
    public const string SchemePrefix = "cfsec1:";

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>AES-GCM authentication tag length in bytes.</summary>
    public const int TagSizeBytes = 16;

    private static readonly byte[] AdditionalData = Encoding.UTF8.GetBytes(SchemePrefix);

    private readonly EntropyKeyStore _keyStore;

    /// <summary>Creates a protector keyed by the given local app-entropy store.</summary>
    /// <param name="keyStore">Local app-generated entropy key store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keyStore"/> is null.</exception>
    public AppEntropySecretProtector(EntropyKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="plaintext"/> is null.</exception>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] key = _keyStore.GetKey();
        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            byte[] ciphertext = new byte[plainBytes.Length];
            byte[] tag = new byte[TagSizeBytes];

            using (var aes = new AesGcm(key, TagSizeBytes))
            {
                aes.Encrypt(nonce, plainBytes, ciphertext, tag, AdditionalData);
            }

            byte[] envelope = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, envelope, 0, NonceSizeBytes);
            Buffer.BlockCopy(tag, 0, envelope, NonceSizeBytes, TagSizeBytes);
            Buffer.BlockCopy(ciphertext, 0, envelope, NonceSizeBytes + TagSizeBytes, ciphertext.Length);

            return SchemePrefix + Convert.ToBase64String(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="protectedText"/> is null.</exception>
    /// <exception cref="CryptographicException">
    /// The payload is malformed, uses an unknown scheme, or fails authentication
    /// (tampered ciphertext, tag, nonce, or prefix; or a different key).
    /// </exception>
    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        if (!protectedText.StartsWith(SchemePrefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Protected secret does not use the cfsec1 scheme.");
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(protectedText.Substring(SchemePrefix.Length));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Protected secret is not valid Base64.", ex);
        }

        if (envelope.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Protected secret is truncated.");
        }

        byte[] key = _keyStore.GetKey();
        try
        {
            byte[] nonce = new byte[NonceSizeBytes];
            byte[] tag = new byte[TagSizeBytes];
            int ciphertextLength = envelope.Length - NonceSizeBytes - TagSizeBytes;
            byte[] ciphertext = new byte[ciphertextLength];
            Buffer.BlockCopy(envelope, 0, nonce, 0, NonceSizeBytes);
            Buffer.BlockCopy(envelope, NonceSizeBytes, tag, 0, TagSizeBytes);
            Buffer.BlockCopy(envelope, NonceSizeBytes + TagSizeBytes, ciphertext, 0, ciphertextLength);

            byte[] plainBytes = new byte[ciphertextLength];
            using (var aes = new AesGcm(key, TagSizeBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plainBytes, AdditionalData);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}