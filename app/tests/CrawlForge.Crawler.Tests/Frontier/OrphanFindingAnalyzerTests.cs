using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Tests.Frontier.Support;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// Orphan identification with findings (CAP-B-048, CAP-E-122) on the real
/// SQLite stack: zero internal in-links (self-loops excluded), entry-point
/// exclusion, sitemap-membership evidence, and producer→consumer persistence
/// through CrawlFindingRecorder + SqlRepository.
/// </summary>
public sealed class OrphanFindingAnalyzerTests
{
    private const long AuditId = 1;
    private const string Base = "http://site.example";

    private static async Task<(FrontierTestDatabase Db, long Home, long Linked, long Orphan, long SelfLoop)> SeedGraphAsync()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await database.SeedAuditAsync(AuditId);
        var home = await database.InsertUrlAsync(AuditId, Base + "/", DiscoverySource.Seed, CrawlStatusClass.Success, 200);
        var linked = await database.InsertUrlAsync(AuditId, Base + "/linked", DiscoverySource.Link, CrawlStatusClass.Success, 200);
        var orphan = await database.InsertUrlAsync(AuditId, Base + "/pages/orphan.html", DiscoverySource.Sitemap, CrawlStatusClass.Success, 200);
        var selfLoop = await database.InsertUrlAsync(AuditId, Base + "/self-loop", DiscoverySource.Link, CrawlStatusClass.Success, 200);
        await database.InsertLinkEdgeAsync(AuditId, home, Base + "/linked", linked, isInternal: true);
        await database.InsertLinkEdgeAsync(AuditId, selfLoop, Base + "/self-loop", selfLoop, isInternal: true);
        return (database, home, linked, orphan, selfLoop);
    }

    [Fact]
    public async Task Analyze_ZeroInlinkUrls_OrphanFindingsWithEvidence()
    {
        var (database, _, _, orphan, selfLoop) = await SeedGraphAsync();
        await using var _ = database;
        var analyzer = new OrphanFindingAnalyzer(database.Provider);

        IReadOnlyList<FindingDto> findings = await analyzer.AnalyzeAsync(AuditId, new[] { Base + "/pages/orphan.html" });

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal(LinkRuleKeys.Orphan, f.RuleKey);
            Assert.Equal(AuditCategory.Indexability, f.Category);
            Assert.Equal(FindingSeverity.Warning, f.Severity);
        });

        FindingDto orphanFinding = findings.Single(f => f.UrlId == orphan);
        using (var evidence = JsonDocument.Parse(orphanFinding.EvidenceJson))
        {
            Assert.Equal(Base + "/pages/orphan.html", evidence.RootElement.GetProperty("url").GetString());
            Assert.True(evidence.RootElement.GetProperty("inSitemap").GetBoolean());
            Assert.Equal("Sitemap", evidence.RootElement.GetProperty("discoverySource").GetString());
            Assert.Equal(0, evidence.RootElement.GetProperty("internalInlinkCount").GetInt32());
        }

        FindingDto selfLoopFinding = findings.Single(f => f.UrlId == selfLoop);
        using var selfEvidence = JsonDocument.Parse(selfLoopFinding.EvidenceJson);
        Assert.False(selfEvidence.RootElement.GetProperty("inSitemap").GetBoolean());
    }

    [Fact]
    public async Task Analyze_EntryPointsExcludedByDefault()
    {
        var (database, home, _, _, _) = await SeedGraphAsync();
        await using var _ = database;
        var analyzer = new OrphanFindingAnalyzer(database.Provider);

        IReadOnlyList<FindingDto> findings = await analyzer.AnalyzeAsync(AuditId);

        Assert.DoesNotContain(findings, f => f.UrlId == home);
    }

    [Fact]
    public async Task Analyze_EntryPointInclusionOption_ReportsUnlinkedSeed()
    {
        var (database, home, _, _, _) = await SeedGraphAsync();
        await using var _ = database;
        var analyzer = new OrphanFindingAnalyzer(database.Provider);

        IReadOnlyList<FindingDto> findings = await analyzer.AnalyzeAsync(AuditId, options: new OrphanDetectionOptions(ExcludeEntryPoints: false));

        Assert.Contains(findings, f => f.UrlId == home);
    }

    [Fact]
    public async Task Analyze_ExternalInlinksDoNotCount()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var source = await database.InsertUrlAsync(AuditId, Base + "/", DiscoverySource.Seed, CrawlStatusClass.Success, 200);
        var target = await database.InsertUrlAsync(AuditId, Base + "/target", DiscoverySource.Sitemap, CrawlStatusClass.Success, 200);
        // An external edge pointing at the same canonical URL must not count
        // as an internal in-link (is_internal = 0).
        await database.InsertLinkEdgeAsync(AuditId, source, Base + "/target", target, isInternal: false);
        var analyzer = new OrphanFindingAnalyzer(database.Provider);

        IReadOnlyList<FindingDto> findings = await analyzer.AnalyzeAsync(AuditId);

        Assert.Contains(findings, f => f.UrlId == target);
    }

    [Fact]
    public async Task Analyze_FindingsPersistedAndReadBackThroughRepository()
    {
        var (database, _, _, orphan, _) = await SeedGraphAsync();
        await using var _ = database;
        var analyzer = new OrphanFindingAnalyzer(database.Provider);
        IReadOnlyList<FindingDto> drafts = await analyzer.AnalyzeAsync(AuditId, new[] { Base + "/pages/orphan.html" });

        var recorder = new CrawlFindingRecorder(database.Provider);
        var inserted = await recorder.RecordAsync(AuditId, drafts);
        Assert.Equal(2, inserted);

        // Storage producer → repository consumer.
        var repository = new SqlRepository<FindingDto>(database.Provider);
        var stored = await repository.ListAsync(new ListQuery(0, 50, new Dictionary<string, string>
        {
            ["auditId"] = AuditId.ToString(),
            ["ruleKey"] = LinkRuleKeys.Orphan,
        }));
        Assert.Equal(2, stored.TotalCount);
        FindingDto orphanRow = stored.Items.Single(f => f.UrlId == orphan);
        Assert.Equal(AuditCategory.Indexability, orphanRow.Category);
        Assert.Equal(FindingSeverity.Warning, orphanRow.Severity);
        Assert.Contains("orphan.html", orphanRow.EvidenceJson);

        // Idempotent re-run: no duplicates.
        Assert.Equal(0, await recorder.RecordAsync(AuditId, drafts));
    }
}
