using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.ListMode;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// FS-CRAWL-002 §10.3/§10.6/§10.8 persistence evidence over the real SQLite
/// stack: redirect-matrix verdict aggregates in metric_summaries (§8.1
/// idempotent pattern), offline re-audit readability without any fetch,
/// matrix-row-validated hub batches with the at-most-once-per-second throttle
/// contract, and staging↔production normalized comparison.
/// </summary>
public class MatrixPersistenceTests
{
    private const string Source = "https://old.example.com/a";
    private const string Expected = "https://new.example.com/a";

    private static RedirectMappingDto Map(long auditId, int ordinal = 0, string? source = null, string? expected = null) =>
        new(ordinal + 1, auditId, source ?? Source, expected ?? Expected, ordinal);

    private static ScriptedHttpFetcher PassingFetcher() =>
        new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200));

    /// <summary>Verdict aggregates persist under the redirect-matrix metric key,
    /// dimensioned by verdict, with all five dimensions present.</summary>
    [Fact]
    public async Task Aggregates_Persist_By_Verdict_Dimension()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = PassingFetcher()
            .Script("https://old.example.com/b", ScriptedHttpFetcher.Response("https://old.example.com/b", 301, location: "https://wrong.example.com/b"))
            .Script("https://wrong.example.com/b", ScriptedHttpFetcher.Response("https://wrong.example.com/b", 200));
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), new RecordingHub());

        await validator.ValidateAsync(auditId, [
            Map(auditId, 0),
            Map(auditId, 1, source: "https://old.example.com/b"),
        ]);

        var aggregates = await RedirectMatrixResults.ReadAggregatesAsync(fixture.Provider, auditId);
        Assert.Equal(5, aggregates.Count);
        Assert.Equal(1, aggregates.Single(a => a.Verdict == RedirectMatrixVerdict.Pass).Count);
        Assert.Equal(1, aggregates.Single(a => a.Verdict == RedirectMatrixVerdict.TargetMismatch).Count);
        Assert.Equal(0, aggregates.Single(a => a.Verdict == RedirectMatrixVerdict.ChainExceeded).Count);

        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `metric_summaries` WHERE `audit_id` = @auditId AND `metric_key` = 'redirect-matrix'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        Assert.Equal(5L, (long)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>Re-running validation refreshes aggregates idempotently — never double-counted.</summary>
    [Fact]
    public async Task Aggregate_Refresh_Is_Idempotent()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var firstRun = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), new RecordingHub());
        var secondRun = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), new RecordingHub());

        await firstRun.ValidateAsync(auditId, [Map(auditId)]);
        await secondRun.ValidateAsync(auditId, [Map(auditId)]);

        var aggregates = await RedirectMatrixResults.ReadAggregatesAsync(fixture.Provider, auditId);
        Assert.Equal(1, aggregates.Single(a => a.Verdict == RedirectMatrixVerdict.Pass).Count);

        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `metric_summaries` WHERE `audit_id` = @auditId AND `metric_key` = 'redirect-matrix'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        Assert.Equal(5L, (long)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>Results stay readable offline on re-audit: a fresh provider over
    /// the same file reads aggregates and findings with no fetcher involved.</summary>
    [Fact]
    public async Task Results_Readable_Offline_On_Reaudit()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), new RecordingHub());
        await validator.ValidateAsync(auditId, [Map(auditId)]);

        // Simulate a later app session: a brand-new provider instance over the
        // same database file; no fetcher, no hub, no network.
        using var reopened = new SqliteDbProvider(new SqliteProviderOptions { DatabasePath = fixture.Provider.DatabasePath });
        var aggregates = await RedirectMatrixResults.ReadAggregatesAsync(reopened, auditId);
        var findings = await RedirectMatrixResults.ReadFindingsAsync(reopened, auditId);

        Assert.Equal(1, aggregates.Single(a => a.Verdict == RedirectMatrixVerdict.Pass).Count);
        var finding = Assert.Single(findings);
        Assert.Equal(FindingRuleKeys.RedirectMatrixPass, finding.RuleKey);
        var evidence = JsonSerializer.Deserialize<MatrixRowEvidence>(finding.EvidenceJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal(Source, evidence!.SourceUrl);
        Assert.Equal(Expected, evidence.ActualFinalUrl);
        Assert.Single(evidence.Chain);
    }

    /// <summary>Hub emission carries the matrix-row-validated contract: audit id,
    /// (urlId, sourceUrl, verdict) rows, and the 1-second throttle metadata.</summary>
    [Fact]
    public async Task Hub_Emission_Carries_Matrix_Row_Validated_Contract()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());
        await ingestor.IngestAsync(auditId, [Source], allowDiscovery: false);
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), hub);

        await validator.ValidateAsync(auditId, [Map(auditId)]);

        var hubEvent = Assert.Single(hub.Events);
        Assert.Equal(MatrixHubContract.EventName, hubEvent.Name);
        Assert.Equal(TimeSpan.FromSeconds(1), hubEvent.ThrottleInterval);
        Assert.Equal(MatrixHubContract.ThrottleKey(auditId), hubEvent.ThrottleKey);

        var payload = JsonSerializer.Deserialize<MatrixRowValidatedPayload>(hubEvent.PayloadJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        // G-MATRIX-VERDICT: ids and verdicts are strings (frontend EntityId contract).
        Assert.Equal(auditId.ToString(System.Globalization.CultureInfo.InvariantCulture), payload!.AuditId);
        var item = Assert.Single(payload.Rows);
        Assert.Equal(Source, item.SourceUrl);
        Assert.Equal(MatrixVerdictNames.Of(RedirectMatrixVerdict.Pass), item.Verdict);
        Assert.True(long.TryParse(item.UrlId, out var urlId) && urlId > 0,
            "The seeded source URL must resolve to its url_records identifier.");
    }

    /// <summary>Large matrices emit in bounded batches, each throttled.</summary>
    [Fact]
    public async Task Large_Matrix_Emits_Throttled_Batches()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var rowCount = MatrixHubContract.BatchSize + 5;
        var fetcher = new ScriptedHttpFetcher
        {
            DefaultResponse = ScriptedHttpFetcher.Response(Expected, 200),
        };
        var mappings = new List<RedirectMappingDto>();
        for (var i = 0; i < rowCount; i++)
        {
            var source = $"https://old.example.com/page-{i}";
            fetcher.Script(source, ScriptedHttpFetcher.Response(source, 301, location: Expected));
            mappings.Add(Map(auditId, i, source: source));
        }

        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), hub);

        await validator.ValidateAsync(auditId, mappings);

        Assert.Equal(2, hub.Events.Count);
        Assert.All(hub.Events, e =>
        {
            Assert.Equal(MatrixHubContract.EventName, e.Name);
            Assert.Equal(TimeSpan.FromSeconds(1), e.ThrottleInterval);
        });
        var first = JsonSerializer.Deserialize<MatrixRowValidatedPayload>(hub.Events[0].PayloadJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var second = JsonSerializer.Deserialize<MatrixRowValidatedPayload>(hub.Events[1].PayloadJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal(MatrixHubContract.BatchSize, first!.Rows.Count);
        Assert.Equal(5, second!.Rows.Count);
    }

    /// <summary>Normalized strictness with host mapping passes staging→production
    /// rows that exact strictness verdicts as target-mismatch.</summary>
    [Theory]
    [InlineData(MatrixComparisonStrictness.Exact, RedirectMatrixVerdict.TargetMismatch)]
    [InlineData(MatrixComparisonStrictness.Normalized, RedirectMatrixVerdict.Pass)]
    public async Task Host_Mapped_Normalized_Comparison(MatrixComparisonStrictness strictness, RedirectMatrixVerdict expectedVerdict)
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        const string stagingFinal = "https://staging.example.com/a";
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: stagingFinal))
            .Script(stagingFinal, ScriptedHttpFetcher.Response(stagingFinal, 200));
        var options = new ListModeOptions(
            Strictness: strictness,
            HostMappings: [new HostMappingRule("staging.example.com", "new.example.com")]);
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), new RecordingHub(), options);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        Assert.Equal(expectedVerdict, Assert.Single(results).Verdict);
    }

    /// <summary>The Link header canonical wins over the HTML canonical element.</summary>
    [Fact]
    public async Task Link_Header_Canonical_Takes_Precedence()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200,
                body: "<html><head><link rel=\"canonical\" href=\"https://html.example.com/z\"></head></html>",
                extraHeaders: new Dictionary<string, string> { ["Link"] = $"<{Expected}>; rel=\"canonical\"" }));
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), new RecordingHub());

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.Pass, row.Verdict);
        Assert.Equal(Expected, row.CanonicalUrl);
    }

    /// <summary>Relative canonicals resolve against the final URL.</summary>
    [Fact]
    public async Task Relative_Canonical_Resolves_Against_Final_Url()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200,
                body: "<html><head><link rel=\"canonical\" href=\"/a\"></head></html>"));
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), new RecordingHub());

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.Pass, row.Verdict);
        Assert.Equal(Expected, row.CanonicalUrl);
    }

    /// <summary>Findings reference the seeded source URL record when present.</summary>
    [Fact]
    public async Task Findings_Reference_Seeded_Url_Record()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());
        await ingestor.IngestAsync(auditId, [Source], allowDiscovery: false);
        var validator = new RedirectMatrixValidator(PassingFetcher(), fixture.Provider, new TestCanonicalizer(), new RecordingHub());

        await validator.ValidateAsync(auditId, [Map(auditId)]);

        var finding = Assert.Single(await RedirectMatrixResults.ReadFindingsAsync(fixture.Provider, auditId));
        Assert.NotNull(finding.UrlId);
        Assert.True(finding.UrlId > 0);
    }
}
