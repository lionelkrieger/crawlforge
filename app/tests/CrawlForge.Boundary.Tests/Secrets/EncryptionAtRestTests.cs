using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Security;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Security.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Boundary.Tests.Secrets;

/// <summary>
/// Acceptance criterion #17 (§9.1): all five credential classes are encrypted
/// at rest — site signing material, destination credentials, proxy credentials,
/// external-database credentials, and local tokens. Secrets are written through
/// the REAL product surfaces, then every file in the data directory is scanned
/// for plaintext: none may exist. Key material is app-generated random entropy,
/// never hardware/OS-identity derived.
/// </summary>
public sealed class EncryptionAtRestTests
{
    [Fact]
    public async Task AllFiveCredentialClasses_EncryptedAtRest_NoPlaintextOnDisk()
    {
        await using var fixture = await BackendFixture.StartAsync();
        var protector = fixture.Backend.App.Services.GetRequiredService<ISecretProtector>();
        var suffix = Guid.NewGuid().ToString("N");

        var proxySecret = "PROXY-SECRET-" + suffix;
        var dbSecret = "DB-SECRET-" + suffix;
        var ftpSecret = "FTP-SECRET-" + suffix;
        var signingSecret = "SIGNING-SECRET-" + suffix;

        // Class 3 (proxy) + class 4 (external DB): settings surface.
        var settingsPayload = JsonSerializer.Serialize(new
        {
            proxy = new { enabled = true, host = "proxy.example.invalid", port = 8080, username = "proxy-user", password = proxySecret },
            database = new { provider = "mysql", host = "db.example.invalid", port = 3306, database = "crawlforge", username = "db-user", password = dbSecret },
        });
        using (var settingsResponse = await fixture.Http.PutAsync("/api/settings", new StringContent(settingsPayload, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        }

        // Class 2 (destination): destinations surface.
        var destinationPayload = JsonSerializer.Serialize(new
        {
            name = "boundary-ftp",
            kind = "ftp",
            target = "ftp://ftp.example.invalid:21/uploads",
            username = "ftp-user",
            secret = ftpSecret,
        });
        using (var destinationResponse = await fixture.Http.PostAsync("/api/destinations", new StringContent(destinationPayload, Encoding.UTF8, "application/json")))
        {
            Assert.True(
                destinationResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Destination create returned {(int)destinationResponse.StatusCode}.");
        }

        // Class 1 (site signing material): protected store.
        var signingStore = new SignedRequestSecretStore(protector, Path.Combine(fixture.DataDirectory, "signing-material.json"));
        await signingStore.SaveAsync(new SignedRequestSecret("key-1", signingSecret, "hmac-sha256", "X-Signature"));
        var restored = await signingStore.LoadAsync();
        Assert.NotNull(restored);
        Assert.Equal(signingSecret, restored.Secret.Secret);

        // Class 5 (local token): MCP token (protected file proven in McpBoundaryTests).
        var mcpToken = ReadMcpToken(fixture.DataDirectory);

        // Reads never return plaintext: settings masks passwords.
        using (var read = await fixture.Http.GetAsync("/api/settings"))
        {
            var body = await read.Content.ReadAsStringAsync();
            Assert.DoesNotContain(proxySecret, body);
            Assert.DoesNotContain(dbSecret, body);
            Assert.Contains("********", body);
        }

        // Full data-directory plaintext scan (database files, WAL, JSON stores).
        // Per-launch handshake descriptors (mcp-endpoint.json, loopback-handshake.json)
        // are RUNTIME bootstrap artifacts, not durable credential stores: they exist
        // only while the backend runs and are retired at shutdown (proven separately
        // in HandshakeDescriptors_RetiredAtShutdown). The durable token store
        // (mcp-token.json) is encrypted and IS scanned.
        string[] transientHandshakes = ["mcp-endpoint.json", "loopback-handshake.json"];
        var secrets = new[] { proxySecret, dbSecret, ftpSecret, signingSecret, mcpToken };
        var files = Directory.EnumerateFiles(fixture.DataDirectory, "*", SearchOption.AllDirectories).ToList();
        var leaks = new List<string>();
        var protectedMarkers = 0;
        foreach (var file in files)
        {
            if (transientHandshakes.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // The live backend holds the SQLite files; read with shared access.
            string text;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync();
            }

            foreach (var secret in secrets)
            {
                if (text.Contains(secret, StringComparison.Ordinal))
                {
                    leaks.Add($"{Path.GetFileName(file)} contains a plaintext secret");
                }
            }

            if (text.Contains("cfsec1:", StringComparison.Ordinal))
            {
                protectedMarkers++;
            }
        }

        EvidenceWriter.WriteMarkdown(
            "encryption-at-rest.md",
            $"""
            # Criterion #17 — five credential classes encrypted at rest

            | Class | Surface | Storage |
            |---|---|---|
            | Site signing material | SignedRequestSecretStore | signing-material.json (cfsec1:) |
            | Destination credentials | POST /api/destinations (ftp) | export_destinations.encrypted_credentials_json |
            | Proxy credentials | PUT /api/settings | application_settings storage proxy.passwordProtected |
            | External-DB credentials | PUT /api/settings (mysql) | application_settings storage database.passwordProtected |
            | Local automation token | McpTokenService | mcp-token.json (cfsec1:) |

            - Files scanned in data directory: {files.Count} (transient handshake descriptors excluded — retired at shutdown, proven separately)
            - Plaintext secret leaks: **{leaks.Count}** (required: 0)
            - Files carrying protected (cfsec1:) material: {protectedMarkers}
            - Settings read masks passwords: yes (********)
            - Key derivation: app-generated 32-byte RandomNumberGenerator entropy (EntropyKeyStore), never hardware/OS identity

            Leaks: {(leaks.Count == 0 ? "none" : string.Join("; ", leaks))}
            """);

        Assert.Empty(leaks);
        Assert.True(protectedMarkers >= 3, "Protected material must be present in the settings DB, signing store, and token file.");
    }

    [Fact]
    public void Protector_TamperRejection_RoundTrip_AndKeyIndependence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-boundary-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storeA = new EntropyKeyStore(Path.Combine(directory, "a.key"));
            var storeB = new EntropyKeyStore(Path.Combine(directory, "b.key"));
            var protectorA = new AppEntropySecretProtector(storeA);
            var protectorB = new AppEntropySecretProtector(storeB);

            const string secret = "round-trip-secret";
            var protectedA = protectorA.Protect(secret);
            Assert.StartsWith("cfsec1:", protectedA);
            Assert.Equal(secret, protectorA.Unprotect(protectedA));

            // Independent app-entropy keys: different random material, cross-decrypt fails.
            Assert.NotEqual(protectedA, protectorB.Protect(secret));
            Assert.ThrowsAny<CryptographicException>(() => protectorB.Unprotect(protectedA));

            // Tamper detection: flip a ciphertext byte.
            var payload = protectedA["cfsec1:".Length..];
            var raw = Convert.FromBase64String(payload);
            raw[^1] ^= 0xFF;
            var tampered = "cfsec1:" + Convert.ToBase64String(raw);
            Assert.ThrowsAny<CryptographicException>(() => protectorA.Unprotect(tampered));

            // Key files are 32 random bytes, persisted locally; two stores differ.
            var keyA = File.ReadAllBytes(storeA.KeyFilePath);
            var keyB = File.ReadAllBytes(storeB.KeyFilePath);
            Assert.Equal(EntropyKeyStore.KeyLengthBytes, keyA.Length);
            Assert.Equal(EntropyKeyStore.KeyLengthBytes, keyB.Length);
            Assert.False(keyA.AsSpan().SequenceEqual(keyB), "App-entropy keys must be independently random.");

            EvidenceWriter.WriteMarkdown(
                "secret-protector-properties.md",
                """
                # Criterion #17 — protector properties

                - Scheme: cfsec1: (AES-256-GCM, authenticated; prefix as AAD)
                - Round-trip: pass
                - Tampered ciphertext: CryptographicException (rejected)
                - Cross-key decrypt: CryptographicException (rejected)
                - Key material: 32 bytes from RandomNumberGenerator per store, persisted locally,
                  independently random — no hardware/OS-identity derivation anywhere in EntropyKeyStore
                """);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task HandshakeDescriptors_RetiredAtShutdown()
    {
        // Per-launch handshake descriptors carry the local bootstrap tokens in
        // clear ONLY while the backend runs; they must not survive shutdown as
        // durable plaintext at rest. The durable stores are encrypted (above).
        var dataDirectory = Path.Combine(Path.GetTempPath(), "cf-boundary-" + Guid.NewGuid().ToString("N"));
        string[] args = [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"];

        var backend = await CrawlForge.Host.CrawlForgeBackend.StartAsync(args);
        var mcpDescriptor = Path.Combine(dataDirectory, "mcp-endpoint.json");
        var sessionHandshake = Path.Combine(dataDirectory, "loopback-handshake.json");
        Assert.True(File.Exists(mcpDescriptor), "MCP descriptor exists while running.");
        Assert.True(File.Exists(sessionHandshake), "Session handshake exists while running.");
        await backend.DisposeAsync();

        Assert.False(File.Exists(mcpDescriptor), "MCP descriptor must be retired at shutdown.");
        Assert.False(File.Exists(sessionHandshake), "Session handshake must be retired at shutdown.");

        // The durable token store survives — encrypted.
        var durableToken = Path.Combine(dataDirectory, "mcp-token.json");
        Assert.True(File.Exists(durableToken));
        Assert.Contains("cfsec1:", await File.ReadAllTextAsync(durableToken));

        EvidenceWriter.WriteMarkdown(
            "handshake-lifecycle.md",
            """
            # Criterion #17 — handshake descriptor lifecycle

            - mcp-endpoint.json + loopback-handshake.json: per-launch runtime bootstrap
              artifacts (local shell / stdio adapter discovery), present only while running.
            - Both RETIRED (deleted) at graceful shutdown — no durable plaintext token at rest.
            - Durable token store mcp-token.json survives shutdown, cfsec1:-encrypted.

            ## Orchestrator §9.1 ruling (WP-07-02, accepted)

            Runtime handshake ≠ durable at-rest store. NOT a product-design blocker.
            §9.1 (network-boundary-and-security.md:61) mandates encryption for the DURABLE
            secret store; process-model-and-lifecycle.md:45 explicitly mandates "a port
            descriptor written to the per-user application data directory, or an equivalent
            spawn-time handshake" — the bootstrap descriptors are that spec-mandated
            transient discovery mechanism.

            STRICT-READING NUANCE (recorded for sign-off): "any local token" is broad
            wording; a strict auditor could argue it covers the handshake. The spec's own
            escape valve is "or an equivalent spawn-time handshake" — a future hardening
            could pass the session token via spawn-time IPC (never on disk). Recorded as a
            product-design option; WP-07-04 attestation carries the classification rationale.

            CRASH RESIDUAL (defense-in-depth note): a crash may leave a stale descriptor;
            its per-launch token is invalid after restart (new token) → dead artifact,
            harmless. Optional hardening: startup purge of prior-launch descriptors.
            """);

        try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
    }

    private static string ReadMcpToken(string dataDirectory)
    {
        using var descriptor = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "mcp-endpoint.json")));
        return descriptor.RootElement.GetProperty("token").GetString()!;
    }
}
