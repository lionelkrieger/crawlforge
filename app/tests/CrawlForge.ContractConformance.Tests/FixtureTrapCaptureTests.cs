using System.Text.Json;
using CrawlForge.ContractConformance.Tests.Harness;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Fixture trap capture conformance (WP-08-03 done_when): the http-mode
/// fixture audit's payloads for the noindex and redirect-chain traps are
/// captured from the LIVE backend and asserted for wire shape. Trap SEMANTICS
/// (hop counts, finding verdicts) belong to cf-wp-08-04's fixture acceptance;
/// this suite asserts shape only and records the known G27/G03 limitations.
/// </summary>
[Collection(LiveBackendCollection.Name)]
public sealed class FixtureTrapCaptureTests
{
    private readonly LiveBackendFixture _fixture;

    /// <summary>Creates the suite over the shared live backend.</summary>
    /// <param name="fixture">Shared live-backend fixture.</param>
    public FixtureTrapCaptureTests(LiveBackendFixture fixture) => _fixture = fixture;

    /// <summary>
    /// noindex trap: url row + url detail + findings + summary captured and
    /// conformant (UrlRecordSummaryWire / UrlDetailWire / FindingWire /
    /// AuditSummaryExplorerWire shapes).
    /// </summary>
    [Fact]
    public async Task NoindexTrap_PayloadsCapturedAndAsserted()
    {
        var row = Capture("trap-noindex-url-row.json");
        Assert.EndsWith("/pages/noindex.html", row.GetProperty("canonicalUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.String, row.GetProperty("id").ValueKind);
        Assert.Equal(200, row.GetProperty("statusCode").GetInt32());
        // Shape only: indexable is a boolean dimension on the wire. Its VALUE
        // for the noindex trap is a semantics question (G-NOINDEX-FLAG-QUESTION:
        // observed true despite meta robots noindex; cf-wp-08-04 owns trap
        // semantics via the indexability.noindex finding) — not asserted here.
        Assert.True(row.GetProperty("indexable").ValueKind is JsonValueKind.True or JsonValueKind.False);

        var detail = Capture("trap-noindex-url-detail.json");
        Assert.Equal(row.GetProperty("id").GetString(), detail.GetProperty("id").GetString());
        Assert.EndsWith("/pages/noindex.html", detail.GetProperty("canonicalUrl").GetString(), StringComparison.Ordinal);
        Assert.True(detail.GetProperty("indexable").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("elementInventory").ValueKind);
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("findings").ValueKind);
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("inboundLinks").ValueKind);
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("outboundLinks").ValueKind);
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("redirectChain").ValueKind);
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("responseHeaders").ValueKind);
        AssertFindingsShape(detail.GetProperty("findings"));

        var findings = Capture("trap-noindex-findings.json");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        AssertFindingsShape(findings);

        var summary = Capture("trap-noindex-summary.json");
        AssertSummaryShape(summary);
        await Task.CompletedTask;
    }

    /// <summary>
    /// redirect-chain trap: url row + detail captured; the detail carries the
    /// redirectChain array (RedirectHopWire shape) and the final target.
    /// Shape-only: G27 means the chain is not persisted hop-by-hop today, so
    /// no hop-count assertion is made (cf-wp-08-04 owns trap semantics).
    /// </summary>
    [Fact]
    public async Task RedirectChainTrap_PayloadsCapturedAndAsserted()
    {
        var row = Capture("trap-redirect-chain-url-row.json");
        var canonical = row.GetProperty("canonicalUrl").GetString()!;
        Assert.True(
            canonical.EndsWith("/redirect/chain-1", StringComparison.Ordinal)
                || canonical.EndsWith("/pages/redirect-target.html", StringComparison.Ordinal),
            $"unexpected redirect-chain trap row: {canonical}");
        Assert.Equal(JsonValueKind.String, row.GetProperty("id").ValueKind);

        var detail = Capture("trap-redirect-chain-url-detail.json");
        Assert.Equal(row.GetProperty("id").GetString(), detail.GetProperty("id").GetString());
        var chain = detail.GetProperty("redirectChain");
        Assert.Equal(JsonValueKind.Array, chain.ValueKind);
        foreach (var hop in chain.EnumerateArray())
        {
            // RedirectHopWire: {url, statusCode, location?}.
            Assert.Equal(JsonValueKind.String, hop.GetProperty("url").ValueKind);
            Assert.Equal(JsonValueKind.Number, hop.GetProperty("statusCode").ValueKind);
            Assert.True(hop.TryGetProperty("location", out _));
        }

        Assert.Equal(JsonValueKind.Array, detail.GetProperty("findings").ValueKind);
        AssertFindingsShape(detail.GetProperty("findings"));

        var findings = Capture("trap-redirect-chain-findings.json");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        AssertFindingsShape(findings);

        var summary = Capture("trap-redirect-chain-summary.json");
        AssertSummaryShape(summary);
        await Task.CompletedTask;
    }

    /// <summary>The fixture recorder proves the crawl stayed GET-only.</summary>
    [Fact]
    public void FixtureRecorder_NoNonGetRequests()
    {
        var requests = _fixture.FixtureRequestsAfterAudit;
        Assert.Equal(0, requests.GetProperty("nonGet").GetArrayLength());
        Assert.True(requests.TryGetProperty("tagHits", out _), "recorder snapshot must carry tagHits");
    }

    /// <summary>All trap captures are committed under the project's captured/ directory.</summary>
    [Fact]
    public void Captures_CommittedToProject()
    {
        var expected = new[]
        {
            "trap-noindex-url-row.json",
            "trap-noindex-url-detail.json",
            "trap-noindex-findings.json",
            "trap-noindex-summary.json",
            "trap-redirect-chain-url-row.json",
            "trap-redirect-chain-url-detail.json",
            "trap-redirect-chain-findings.json",
            "trap-redirect-chain-summary.json"
        };
        foreach (var fileName in expected)
        {
            var path = Path.Combine(RepoPaths.CapturedDir, fileName);
            Assert.True(File.Exists(path), $"missing committed capture: {fileName}");
            Assert.True(_fixture.TrapCaptures.ContainsKey(fileName), $"capture not held by the fixture: {fileName}");
        }
    }

    private JsonElement Capture(string fileName)
    {
        Assert.True(_fixture.TrapCaptures.TryGetValue(fileName, out var payload), $"missing capture {fileName}");
        return payload;
    }

    private static void AssertFindingsShape(JsonElement findings)
    {
        foreach (var finding in findings.EnumerateArray())
        {
            // FindingWire: id, ruleKey, severity, category, urlId?, message, evidence?.
            Assert.Equal(JsonValueKind.String, finding.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, finding.GetProperty("ruleKey").ValueKind);
            Assert.Equal(JsonValueKind.String, finding.GetProperty("severity").ValueKind);
            Assert.Equal(JsonValueKind.String, finding.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.String, finding.GetProperty("message").ValueKind);
            var severity = finding.GetProperty("severity").GetString()!;
            Assert.Equal(severity, severity.ToLowerInvariant());
            if (finding.TryGetProperty("evidence", out var evidence) && evidence.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal(JsonValueKind.Object, evidence.ValueKind);
            }
        }
    }

    private static void AssertSummaryShape(JsonElement summary)
    {
        // AuditDashboardSummaryWire (Api/Controllers/AuditExplorerWireContracts.cs:185):
        // countsByStatus, countsBySeverity, countsByCategory, crawlHealth,
        // waf? (nullable), throttle? (nullable).
        foreach (var field in new[] { "countsByStatus", "countsBySeverity", "countsByCategory", "crawlHealth" })
        {
            Assert.True(summary.TryGetProperty(field, out var value), $"summary missing {field}");
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
        }

        Assert.True(summary.TryGetProperty("waf", out _), "summary missing waf (nullable)");
        Assert.True(summary.TryGetProperty("throttle", out _), "summary missing throttle (nullable)");
        var health = summary.GetProperty("crawlHealth");
        Assert.True(health.GetProperty("totalUrls").GetInt64() > 0);
        Assert.True(health.GetProperty("fetched").GetInt64() > 0);
    }
}
