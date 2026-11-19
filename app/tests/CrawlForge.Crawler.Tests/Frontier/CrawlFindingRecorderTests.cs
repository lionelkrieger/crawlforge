using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Tests.Frontier.Support;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// Finding persistence (CrawlFindingRecorder): transactional inserts through
/// the locked IDbProvider contract, idempotent per (audit, rule key, URL)
/// including site-wide NULL-URL findings, read back through the real
/// repository stack.
/// </summary>
public sealed class CrawlFindingRecorderTests
{
    private const long AuditId = 1;

    private static FindingDto Draft(string ruleKey, long? urlId, string evidence = "{\"url\":\"http://site.example/\"}") =>
        new(0, AuditId, ruleKey, AuditCategory.Indexability, FindingSeverity.Warning, urlId, evidence, null, false);

    [Fact]
    public async Task Record_DraftsPersistedWithAllFields()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var urlId = await database.InsertUrlAsync(AuditId, "http://site.example/");
        var recorder = new CrawlFindingRecorder(database.Provider);
        var drafts = new[]
        {
            Draft(LinkRuleKeys.Orphan, urlId, "{\"inSitemap\":true}"),
            new FindingDto(0, AuditId, "sitemap.missing-url", AuditCategory.Indexability, FindingSeverity.Warning, null, "{\"reason\":\"not-crawled\"}", "fix-sitemap", false),
        };

        var inserted = await recorder.RecordAsync(AuditId, drafts);

        Assert.Equal(2, inserted);
        var repository = new SqlRepository<FindingDto>(database.Provider);
        var stored = await repository.ListAsync(new ListQuery(0, 50, new Dictionary<string, string> { ["auditId"] = AuditId.ToString() }));
        Assert.Equal(2, stored.TotalCount);

        FindingDto orphan = stored.Items.Single(f => f.RuleKey == LinkRuleKeys.Orphan);
        Assert.Equal(urlId, orphan.UrlId);
        Assert.Equal(AuditCategory.Indexability, orphan.Category);
        Assert.Equal(FindingSeverity.Warning, orphan.Severity);
        Assert.Equal("{\"inSitemap\":true}", orphan.EvidenceJson);
        Assert.False(orphan.IsSuppressed);

        FindingDto siteWide = stored.Items.Single(f => f.RuleKey == "sitemap.missing-url");
        Assert.Null(siteWide.UrlId);
        Assert.Equal("fix-sitemap", siteWide.RecommendationKey);
    }

    [Fact]
    public async Task Record_DuplicateUrlFinding_NotInsertedTwice()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var urlId = await database.InsertUrlAsync(AuditId, "http://site.example/");
        var recorder = new CrawlFindingRecorder(database.Provider);

        Assert.Equal(1, await recorder.RecordAsync(AuditId, new[] { Draft(LinkRuleKeys.Orphan, urlId) }));
        Assert.Equal(0, await recorder.RecordAsync(AuditId, new[] { Draft(LinkRuleKeys.Orphan, urlId) }));
        // Different rule key on the same URL is a distinct finding.
        Assert.Equal(1, await recorder.RecordAsync(AuditId, new[] { Draft("sitemap.redirecting", urlId) }));
    }

    [Fact]
    public async Task Record_DuplicateSiteWideFinding_NullUrlDedup()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var recorder = new CrawlFindingRecorder(database.Provider);

        Assert.Equal(1, await recorder.RecordAsync(AuditId, new[] { Draft("sitemap.missing-url", null) }));
        Assert.Equal(0, await recorder.RecordAsync(AuditId, new[] { Draft("sitemap.missing-url", null) }));
    }

    [Fact]
    public async Task Record_DifferentAudits_Isolated()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        await database.SeedAuditAsync(2);
        var urlOne = await database.InsertUrlAsync(AuditId, "http://site.example/");
        var urlTwo = await database.InsertUrlAsync(2, "http://site.example/");
        var recorder = new CrawlFindingRecorder(database.Provider);

        Assert.Equal(1, await recorder.RecordAsync(AuditId, new[] { Draft(LinkRuleKeys.Orphan, urlOne) }));
        Assert.Equal(1, await recorder.RecordAsync(2, new[] { new FindingDto(0, 2, LinkRuleKeys.Orphan, AuditCategory.Indexability, FindingSeverity.Warning, urlTwo, "{}", null, false) }));
    }

    [Fact]
    public async Task Record_EmptyList_ZeroInserted()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        var recorder = new CrawlFindingRecorder(database.Provider);

        Assert.Equal(0, await recorder.RecordAsync(AuditId, Array.Empty<FindingDto>()));
    }

    [Fact]
    public async Task Record_ConcurrentBatches_ThreadSafeAndIdempotent()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var urlId = await database.InsertUrlAsync(AuditId, "http://site.example/");
        var recorder = new CrawlFindingRecorder(database.Provider);
        var drafts = new[] { Draft(LinkRuleKeys.Orphan, urlId) };

        int[] inserted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => recorder.RecordAsync(AuditId, drafts))));

        Assert.Equal(1, inserted.Sum());
        var repository = new SqlRepository<FindingDto>(database.Provider);
        var stored = await repository.ListAsync(new ListQuery(0, 50, new Dictionary<string, string> { ["auditId"] = AuditId.ToString() }));
        Assert.Equal(1, stored.TotalCount);
    }
}
