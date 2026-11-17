using System.Security.Cryptography;
using System.Text;
using CrawlForge.Security.Secrets;
using CrawlForge.Security.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Security.Tests.Secrets;

/// <summary>
/// Acceptance criterion #17: all five credential classes encrypted at rest with
/// authenticated encryption, app-generated random entropy key material (never
/// hardware/OS-derived), tamper detection, and no plaintext at rest.
/// </summary>
public sealed class SecretProtectionTests
{
    /// <summary>The five mandatory credential classes (FS-CRAWL-001 §9.1).</summary>
    public static TheoryData<string, string> CredentialClasses => new()
    {
        { "site", "site-user:Passw0rd!äöü" },
        { "destination", "ftp://dest-user:s3cret@files.example.org/inbox" },
        { "proxy", "proxy-user:P@ssw0rd#2026" },
        { "external-database", "Server=db.internal;User Id=crawlforge;Password=Sup3r$ecret;" },
        { "automation-token", "cf_tok_9f8e7d6c5b4a3210fedcba9876543210" },
    };

    [Theory]
    [MemberData(nameof(CredentialClasses))]
    public void Protect_Unprotect_RoundTrips_EveryCredentialClass(string credentialClass, string secret)
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);

        string protectedText = protector.Protect(secret);

        Assert.StartsWith(AppEntropySecretProtector.SchemePrefix, protectedText, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(protectedText));
        Assert.False(string.IsNullOrEmpty(credentialClass));
    }

    [Theory]
    [InlineData("")]
    [InlineData("密码🔐-unicode-secret")]
    [InlineData("line1\nline2\twith \"quotes\" and \\backslashes")]
    public void Protect_Unprotect_RoundTrips_EdgeCaseSecrets(string secret)
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);

        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
    }

    [Fact]
    public void Protect_Unprotect_RoundTrips_LargeSecret()
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);
        string secret = new('x', 100_000);

        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
    }

    [Fact]
    public void Protect_SamePlaintext_ProducesDistinctCiphertexts()
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);
        const string secret = "repeatable-secret";

        string first = protector.Protect(secret);
        string second = protector.Protect(secret);

        Assert.NotEqual(first, second);
        Assert.Equal(secret, protector.Unprotect(first));
        Assert.Equal(secret, protector.Unprotect(second));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("tag")]
    [InlineData("ciphertext")]
    [InlineData("prefix")]
    [InlineData("truncated")]
    [InlineData("bad-base64")]
    public void Unprotect_TamperedPayload_ThrowsCryptographicException(string mutation)
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);
        string protectedText = protector.Protect("tamper-detection-secret-0123456789");

        string tampered = Mutate(protectedText, mutation);

        // AuthenticationTagMismatchException derives from CryptographicException.
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_WithDifferentAppEntropyKey_Throws()
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protectorA = CreateProtector(temp, "key-a.bin");
        AppEntropySecretProtector protectorB = CreateProtector(temp, "key-b.bin");
        string protectedText = protectorA.Protect("cross-key-secret");

        Assert.ThrowsAny<CryptographicException>(() => protectorB.Unprotect(protectedText));
    }

    [Fact]
    public void Unprotect_WithCopiedLocalKeyFile_Succeeds_ProvingLocalKeyProvenance()
    {
        using var temp = new TempDirectory();
        var storeA = new EntropyKeyStore(Path.Combine(temp.RootPath, "a", "key.bin"));
        var protectorA = new AppEntropySecretProtector(storeA);
        string protectedText = protectorA.Protect("provenance-secret");

        // The ONLY key material is the local app-entropy file: copying it is
        // sufficient to decrypt; nothing else (hardware/OS identity) is involved.
        string copiedKeyPath = Path.Combine(temp.RootPath, "b", "key.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(copiedKeyPath)!);
        File.Copy(storeA.KeyFilePath, copiedKeyPath);
        var protectorCopy = new AppEntropySecretProtector(new EntropyKeyStore(copiedKeyPath));

        Assert.Equal("provenance-secret", protectorCopy.Unprotect(protectedText));
    }

    [Fact]
    public void KeyFile_IsAppGeneratedRandomEntropy_PersistedLocally()
    {
        using var temp = new TempDirectory();
        var storeA = new EntropyKeyStore(Path.Combine(temp.RootPath, "a", "key.bin"));
        var storeB = new EntropyKeyStore(Path.Combine(temp.RootPath, "b", "key.bin"));
        var protectorA = new AppEntropySecretProtector(storeA);
        var protectorB = new AppEntropySecretProtector(storeB);

        // First use generates and persists the key.
        protectorA.Protect("trigger-a");
        protectorB.Protect("trigger-b");

        byte[] keyA = File.ReadAllBytes(storeA.KeyFilePath);
        byte[] keyB = File.ReadAllBytes(storeB.KeyFilePath);

        Assert.Equal(EntropyKeyStore.KeyLengthBytes, keyA.Length);
        Assert.Equal(EntropyKeyStore.KeyLengthBytes, keyB.Length);
        Assert.False(keyA.All(b => b == 0));
        Assert.False(keyB.All(b => b == 0));

        // Random per installation: two fresh stores never derive the same key
        // (a hardware/OS-derived scheme would be deterministic per machine).
        Assert.NotEqual(keyA, keyB);

        // Persisted locally: a new store instance over the same file reuses it.
        var reopened = new AppEntropySecretProtector(new EntropyKeyStore(storeA.KeyFilePath));
        Assert.Equal("trigger-a", reopened.Unprotect(protectorA.Protect("trigger-a")));
        Assert.Equal(keyA, File.ReadAllBytes(storeA.KeyFilePath));
    }

    [Fact]
    public void ProtectedText_KeyFile_AndStorageDirectory_ContainNoPlaintext()
    {
        using var temp = new TempDirectory();
        var store = new EntropyKeyStore(Path.Combine(temp.RootPath, "key.bin"));
        var protector = new AppEntropySecretProtector(store);
        const string secret = "AtRest-Plaintext-Check-Sup3r$ecret";

        string protectedText = protector.Protect(secret);

        Assert.DoesNotContain(secret, protectedText, StringComparison.Ordinal);

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] payload = Convert.FromBase64String(
            protectedText.Substring(AppEntropySecretProtector.SchemePrefix.Length));
        Assert.False(ContainsSubsequence(payload, secretBytes));
        Assert.False(ContainsSubsequence(File.ReadAllBytes(store.KeyFilePath), secretBytes));

        // Sweep every persisted file: no plaintext anywhere at rest.
        foreach (string file in Directory.GetFiles(temp.RootPath, "*", SearchOption.AllDirectories))
        {
            Assert.False(ContainsSubsequence(File.ReadAllBytes(file), secretBytes), $"Plaintext found at rest in '{file}'.");
        }
    }

    [Fact]
    public void Protect_NullPlaintext_Throws()
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);

        Assert.Throws<ArgumentNullException>(() => protector.Protect(null!));
    }

    [Fact]
    public void Unprotect_NullPayload_Throws()
    {
        using var temp = new TempDirectory();
        AppEntropySecretProtector protector = CreateProtector(temp);

        Assert.Throws<ArgumentNullException>(() => protector.Unprotect(null!));
    }

    [Fact]
    public void Constructor_NullKeyStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AppEntropySecretProtector(null!));
    }

    [Fact]
    public void KeyStore_CorruptKeyLength_ThrowsCryptographicException()
    {
        using var temp = new TempDirectory();
        string keyPath = Path.Combine(temp.RootPath, "key.bin");
        File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3, 4, 5 });
        var protector = new AppEntropySecretProtector(new EntropyKeyStore(keyPath));

        Assert.Throws<CryptographicException>(() => protector.Protect("any"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void KeyStore_EmptyPath_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => new EntropyKeyStore(path));
    }

    private static AppEntropySecretProtector CreateProtector(TempDirectory temp, string name = "key.bin") =>
        new(new EntropyKeyStore(Path.Combine(temp.RootPath, name)));

    private static string Mutate(string protectedText, string mutation)
    {
        string prefix = AppEntropySecretProtector.SchemePrefix;
        byte[] envelope = Convert.FromBase64String(protectedText.Substring(prefix.Length));
        switch (mutation)
        {
            case "nonce":
                envelope[0] ^= 0xFF;
                return prefix + Convert.ToBase64String(envelope);
            case "tag":
                envelope[AppEntropySecretProtector.NonceSizeBytes] ^= 0xFF;
                return prefix + Convert.ToBase64String(envelope);
            case "ciphertext":
                envelope[AppEntropySecretProtector.NonceSizeBytes + AppEntropySecretProtector.TagSizeBytes] ^= 0xFF;
                return prefix + Convert.ToBase64String(envelope);
            case "prefix":
                return "cfsec2:" + protectedText.Substring(prefix.Length);
            case "truncated":
                return protectedText.Substring(0, protectedText.Length - 8);
            case "bad-base64":
                return prefix + "!!!not-valid-base64!!!";
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}