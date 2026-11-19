using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// G-MATRIX-VERDICT component-side fix (PHASE-08-REPORT §4.9): the
/// matrix-row-validated hub payload must match the frozen frontend entry
/// (contracts/hub/events.ts:180-184 — urlId: EntityId, sourceUrl: string,
/// verdict: string; payload auditId: EntityId). These tests assert the REAL
/// producer JSON value kinds and the §10.3/§10.6 verdict dimension vocabulary
/// shared with the metric aggregates — the same shape the coordinator's
/// skipped MatrixRowValidated_FrontendContractShape conformance test locks.
/// </summary>
public class MatrixWireShapeTests
{
    private const string Source = "https://old.example.com/a";
    private const string Expected = "https://new.example.com/a";

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static RedirectMappingDto Map(long auditId, int ordinal = 0, string? source = null, string? expected = null) =>
        new(ordinal + 1, auditId, source ?? Source, expected ?? Expected, ordinal);

    private static ScriptedHttpFetcher PassingFetcher() =>
        new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200));

    [Fact]
    public async Task HubPayload_IdsAndVerdicts_AreStrings()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());
        await ingestor.IngestAsync(auditId, [Source], allowDiscovery: false);
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), hub);

        await validator.ValidateAsync(auditId, [Map(auditId)]);

        var hubEvent = Assert.Single(hub.Events);
        using var payload = JsonDocument.Parse(hubEvent.PayloadJson);

        // Frozen frontend payload contract: auditId is a string EntityId.
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("auditId").ValueKind);
        Assert.Equal(auditId.ToString(System.Globalization.CultureInfo.InvariantCulture), payload.RootElement.GetProperty("auditId").GetString());

        var row = Assert.Single(payload.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.String, row.GetProperty("urlId").ValueKind);
        Assert.Equal(JsonValueKind.String, row.GetProperty("sourceUrl").ValueKind);
        Assert.Equal(JsonValueKind.String, row.GetProperty("verdict").ValueKind);
        Assert.Equal(Source, row.GetProperty("sourceUrl").GetString());
        Assert.Equal("pass", row.GetProperty("verdict").GetString());
        Assert.True(long.TryParse(row.GetProperty("urlId").GetString(), out var urlId) && urlId > 0);
    }

    [Fact]
    public async Task HubPayload_AbsentUrlRecord_IsEmptyString()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), hub);

        await validator.ValidateAsync(auditId, [Map(auditId)]);

        var hubEvent = Assert.Single(hub.Events);
        using var payload = JsonDocument.Parse(hubEvent.PayloadJson);
        var row = Assert.Single(payload.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.String, row.GetProperty("urlId").ValueKind);
        Assert.Equal(string.Empty, row.GetProperty("urlId").GetString());
    }

    [Fact]
    public async Task HubPayload_VerdictVocabulary_MatchesSpecDimensionNames()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        const string wrong = "https://wrong.example.com/a";
        const string deadEnd = "https://wrong.example.com/d";
        const string mismatchSource = "https://old.example.com/b";
        const string canonicalSource = "https://old.example.com/c";
        const string non200Source = "https://old.example.com/d";
        const string chainSource = "https://old.example.com/e";
        const string loopB = "https://old.example.com/f";

        var fetcher = new ScriptedHttpFetcher()
            .Script(mismatchSource, ScriptedHttpFetcher.Response(mismatchSource, 301, location: wrong))
            .Script(wrong, ScriptedHttpFetcher.Response(wrong, 200))
            .Script(canonicalSource, ScriptedHttpFetcher.Response(canonicalSource, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200,
                body: "<html><head><link rel=\"canonical\" href=\"https://other.example.com/z\"></head></html>"))
            .Script(non200Source, ScriptedHttpFetcher.Response(non200Source, 301, location: deadEnd))
            .Script(deadEnd, ScriptedHttpFetcher.Response(deadEnd, 500))
            .Script(chainSource, ScriptedHttpFetcher.Response(chainSource, 302, location: loopB))
            .Script(loopB, ScriptedHttpFetcher.Response(loopB, 302, location: chainSource));
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), hub,
            new ListModeOptions(RedirectHopLimit: 1));

        await validator.ValidateAsync(auditId, [
            Map(auditId, 0, source: mismatchSource),
            Map(auditId, 1, source: canonicalSource, expected: Expected),
            Map(auditId, 2, source: non200Source),
            Map(auditId, 3, source: chainSource),
        ]);

        using var payload = JsonDocument.Parse(Assert.Single(hub.Events).PayloadJson);
        var verdicts = payload.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("verdict").GetString())
            .ToList();

        Assert.Equal(["target-mismatch", "canonical-mismatch", "final-non-200", "chain-exceeded"], verdicts);

        // The vocabulary is exactly the metric-aggregate dimension vocabulary.
        foreach (var verdict in verdicts)
        {
            Assert.NotNull(verdict);
            _ = MatrixVerdictNames.Parse(verdict!); // throws on unknown names
        }
    }

    [Fact]
    public async Task HubPayload_RoundTrips_ThroughTheProducerRecord()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), hub);

        await validator.ValidateAsync(auditId, [Map(auditId)]);

        var payload = JsonSerializer.Deserialize<MatrixRowValidatedPayload>(
            Assert.Single(hub.Events).PayloadJson, CamelCase);
        Assert.NotNull(payload);
        Assert.Equal(auditId.ToString(System.Globalization.CultureInfo.InvariantCulture), payload!.AuditId);
        var row = Assert.Single(payload.Rows);
        Assert.Equal("pass", row.Verdict);
        Assert.Equal(Source, row.SourceUrl);
    }

    [Fact]
    public async Task HubPayload_PassRow_CarriesSeededUrlId()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var frontier = new RecordingFrontier();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), frontier);
        await ingestor.IngestAsync(auditId, [Source], allowDiscovery: false);
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), hub);

        await validator.ValidateAsync(auditId, [Map(auditId)]);

        var payload = JsonSerializer.Deserialize<MatrixRowValidatedPayload>(
            Assert.Single(hub.Events).PayloadJson, CamelCase);
        await using var connection = await fixture.OpenConnectionAsync();
        var seededUrlId = await RedirectMatrixResults.FindUrlIdAsync(connection, auditId, Source);
        Assert.True(seededUrlId > 0);
        Assert.Equal(seededUrlId.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(payload!.Rows).UrlId);
    }
}
