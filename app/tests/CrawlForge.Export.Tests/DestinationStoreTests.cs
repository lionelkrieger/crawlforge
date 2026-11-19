using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Delivery;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Destination credential/endpoint proofs (WP-09-10, CAP-H-X02; §8.5/§9.1):
/// credentials are ciphertext at rest through the REAL AppEntropySecretProtector
/// (raw database bytes never contain the plaintext), round-trip at the point of
/// use, and endpoint validation rejects traversal / non-http webhook targets.
/// The REST CRUD surface lives in Api (WP-09-06); these proofs cover the
/// Export-side components it composes.
/// </summary>
public sealed class DestinationStoreTests
{
    /// <summary>Ciphertext at rest: the stored column never contains the secret.</summary>
    [Fact]
    public async Task Credentials_Are_Ciphertext_At_Rest()
    {
        await using var stack = new ExportTestStack();
        var credentials = new DestinationCredentials("backup-user", "sup3r-s3cret-password", null);
        var protectedJson = DestinationCredentials.Protect(stack.Protector, credentials);

        // The protected payload itself must not leak the plaintext.
        Assert.DoesNotContain("sup3r-s3cret-password", protectedJson);
        Assert.DoesNotContain("backup-user", protectedJson);

        var destinationId = await stack.SeedDestinationAsync(
            ExportDestinationClass.Sftp,
            """{"host":"files.example.com","port":22,"remotePath":"/backups"}""",
            protectedJson);

        // Raw stored bytes: ciphertext, not plaintext.
        var stored = (string?)await stack.ScalarAsync("SELECT `encrypted_credentials_json` FROM `export_destinations` WHERE `id` = @id", ("@id", destinationId));
        Assert.NotNull(stored);
        Assert.DoesNotContain("sup3r-s3cret-password", stored);
        Assert.DoesNotContain("backup-user", stored);
        Assert.NotEqual("""{"username":"backup-user","password":"sup3r-s3cret-password"}""", protectedJson);

        // Point-of-use round-trip recovers the exact credentials.
        var recovered = DestinationCredentials.Unprotect(stack.Protector, stored);
        Assert.NotNull(recovered);
        Assert.Equal("backup-user", recovered.Username);
        Assert.Equal("sup3r-s3cret-password", recovered.Password);
        Assert.Null(recovered.Token);
    }

    /// <summary>Webhook bearer tokens are protected the same way.</summary>
    [Fact]
    public async Task WebhookToken_Is_Ciphertext_At_Rest()
    {
        await using var stack = new ExportTestStack();
        var protectedJson = DestinationCredentials.Protect(stack.Protector, new DestinationCredentials(null, null, "bearer-token-xyz"));
        var destinationId = await stack.SeedDestinationAsync(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""", protectedJson);

        var stored = (string?)await stack.ScalarAsync("SELECT `encrypted_credentials_json` FROM `export_destinations` WHERE `id` = @id", ("@id", destinationId));
        Assert.NotNull(stored);
        Assert.DoesNotContain("bearer-token-xyz", stored);
        Assert.Equal("bearer-token-xyz", DestinationCredentials.Unprotect(stack.Protector, stored)!.Token);
    }

    /// <summary>Absent credentials unprotect to null (LocalFolder needs none).</summary>
    [Fact]
    public void Unprotect_Null_Payload_Returns_Null()
    {
        var protector = new CrawlForge.Security.Secrets.AppEntropySecretProtector(
            new CrawlForge.Security.Secrets.EntropyKeyStore(Path.Combine(Path.GetTempPath(), "cf-dest-" + Guid.NewGuid().ToString("N") + ".key")));
        Assert.Null(DestinationCredentials.Unprotect(protector, null));
        Assert.Null(DestinationCredentials.Unprotect(protector, string.Empty));
    }

    /// <summary>Endpoint validation: class shapes, traversal rejection, http(s)-only webhooks.</summary>
    [Theory]
    [InlineData(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""", true)]
    [InlineData(ExportDestinationClass.LocalFolder, """{"path":""}""", false)]
    [InlineData(ExportDestinationClass.Ftp, """{"host":"ftp.example.com","port":21,"remotePath":"/drops"}""", true)]
    [InlineData(ExportDestinationClass.Sftp, """{"host":"files.example.com","remotePath":"/../escape"}""", false)]
    [InlineData(ExportDestinationClass.Sftp, """{"port":22}""", false)]
    [InlineData(ExportDestinationClass.Webhook, """{"url":"https://user.example/hook"}""", true)]
    [InlineData(ExportDestinationClass.Webhook, """{"url":"ftp://user.example/hook"}""", false)]
    [InlineData(ExportDestinationClass.Webhook, """{"url":"not-a-url"}""", false)]
    public void Endpoint_Validation_Matches_Class(ExportDestinationClass destinationClass, string json, bool valid)
    {
        var endpoint = DestinationEndpoint.Parse(json);
        if (valid)
        {
            endpoint.Validate(destinationClass);
        }
        else
        {
            Assert.Throws<FormatException>(() => endpoint.Validate(destinationClass));
        }
    }

    /// <summary>Malformed endpoint JSON is a FormatException, never a silent pass.</summary>
    [Fact]
    public void Malformed_Endpoint_Json_Is_Rejected()
    {
        Assert.Throws<FormatException>(() => DestinationEndpoint.Parse("{not json"));
        Assert.Throws<ArgumentException>(() => DestinationEndpoint.Parse("  "));
    }

    /// <summary>Enable/disable round-trips through the durable row (delivery honors it).</summary>
    [Fact]
    public async Task EnableDisable_RoundTrips_Through_Durable_Row()
    {
        await using var stack = new ExportTestStack();
        var destinationId = await stack.SeedDestinationAsync(ExportDestinationClass.LocalFolder, """{"path":"C:\\exports"}""", enabled: true);
        Assert.Equal(1L, (long)(await stack.ScalarAsync("SELECT `is_enabled` FROM `export_destinations` WHERE `id` = @id", ("@id", destinationId)))!);

        await stack.ExecuteAsync("UPDATE `export_destinations` SET `is_enabled` = 0 WHERE `id` = @id", ("@id", destinationId));
        Assert.Equal(0L, (long)(await stack.ScalarAsync("SELECT `is_enabled` FROM `export_destinations` WHERE `id` = @id", ("@id", destinationId)))!);
    }
}