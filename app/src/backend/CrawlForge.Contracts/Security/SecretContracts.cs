namespace CrawlForge.Contracts.Security;

/// <summary>
/// Encrypts secrets at rest (FS-CRAWL-001 §9.1, mandatory): site credentials,
/// destination credentials, external-database credentials, proxy credentials,
/// and local tokens/signing secrets. Key material is app-generated random
/// entropy persisted locally — never derived from hardware or OS identity
/// (exclusion row 1).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a plaintext secret for storage.</summary>
    /// <param name="plaintext">Plaintext secret.</param>
    /// <returns>Protected representation safe for persistence.</returns>
    string Protect(string plaintext);

    /// <summary>Decrypts a protected secret.</summary>
    /// <param name="protectedText">Protected representation.</param>
    /// <returns>The plaintext secret.</returns>
    string Unprotect(string protectedText);
}
