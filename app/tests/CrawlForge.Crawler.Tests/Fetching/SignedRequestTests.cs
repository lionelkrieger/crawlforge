using System.Security.Cryptography;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using CrawlForge.Security.Secrets;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Signed-request crawler access (FS-CRAWL-001 §5.10, criterion #34):
/// in-scope-only header application, expiry tracking, and signing material
/// encrypted at rest through the REAL phase-00 AppEntropySecretProtector.
/// </summary>
public sealed class SignedRequestTests
{
    [Fact]
    public void Sign_StaticAlgorithm_AppliesSuppliedHeaderValueVerbatim()
    {
        var handler = new SignedRequestHandler();
        var secret = new SignedRequestSecret("key-1", "user-supplied-signature", SignedRequestHandler.StaticAlgorithm, "X-Signature");
        var request = new HttpFetchRequest("http://target.example.com/page");

        HttpFetchRequest signed = handler.Sign(request, secret);

        Assert.Equal("user-supplied-signature", signed.Headers!["X-Signature"]);
        Assert.Equal(request.Url, signed.Url);
    }

    [Fact]
    public void Sign_HmacSha256_ComputesDeterministicRequestSignature()
    {
        var handler = new SignedRequestHandler();
        var secret = new SignedRequestSecret("key-9", "hmac-secret-material", SignedRequestHandler.HmacSha256Algorithm, "Authorization");
        var request = new HttpFetchRequest("http://target.example.com/path?query=1", Method: "GET");

        HttpFetchRequest signed = handler.Sign(request, secret);

        string expected = Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("hmac-secret-material"),
                Encoding.UTF8.GetBytes("GET /path?query=1"))).ToLowerInvariant();
        Assert.Equal("key-9:" + expected, signed.Headers!["Authorization"]);

        // Deterministic for the same request; different for a different path.
        HttpFetchRequest again = handler.Sign(request, secret);
        Assert.Equal(signed.Headers!["Authorization"], again.Headers!["Authorization"]);
        HttpFetchRequest other = handler.Sign(new HttpFetchRequest("http://target.example.com/other"), secret);
        Assert.NotEqual(signed.Headers!["Authorization"], other.Headers!["Authorization"]);
    }

    [Fact]
    public void Sign_OutOfScopeRequest_ReturnedUnchanged()
    {
        var handler = new SignedRequestHandler(uri => uri.Host == "in.example.com");
        var secret = new SignedRequestSecret("key-1", "sig", SignedRequestHandler.StaticAlgorithm, "X-Signature");
        var request = new HttpFetchRequest(
            "http://out.example.com/page",
            Headers: new Dictionary<string, string> { ["Accept"] = "text/html" });

        HttpFetchRequest signed = handler.Sign(request, secret);

        Assert.Same(request, signed);
        Assert.False(signed.Headers!.ContainsKey("X-Signature"));
    }

    [Fact]
    public void Sign_InScopeRequest_Applied()
    {
        var handler = new SignedRequestHandler(uri => uri.Host == "in.example.com");
        var secret = new SignedRequestSecret("key-1", "sig", SignedRequestHandler.StaticAlgorithm, "X-Signature");

        HttpFetchRequest signed = handler.Sign(new HttpFetchRequest("http://in.example.com/page"), secret);

        Assert.Equal("sig", signed.Headers!["X-Signature"]);
    }

    [Fact]
    public void Sign_PreservesNonSignatureHeaders_AndReplacesStaleSignature()
    {
        var handler = new SignedRequestHandler();
        var secret = new SignedRequestSecret("key-1", "new-sig", SignedRequestHandler.StaticAlgorithm, "X-Signature");
        var request = new HttpFetchRequest(
            "http://target.example.com/",
            Headers: new Dictionary<string, string>
            {
                ["Accept-Language"] = "en",
                ["X-Signature"] = "stale-sig",
            });

        HttpFetchRequest signed = handler.Sign(request, secret);

        Assert.Equal("en", signed.Headers!["Accept-Language"]);
        Assert.Equal("new-sig", signed.Headers["X-Signature"]);
        Assert.Equal("stale-sig", request.Headers!["X-Signature"]);
    }

    [Fact]
    public void Sign_UnknownAlgorithm_Throws()
    {
        var handler = new SignedRequestHandler();
        var secret = new SignedRequestSecret("key-1", "sig", "vendor-proprietary", "X-Signature");

        Assert.Throws<ArgumentException>(() => handler.Sign(new HttpFetchRequest("http://target.example.com/"), secret));
    }

    [Fact]
    public void Sign_InvalidArguments_Throw()
    {
        var handler = new SignedRequestHandler();
        var secret = new SignedRequestSecret("key-1", "sig", SignedRequestHandler.StaticAlgorithm, " ");

        Assert.Throws<ArgumentNullException>(() => handler.Sign(null!, secret));
        Assert.Throws<ArgumentNullException>(() => handler.Sign(new HttpFetchRequest("http://target.example.com/"), null!));
        Assert.Throws<ArgumentException>(() => handler.Sign(new HttpFetchRequest("http://target.example.com/"), secret));
    }

    [Fact]
    public void ExpiryTracker_StatesAcrossWarningWindow()
    {
        var tracker = new SignedRequestExpiryTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        tracker.Register("soon", now.AddHours(2), TimeSpan.FromHours(24));
        tracker.Register("active", now.AddDays(7), TimeSpan.FromHours(24));
        tracker.Register("lapsed", now.AddHours(-1), TimeSpan.FromHours(24));

        Assert.Equal(SignedRequestExpiryState.ExpiringSoon, tracker.GetState("soon", now));
        Assert.Equal(SignedRequestExpiryState.Active, tracker.GetState("active", now));
        Assert.Equal(SignedRequestExpiryState.Expired, tracker.GetState("lapsed", now));
        Assert.Equal(SignedRequestExpiryState.Unknown, tracker.GetState("unregistered", now));

        // After lapse the state remains Expired (post-expiry warning surface).
        Assert.Equal(SignedRequestExpiryState.Expired, tracker.GetState("soon", now.AddHours(3)));
        tracker.Unregister("soon");
        Assert.Equal(SignedRequestExpiryState.Unknown, tracker.GetState("soon", now));
    }

    [Fact]
    public void Sign_ExpiredMaterial_NotApplied()
    {
        var tracker = new SignedRequestExpiryTracker();
        tracker.Register("key-1", DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new SignedRequestHandler(expiryTracker: tracker);
        var secret = new SignedRequestSecret("key-1", "sig", SignedRequestHandler.StaticAlgorithm, "X-Signature");
        var request = new HttpFetchRequest("http://target.example.com/");

        HttpFetchRequest signed = handler.Sign(request, secret);

        Assert.Same(request, signed);
    }

    [Fact]
    public void Sign_ExpiringSoonMaterial_StillApplied()
    {
        var tracker = new SignedRequestExpiryTracker();
        tracker.Register("key-1", DateTimeOffset.UtcNow.AddHours(2));
        var handler = new SignedRequestHandler(expiryTracker: tracker);
        var secret = new SignedRequestSecret("key-1", "sig", SignedRequestHandler.StaticAlgorithm, "X-Signature");

        HttpFetchRequest signed = handler.Sign(new HttpFetchRequest("http://target.example.com/"), secret);

        Assert.Equal("sig", signed.Headers!["X-Signature"]);
    }

    [Fact]
    public async Task SecretStore_RoundTripsThroughRealAppEntropyProtector()
    {
        using var temp = new TempDirectory();
        var protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(temp.RootPath, "entropy.key")));
        var store = new SignedRequestSecretStore(protector, Path.Combine(temp.RootPath, "signing.json"));
        var secret = new SignedRequestSecret("key-42", "super-secret-signing-material", SignedRequestHandler.HmacSha256Algorithm, "X-Signature");
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddDays(30);

        await store.SaveAsync(secret, expiry);
        StoredSignedRequest? loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(secret, loaded.Secret);
        Assert.Equal(expiry, loaded.ExpiresAtUtc);
    }

    [Fact]
    public async Task SecretStore_CiphertextAtRest_NoPlaintext()
    {
        using var temp = new TempDirectory();
        var protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(temp.RootPath, "entropy.key")));
        var store = new SignedRequestSecretStore(protector, Path.Combine(temp.RootPath, "signing.json"));
        const string secretValue = "plaintext-must-never-touch-disk-9f8e7d";

        await store.SaveAsync(new SignedRequestSecret("key-1", secretValue, SignedRequestHandler.StaticAlgorithm, "X-Signature"));

        string raw = await File.ReadAllTextAsync(store.StorageFilePath);
        Assert.DoesNotContain(secretValue, raw, StringComparison.Ordinal);
        Assert.Contains("cfsec1:", raw, StringComparison.Ordinal);
        Assert.Contains("key-1", raw, StringComparison.Ordinal);

        // Nothing else in the storage directory contains the plaintext either.
        foreach (string file in Directory.GetFiles(temp.RootPath, "*", SearchOption.AllDirectories))
        {
            string content = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(secretValue, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SecretStore_TamperedCiphertext_ThrowsOnLoad()
    {
        using var temp = new TempDirectory();
        var protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(temp.RootPath, "entropy.key")));
        var store = new SignedRequestSecretStore(protector, Path.Combine(temp.RootPath, "signing.json"));
        await store.SaveAsync(new SignedRequestSecret("key-1", "tamper-target-secret", SignedRequestHandler.StaticAlgorithm, "X-Signature"));

        string raw = await File.ReadAllTextAsync(store.StorageFilePath);
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(raw)!;
        string protectedValue = node["protectedSecret"]!.GetValue<string>();
        char[] chars = protectedValue.ToCharArray();
        int index = protectedValue.Length - 5;
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        node["protectedSecret"] = new string(chars);
        await File.WriteAllTextAsync(store.StorageFilePath, node.ToJsonString());

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task SecretStore_MissingFile_LoadsNull_AndDeleteRemoves()
    {
        using var temp = new TempDirectory();
        var protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(temp.RootPath, "entropy.key")));
        var store = new SignedRequestSecretStore(protector, Path.Combine(temp.RootPath, "signing.json"));

        Assert.Null(await store.LoadAsync());

        await store.SaveAsync(new SignedRequestSecret("key-1", "s", SignedRequestHandler.StaticAlgorithm, "X-Signature"));
        Assert.True(File.Exists(store.StorageFilePath));

        await store.DeleteAsync();
        Assert.False(File.Exists(store.StorageFilePath));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task SecretStore_SaveOverwrites_PreviousMaterial()
    {
        using var temp = new TempDirectory();
        var protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(temp.RootPath, "entropy.key")));
        var store = new SignedRequestSecretStore(protector, Path.Combine(temp.RootPath, "signing.json"));

        await store.SaveAsync(new SignedRequestSecret("key-1", "first", SignedRequestHandler.StaticAlgorithm, "X-Signature"));
        await store.SaveAsync(new SignedRequestSecret("key-2", "second", SignedRequestHandler.HmacSha256Algorithm, "Authorization"), expiresAtUtc: null);

        StoredSignedRequest? loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("key-2", loaded.Secret.KeyId);
        Assert.Equal("second", loaded.Secret.Secret);
        Assert.Null(loaded.ExpiresAtUtc);
    }
}