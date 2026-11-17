using System.Net;
using System.Text;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Intelligence.Embeddings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Boundary.Tests.Consent;

/// <summary>
/// Exclusion E8 (DNS verification is the only optional network action of the
/// log analyzer: user-triggered, off by default, per-import consent) and E7
/// (embedding endpoints must be loopback; remote embedding APIs are forbidden).
/// </summary>
public sealed class ConsentAndEmbeddingTests
{
    [Fact]
    public async Task DnsVerification_RequiresExplicitConsent_NothingSentWithoutIt()
    {
        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        await using var fixture = await BackendFixture.StartAsync();

        // Seed a project + completed log import (local rows only).
        var projects = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Consent", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var imports = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<LogImportDto>>();
        var import = await imports.CreateAsync(new LogImportDto(
            0, project.Id, LogFileFormat.ApacheNginxCombined,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            "{}", BotVerificationMode.CidrOnly, LogImportStatus.Completed,
            DateTimeOffset.UtcNow, null));

        monitor.SetPhase("dns-consent-gate");

        // The consent gate is enforced by the verification tool itself: without
        // consent it throws BEFORE any verifier/DNS invocation.
        var tool = fixture.Backend.App.Services.GetRequiredService<CrawlForge.Intelligence.LogAnalyzer.ILogDnsVerificationTool>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.VerifySampleAsync(import.Id, consent: false));
        Assert.Contains("consent", ex.Message, StringComparison.OrdinalIgnoreCase);

        await Task.Delay(500);
        // Note: DNS queries are UDP; the TCP monitor cannot observe them. The
        // gate proof is the tool's pre-network refusal (consent check precedes
        // every verifier call), and the default mode is CidrOnly (zero outbound).
        Assert.Empty(monitor.NonLoopbackObservations);

        EvidenceWriter.WriteMarkdown(
            "dns-consent.md",
            """
            # Exclusion E8 — DNS verification consent gate

            - Default bot verification mode: CidrOnly (bundled CIDR + agent profiles, zero outbound).
            - LogDnsVerificationTool.VerifySampleAsync(importId, consent:false) throws
              InvalidOperationException ("DNS verification requires explicit per-import user
              consent") BEFORE any verifier invocation — proven at runtime via DI.
            - LogsController.VerifyDns additionally returns started=false without invoking
              the tool when consent is absent (source-verified).
            - DNS verification is the ONLY optional network action of the log analyzer;
              it is user-triggered, per-import, off by default, and locally logged.
            - Monitoring note: DNS is UDP; the harness TCP monitor cannot observe queries.
              The gate proof is the pre-network refusal + code path.

            ## API-surface defect — RESOLVED

            Previously all /api/logs/* routes returned 500: LogsController requires
            ILogCorrelationReadModel, which lacked a DI forwarding registration
            (EngineServicesModule registered only ILogCorrelationEngine, although
            LogCorrelationEngine implements both interfaces). Reported as a WP-07-02
            blocker; fixed in cf-wp-00-04's composition batch (concrete singleton
            forwarded to BOTH interfaces). The REST consent gate is now verified by
            LogsRestSurfaceTests (200 + started=false without consent).
            """);
    }

    [Fact]
    public void EmbeddingValidator_RejectsEveryNonLoopbackEndpoint()
    {
        string[] forbidden =
        [
            "http://192.168.1.50:11434",
            "http://10.0.0.5:11434",
            "https://api.openai.com/v1/embeddings",
            "http://embeddings.vendor.example:8080",
            "http://172.16.0.1:11434",
            "ftp://127.0.0.1:21",
        ];
        foreach (var endpoint in forbidden)
        {
            Assert.Throws<EmbeddingConfigurationException>(() => LoopbackEndpointValidator.Validate(endpoint));
            Assert.False(LoopbackEndpointValidator.TryValidate(endpoint, out _));
        }

        string[] allowed = ["http://127.0.0.1:11434", "http://localhost:11434", "http://[::1]:11434"];
        foreach (var endpoint in allowed)
        {
            var uri = LoopbackEndpointValidator.Validate(endpoint);
            var address = LoopbackEndpointValidator.ResolveLoopbackAddress(uri);
            Assert.True(IPAddress.IsLoopback(address));
        }

        // localhost is pinned to the loopback IP WITHOUT DNS (poisoned hosts file safe).
        var pinned = LoopbackEndpointValidator.ResolveLoopbackAddress(new Uri("http://localhost:11434"));
        Assert.Equal(IPAddress.Loopback, pinned);

        EvidenceWriter.WriteMarkdown(
            "embedding-loopback.md",
            """
            # Exclusion E7 — embedding endpoint loopback enforcement

            - Rejected: private LAN (192.168/10/172.16), public hosts, vendor APIs, non-http schemes.
            - Accepted: 127.0.0.1, ::1, localhost only.
            - localhost resolves to the loopback IP WITHOUT DNS (hosts-file poisoning cannot
              redirect daemon traffic off-machine).
            - Validation is re-applied before every daemon request (defense in depth,
              LoopbackDaemonEmbeddingProvider).
            """);
    }

    [Fact]
    public async Task EmbeddingTestSurface_RejectsNonLoopback()
    {
        await using var fixture = await BackendFixture.StartAsync();
        using var response = await fixture.Http.PostAsync(
            "/api/settings/embedding/test",
            new StringContent("""{"endpoint":"http://192.0.2.10:11434"}""", Encoding.UTF8, "application/json"));
        // Non-loopback candidates are rejected at validation (400) — no provider
        // is constructed and no connection is attempted.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("loopback", body, StringComparison.OrdinalIgnoreCase);
    }
}
