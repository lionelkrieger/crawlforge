using System.Text.Json;
using CrawlForge.ContractConformance.Tests.Harness;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Audit counters cross-layer conformance (WP-08-03; the WP-09-06 known gap).
///
/// Producer: AuditRunCounters.ToPayload() (Host/Execution/AuditRunState.cs)
/// writes counters_json with keys {discovered, fetched, analyzed, failed}.
/// Consumer: AuditWireMapper.ParseCounters (Api/Controllers/AuditWireMapper.cs:22)
/// reads {queued, fetched, error, blocked, wafBlocked?} — missing keys default
/// to 0. Only "fetched" overlaps, so the REST wire silently reports
/// queued=0/error=0/blocked=0 for every audit (hotfix-01-report.md §6).
///
/// The single KnownGap-tagged test below is the ONLY test allowed to fail;
/// wave gates run with --filter KnownGap!=WP-09-06.
/// </summary>
[Collection(LiveBackendCollection.Name)]
public sealed class AuditCountersWireConformanceTests
{
    private readonly LiveBackendFixture _fixture;

    /// <summary>Creates the suite over the shared live backend.</summary>
    /// <param name="fixture">Shared live-backend fixture.</param>
    public AuditCountersWireConformanceTests(LiveBackendFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Producer lock: the engine's stored counters_json carries exactly the
    /// AuditRunCounters.ToPayload() keys (discovered/fetched/analyzed/failed +
    /// the additive blocked/wafBlocked WAF sub-counts, WP-09-11).
    /// </summary>
    [Fact]
    public async Task EngineCounters_ProducerKeys()
    {
        var counters = await ReadEngineCountersAsync();
        var keys = counters.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "analyzed", "blocked", "discovered", "failed", "fetched", "wafBlocked" }, keys);
        Assert.True(counters.GetProperty("fetched").GetInt64() > 0, "fixture audit must have fetched URLs");
        await _fixture.SaveCaptureAsync("engine-counters.json", counters);
    }

    /// <summary>
    /// Consumer lock: the REST audit detail exposes the API-owned counter keys
    /// (queued/fetched/error/blocked, optional wafBlocked) per AuditCountersWire.
    /// </summary>
    [Fact]
    public async Task RestAuditDetail_ConsumerKeys()
    {
        var data = await Rest.GetDataAsync(_fixture.Http, $"/api/audits/{_fixture.AuditId}");
        var counters = data.GetProperty("counters");
        foreach (var key in new[] { "queued", "fetched", "error", "blocked" })
        {
            Assert.True(counters.TryGetProperty(key, out var value), $"REST counters missing consumer key '{key}'");
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
        }

        await _fixture.SaveCaptureAsync("rest-counters.json", counters);
    }

    /// <summary>
    /// KNOWN GAP (WP-09-06): the REST wire counters must reflect the engine's
    /// stored counters — queued↔discovered, fetched↔fetched, error↔failed.
    /// Today only "fetched" overlaps; AuditWireMapper.ParseCounters reads keys
    /// the engine never writes, so queued/error/blocked are silently 0
    /// (hotfix-01-report.md §6; CODEMAP §4.10). This is the ONLY failing test
    /// in the suite; gates exclude it with --filter KnownGap!=WP-09-06.
    /// </summary>
    [Fact]
    [Trait("KnownGap", "WP-09-06")]
    public async Task RestCounters_MatchEngineCounters()
    {
        var engine = await ReadEngineCountersAsync();
        var data = await Rest.GetDataAsync(_fixture.Http, $"/api/audits/{_fixture.AuditId}");
        var wire = data.GetProperty("counters");

        var engineDiscovered = engine.GetProperty("discovered").GetInt64();
        var engineFetched = engine.GetProperty("fetched").GetInt64();
        var engineFailed = engine.GetProperty("failed").GetInt64();
        var wireQueued = wire.GetProperty("queued").GetInt64();
        var wireFetched = wire.GetProperty("fetched").GetInt64();
        var wireError = wire.GetProperty("error").GetInt64();

        Assert.True(
            wireFetched == engineFetched && wireQueued == engineDiscovered && wireError == engineFailed,
            "WP-09-06 counter-key mismatch (hotfix-01-report.md §6): " +
            $"engine counters_json = discovered:{engineDiscovered}/fetched:{engineFetched}/failed:{engineFailed}, " +
            $"REST wire = queued:{wireQueued}/fetched:{wireFetched}/error:{wireError}. " +
            "Producer AuditRunCounters.ToPayload() (Host/Execution/AuditRunState.cs) writes " +
            "discovered/fetched/analyzed/failed; consumer AuditWireMapper.ParseCounters " +
            "(Api/Controllers/AuditWireMapper.cs:22) reads queued/fetched/error/blocked/wafBlocked — " +
            "only 'fetched' overlaps; missing keys default to 0.");
    }

    /// <summary>
    /// Producer consistency invariant: the hub crawl-progress snapshot is
    /// bounded by the stored counters_json totals (throttled ≤1/s emission may
    /// lag, never exceed). The invariant is keyed on "fetched" — the one
    /// dimension both the engine and the frozen AuditCounters contract share —
    /// so it survives the WP-09-06 key reconciliation in either direction.
    /// The exact key-set drift (engine vs consumer keys) is recorded in
    /// GAPS.md (G-HUB-COUNTERS) and asserted by the skipped
    /// HubCounters_MatchRestWireKeys test, NOT locked as expected behavior.
    /// </summary>
    [Fact]
    public async Task HubCounters_EngineKeys_MatchStoredCounters()
    {
        var stored = await ReadEngineCountersAsync();
        var progress = _fixture.Hub.EventsNamed("crawl-progress")
            .Where(e => MatchesId(e.Payload, "auditId", _fixture.AuditId))
            .Last();
        var hubCounters = progress.Payload.GetProperty("counters");
        Assert.Equal(JsonValueKind.Object, hubCounters.ValueKind);

        foreach (var property in hubCounters.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Number, property.Value.ValueKind);
            if (stored.TryGetProperty(property.Name, out var storedValue))
            {
                var hubValue = property.Value.GetInt64();
                Assert.True(hubValue >= 0 && hubValue <= storedValue.GetInt64(),
                    $"hub counters.{property.Name}={hubValue} outside [0, stored={storedValue.GetInt64()}] (throttled snapshot must not exceed the final stored total)");
            }
        }

        var hubFetched = hubCounters.GetProperty("fetched").GetInt64();
        Assert.True(hubFetched > 0 && hubFetched <= stored.GetProperty("fetched").GetInt64(),
            "hub progress must report fetched URLs bounded by the stored total");
    }

    /// <summary>
    /// G-HUB-COUNTERS (WP-09-06 family): hub crawl-progress counters must match
    /// the REST wire counters key-for-key so the shell's live view and the REST
    /// snapshot agree. UNSKIPPED by lead (WP-09-11): WP-09-06's HubPayloadNormalizer
    /// reconciles hub counters to the REST wire keys at the Api hub boundary.
    /// </summary>
    [Fact]
    public async Task HubCounters_MatchRestWireKeys()
    {
        var data = await Rest.GetDataAsync(_fixture.Http, $"/api/audits/{_fixture.AuditId}");
        var wire = data.GetProperty("counters");
        var progress = _fixture.Hub.EventsNamed("crawl-progress").Last();
        var hubCounters = progress.Payload.GetProperty("counters");
        foreach (var key in new[] { "queued", "fetched", "error", "blocked" })
        {
            Assert.True(hubCounters.TryGetProperty(key, out _), $"hub counters missing REST wire key '{key}'");
        }

        Assert.Equal(wire.GetProperty("fetched").GetInt64(), hubCounters.GetProperty("fetched").GetInt64());
    }

    private async Task<JsonElement> ReadEngineCountersAsync()
    {
        var countersJson = (string?)_fixture.Db.Scalar(
            "SELECT counters_json FROM audits WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.AuditId });
        Assert.False(string.IsNullOrEmpty(countersJson), "fixture audit must have stored counters_json");
        await Task.CompletedTask;
        using var document = JsonDocument.Parse(countersJson!);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Id comparison tolerant of the G-G number-vs-string drift: JsonElement
    /// .ToString() yields the raw token text for both kinds, so the filter
    /// keeps working when the producer switches to string EntityIds.
    /// </summary>
    private static bool MatchesId(JsonElement payload, string property, long expected) =>
        payload.TryGetProperty(property, out var id)
            && id.ToString() == expected.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
