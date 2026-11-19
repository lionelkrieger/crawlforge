using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.ListMode;
using CrawlForge.Crawler.Traps;
using CrawlForge.Crawler.Tests.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.Traps;

/// <summary>
/// FS-CRAWL-001 §5.14 finding persistence over the real SQLite stack: a
/// flagged pattern is raised as a finding naming the pattern, its URL count
/// and examples — additive rule key crawler-trap.detected, crawl-health
/// category, Warning severity, idempotent writes, sibling findings untouched.
/// </summary>
public class TrapFindingPersistenceTests
{
    private static TrapDetection Detection(string patternKey = "https://fixture.example/trap/filter") =>
        new(
            patternKey,
            TrapPatternFamily.ParameterCombination,
            UrlCount: 144,
            AdmittedUrlCount: 26,
            ObservedContentCount: 26,
            NearDuplicateCount: 25,
            NearDuplicateRatio: 25d / 26d,
            Examples: [patternKey + "?color=red", patternKey + "?color=blue"],
            DetectedAtUtc: new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Persist_WritesFinding_WithRuleKeyCategorySeverityAndEvidence()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();

        var written = await CrawlerTrapFindings.PersistAsync(fixture.Provider, auditId, [Detection()]);

        Assert.Equal(1, written);
        var row = Assert.Single(await CrawlerTrapFindings.ReadFindingsAsync(fixture.Provider, auditId));
        Assert.Equal(CrawlerTrapRuleKeys.TrapDetected, row.RuleKey);
        Assert.Equal("crawler-trap.detected", row.RuleKey);
        Assert.Equal(FindingSeverity.Warning, row.Severity);

        var evidence = JsonSerializer.Deserialize<TrapDetection>(row.EvidenceJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(evidence);
        Assert.Equal("https://fixture.example/trap/filter", evidence!.PatternKey);
        Assert.Equal(144, evidence.UrlCount);
        Assert.Equal(2, evidence.Examples.Count);
        Assert.Equal(TrapPatternFamily.ParameterCombination, evidence.Family);

        // Category is the crawl-health category (AuditCategory.Crawl = 6).
        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `category`, `url_id` FROM `findings` WHERE `audit_id` = @auditId AND `rule_key` = 'crawler-trap.detected'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((int)AuditCategory.Crawl, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1)); // site-wide finding: no single URL reference.
    }

    [Fact]
    public async Task Persist_IsIdempotent_ReplacesPriorTrapFindings()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();

        await CrawlerTrapFindings.PersistAsync(fixture.Provider, auditId, [Detection(), Detection("https://fixture.example/trap/calendar/{date}")]);
        await CrawlerTrapFindings.PersistAsync(fixture.Provider, auditId, [Detection()]);

        var rows = await CrawlerTrapFindings.ReadFindingsAsync(fixture.Provider, auditId);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Persist_DoesNotTouchSiblingFindingFamilies()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();

        // A sibling redirect-matrix finding exists first.
        var mapping = new Contracts.Entities.RedirectMappingDto(1, auditId, "https://old.example/a", "https://new.example/a", 0);
        var matrixResult = new Contracts.Crawl.RedirectMatrixRowResult(
            mapping, RedirectMatrixVerdict.Pass, "https://new.example/a", 1,
            [new Contracts.Crawl.RedirectHop(0, "https://old.example/a", "https://new.example/a", 301)], null);
        await using (var connection = await fixture.OpenConnectionAsync())
        {
            await using var transaction = connection.BeginTransaction();
            await RedirectMatrixResults.PersistFindingAsync(connection, transaction, auditId, matrixResult, 0, 200, null);
            transaction.Commit();
        }

        await CrawlerTrapFindings.PersistAsync(fixture.Provider, auditId, [Detection()]);

        await using var verification = await fixture.OpenConnectionAsync();
        await using var command = verification.CreateCommand();
        command.CommandText = "SELECT `rule_key` FROM `findings` WHERE `audit_id` = @auditId ORDER BY `id`";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }

        Assert.Equal(2, keys.Count);
        Assert.Contains("redirect-matrix.pass", keys);
        Assert.Contains("crawler-trap.detected", keys);
    }

    [Fact]
    public async Task ReadFindings_Offline_RequiresNoFetch()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        await CrawlerTrapFindings.PersistAsync(fixture.Provider, auditId, [Detection()]);

        // Reading is a pure database read — no fetcher is involved anywhere.
        var rows = await CrawlerTrapFindings.ReadFindingsAsync(fixture.Provider, auditId);
        Assert.Single(rows);
        Assert.Empty(await CrawlerTrapFindings.ReadFindingsAsync(fixture.Provider, auditId + 999));
    }
}
