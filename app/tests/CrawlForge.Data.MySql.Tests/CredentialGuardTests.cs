using System.Security.Cryptography;
using System.Text.Json;
using CrawlForge.Security.Secrets;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Credential protection conformance (§9.1, criterion #18): external-database
/// credentials round-trip through the real app-entropy protector, ciphertext never
/// contains plaintext, and tampered or foreign-key payloads fail closed. Key
/// material is app-generated random entropy — never hardware-derived.
/// </summary>
public sealed class CredentialGuardTests
{
    private static MySqlDatabaseSettings ValidSettings() => new()
    {
        Host = "db.example.invalid",
        Port = 3306,
        Database = "crawlforge",
        UserId = "cf_user",
        Password = "s3cret-pa55word",
    };

    /// <summary>Protect/restore round-trips every settings field exactly.</summary>
    [Fact]
    public void Protect_Restore_Round_Trip()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);
        MySqlDatabaseSettings settings = ValidSettings();

        StoredMySqlSettings stored = guard.Protect(settings);
        MySqlDatabaseSettings restored = guard.Restore(stored);

        Assert.Equal(settings, restored);
    }

    /// <summary>Both credential components are stored encrypted, never as plaintext.</summary>
    [Fact]
    public void Credentials_Are_Encrypted_At_Rest()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);
        MySqlDatabaseSettings settings = ValidSettings();

        StoredMySqlSettings stored = guard.Protect(settings);

        Assert.NotEqual(settings.UserId, stored.ProtectedUserId);
        Assert.NotEqual(settings.Password, stored.ProtectedPassword);
        Assert.StartsWith(AppEntropySecretProtector.SchemePrefix, stored.ProtectedUserId, StringComparison.Ordinal);
        Assert.StartsWith(AppEntropySecretProtector.SchemePrefix, stored.ProtectedPassword, StringComparison.Ordinal);
        Assert.Equal(settings.Host, stored.Host);
        Assert.Equal(settings.Port, stored.Port);
        Assert.Equal(settings.Database, stored.Database);
    }

    /// <summary>The storage JSON payload contains no plaintext credential material.</summary>
    [Fact]
    public void Storage_Json_Contains_No_Plaintext_Credentials()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);

        string json = guard.ToStorageJson(ValidSettings());

        Assert.DoesNotContain("s3cret-pa55word", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cf_user", json, StringComparison.Ordinal);
        Assert.Contains("db.example.invalid", json, StringComparison.Ordinal);
    }

    /// <summary>Storage JSON round-trips through deserialize and decrypt.</summary>
    [Fact]
    public void Storage_Json_Round_Trip()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);
        MySqlDatabaseSettings settings = ValidSettings();

        string json = guard.ToStorageJson(settings);
        MySqlDatabaseSettings restored = guard.FromStorageJson(json);

        Assert.Equal(settings, restored);
    }

    /// <summary>Protection is randomized: identical plaintext yields distinct ciphertext.</summary>
    [Fact]
    public void Protection_Uses_Fresh_Randomness()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);

        StoredMySqlSettings first = guard.Protect(ValidSettings());
        StoredMySqlSettings second = guard.Protect(ValidSettings());

        Assert.NotEqual(first.ProtectedPassword, second.ProtectedPassword);
        Assert.NotEqual(first.ProtectedUserId, second.ProtectedUserId);
    }

    /// <summary>A tampered credential payload fails closed with a cryptographic exception.</summary>
    [Fact]
    public void Tampered_Credential_Fails_Closed()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);
        StoredMySqlSettings stored = guard.Protect(ValidSettings());

        string tampered = stored.ProtectedPassword[..^4] + "AAAA";
        StoredMySqlSettings tamperedStored = stored with { ProtectedPassword = tampered };

        Assert.ThrowsAny<CryptographicException>(() => guard.Restore(tamperedStored));
    }

    /// <summary>A payload encrypted under a different app-entropy key cannot be restored.</summary>
    [Fact]
    public void Foreign_Key_Payload_Fails_Closed()
    {
        using var firstFixture = new TempProtector();
        using var secondFixture = new TempProtector();
        var firstGuard = new MySqlCredentialGuard(firstFixture.Protector);
        var secondGuard = new MySqlCredentialGuard(secondFixture.Protector);

        StoredMySqlSettings stored = firstGuard.Protect(ValidSettings());

        Assert.ThrowsAny<CryptographicException>(() => secondGuard.Restore(stored));
    }

    /// <summary>Malformed storage JSON is rejected.</summary>
    [Fact]
    public void Malformed_Storage_Json_Is_Rejected()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);

        Assert.Throws<JsonException>(() => guard.FromStorageJson("{ not json"));
        Assert.Throws<ArgumentException>(() => guard.FromStorageJson(" "));
    }

    /// <summary>Protection validates settings before encrypting anything.</summary>
    [Fact]
    public void Protect_Validates_Settings()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);
        MySqlDatabaseSettings invalid = ValidSettings() with { Host = "" };

        Assert.Throws<ArgumentException>(() => guard.Protect(invalid));
    }

    /// <summary>The key file is app-generated random entropy of the documented length.</summary>
    [Fact]
    public void Key_Material_Is_App_Generated_Random_Entropy()
    {
        using var fixture = new TempProtector();
        var guard = new MySqlCredentialGuard(fixture.Protector);

        // First use creates the key file; it must be exactly 32 random bytes.
        guard.Protect(ValidSettings());

        Assert.True(File.Exists(fixture.KeyStore.KeyFilePath));
        Assert.Equal(EntropyKeyStore.KeyLengthBytes, new FileInfo(fixture.KeyStore.KeyFilePath).Length);
    }
}
