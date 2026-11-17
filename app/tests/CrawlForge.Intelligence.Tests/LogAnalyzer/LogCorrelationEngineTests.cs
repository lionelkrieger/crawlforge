using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.Tests.LogAnalyzer.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer;

/// <summary>
/// Correlation behavior (FS-CRAWL-002 §11.5): the three views keyed on
/// canonical URL, per-class evidence, finding drafts in the Log Intelligence
/// category, metric contributions, out-of-scope orphan separation, and paged
/// read-model projection. Real persistence stack, fully offline.
/// </summary>
public sealed class LogCorrelationEngineTests
{
    private sealed class CorrelationFixture : IAsyncDisposable
    {
        public LogAnalyzerTestDatabase Database { get; private set; } = null!;
        public long ImportId { get; private set; }
        public LogCorrelationEngine Engine { get; private set; } = null!;

        public static async Task<CorrelationFixture> CreateAsync()
        {
            var fixture = new CorrelationFixture();
            fixture.Database = await LogAnalyzerTestDatabase.CreateAsync();
            await fixture.Database.SeedProjectAndAuditAsync(1, 1);

            // Crawled URLs: home, page-a, page-b indexable; noindex-page not; never-hit indexable.
            var home = await fixture.Database.InsertUrlAsync(1, "https://example.com/", depth: 0);
            var pageA = await fixture.Database.InsertUrlAsync(1, "https://example.com/page-a", depth: 1);
            var pageB = await fixture.Database.InsertUrlAsync(1, "https://example.com/page-b", depth: 1);
            await fixture.Database.InsertUrlAsync(1, "https://example.com/noindex-page", depth: 2, isIndexable: false);
            var neverHit = await fixture.Database.InsertUrlAsync(1, "https://example.com/never-hit", depth: 3);
            await fixture.Database.InsertEdgeAsync(1, home, pageA, "https://example.com/page-a");
            await fixture.Database.InsertEdgeAsync(1, home, pageB, "https://example.com/page-b");
            await fixture.Database.InsertEdgeAsync(1, pageA, neverHit, "https://example.com/never-hit");

            // Log hits: waste candidates, orphans, out-of-scope, human noise.
            var lines = new List<string>();
            void Hits(string path, int count, string ua, int status = 200, string ip = "66.249.66.1")
            {
                for (var i = 0; i < count; i++)
                {
                    lines.Add(LogSamples.CombinedLine(ip, "10/Oct/2026:13:55:36 +0000", "GET", path, status, "100", ua));
                }
            }

            Hits("/page-a", 12, LogSamples.GooglebotDesktopUa);
            Hits("/noindex-page", 12, LogSamples.GooglebotDesktopUa);
            Hits("/error-page", 12, LogSamples.GooglebotDesktopUa, status: 404);
            Hits("/trap?x=1", 12, LogSamples.GooglebotDesktopUa);
            Hits("/orphan-page", 3, LogSamples.GooglebotDesktopUa);
            Hits("/page-b", 2, LogSamples.BingbotUa, ip: "157.55.39.10");
            Hits("https://other.example/foreign", 5, LogSamples.GooglebotDesktopUa);
            Hits("/page-c", 1, LogSamples.HumanUa, ip: "203.0.113.5");
            var logPath = fixture.Database.WriteLogFile("correlation.log", string.Join('\n', lines));

            var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());
            var service = new LogImportService(
                fixture.Database.Provider,
                new LogFileParser(),
                new BotVerifier(catalogue),
                new UrlCanonicalizer(),
                new RecordingHub(),
                catalogue);
            var outcome = await service.ImportAsync(new LogImportRequest(1, logPath, "https://example.com"));
            Assert.Equal(LogImportStatus.Completed, outcome.Status);
            fixture.ImportId = outcome.ImportId;
            fixture.Engine = new LogCorrelationEngine(fixture.Database.Provider);
            return fixture;
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    [Fact]
    public async Task TrueOrphansAreSearchBotHitUrlsAbsentFromTheCrawl()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var result = await fixture.Engine.CorrelateAsync(1, fixture.ImportId);

        Assert.Equal(3, result.TrueOrphans.Count);
        Assert.Contains(result.TrueOrphans, r => r.CanonicalUrl == "https://example.com/orphan-page" && r.HitCount == 3);
        Assert.Contains(result.TrueOrphans, r => r.CanonicalUrl == "https://example.com/error-page" && r.HitCount == 12);
        Assert.Contains(result.TrueOrphans, r => r.CanonicalUrl == "https://example.com/trap?x=1");
        Assert.DoesNotContain(result.TrueOrphans, r => r.CanonicalUrl == "https://example.com/page-a");
        Assert.DoesNotContain(result.TrueOrphans, r => r.CanonicalUrl == "https://example.com/page-c");
        Assert.All(result.TrueOrphans, r => Assert.Equal("Googlebot Desktop", r.BotFamily));
    }

    [Fact]
    public async Task BudgetWasteAppliesThresholdAndLowValueFacets()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var result = await fixture.Engine.CorrelateAsync(1, fixture.ImportId);

        Assert.Equal(3, result.BudgetWaste.Count);
        var urls = result.BudgetWaste.Select(r => r.CanonicalUrl).ToList();
        Assert.Contains("https://example.com/noindex-page", urls);   // non-indexable facet
        Assert.Contains("https://example.com/error-page", urls);    // non-2xx facet
        Assert.Contains("https://example.com/trap?x=1", urls);      // query-trap fallback facet
        // page-a has 12 search hits but is indexable, 2xx, query-free: not waste.
        Assert.DoesNotContain("https://example.com/page-a", urls);
        // orphan-page has only 3 search hits: below threshold.
        Assert.DoesNotContain("https://example.com/orphan-page", urls);
    }

    [Fact]
    public async Task UncrawledIndexableRequiresZeroSearchBotHitsAndIndexability()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var result = await fixture.Engine.CorrelateAsync(1, fixture.ImportId);

        Assert.Equal(2, result.UncrawledIndexable.Count);
        var urls = result.UncrawledIndexable.Select(r => r.CanonicalUrl).ToList();
        Assert.Contains("https://example.com/", urls);
        Assert.Contains("https://example.com/never-hit", urls);
        Assert.DoesNotContain("https://example.com/noindex-page", urls);
        Assert.DoesNotContain("https://example.com/page-a", urls);
        Assert.All(result.UncrawledIndexable, r => Assert.Equal(0, r.HitCount));
    }

    [Fact]
    public async Task DetailedResultCarriesEvidenceFindingsAndMetrics()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var detailed = await fixture.Engine.CorrelateDetailedAsync(1, fixture.ImportId);

        // Findings: one per row across the three views, correct category/severity.
        Assert.Equal(8, detailed.Findings.Count);
        Assert.All(detailed.Findings, f => Assert.Equal(AuditCategory.LogIntelligence, f.Category));
        Assert.Equal(3, detailed.Findings.Count(f => f.RuleKey == LogRuleKeys.TrueOrphan && f.Severity == FindingSeverity.Warning));
        Assert.Equal(3, detailed.Findings.Count(f => f.RuleKey == LogRuleKeys.CrawlBudgetWaste && f.Severity == FindingSeverity.Warning));
        Assert.Equal(2, detailed.Findings.Count(f => f.RuleKey == LogRuleKeys.UncrawledIndexable && f.Severity == FindingSeverity.Notice));

        // Uncrawled evidence: crawl depth + inlink count + window caveat.
        var neverHitRow = detailed.UncrawledIndexable.Single(r => r.CanonicalUrl == "https://example.com/never-hit");
        using var neverHitEvidence = JsonDocument.Parse(neverHitRow.EvidenceJson);
        Assert.Equal(3, neverHitEvidence.RootElement.GetProperty("crawlDepth").GetInt32());
        Assert.Equal(1, neverHitEvidence.RootElement.GetProperty("inlinkCount").GetInt64());
        Assert.True(neverHitEvidence.RootElement.TryGetProperty("windowStart", out _));

        // Waste evidence carries status mix and threshold.
        var errorRow = detailed.BudgetWaste.Single(r => r.CanonicalUrl == "https://example.com/error-page");
        using var errorEvidence = JsonDocument.Parse(errorRow.EvidenceJson);
        Assert.Equal(10, errorEvidence.RootElement.GetProperty("threshold").GetInt64());
        Assert.Equal(12, errorEvidence.RootElement.GetProperty("statusMix").GetProperty("4xx").GetInt64());

        // Metrics: locked keys with counts.
        Assert.Equal(3, detailed.Metrics.Single(m => m.MetricKey == MetricKeys.LogTrueOrphans).Value);
        Assert.Equal(3, detailed.Metrics.Single(m => m.MetricKey == MetricKeys.LogBudgetWaste).Value);
        Assert.Equal(2, detailed.Metrics.Single(m => m.MetricKey == MetricKeys.LogUncrawledIndexable).Value);
        Assert.Equal(59, detailed.Metrics.Single(m => m.MetricKey == MetricKeys.LogBotHits).Value);
    }

    [Fact]
    public async Task OutOfScopeOrphansAreReportedSeparately()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var detailed = await fixture.Engine.CorrelateDetailedAsync(1, fixture.ImportId);

        Assert.Single(detailed.OutOfScopeOrphans);
        Assert.Equal("https://other.example/foreign", detailed.OutOfScopeOrphans[0].CanonicalUrl);
        Assert.DoesNotContain(detailed.TrueOrphans, r => r.CanonicalUrl.Contains("other.example"));
    }

    [Fact]
    public async Task CustomThresholdChangesTheWasteView()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();
        var engine = new LogCorrelationEngine(fixture.Database.Provider, new LogCorrelationOptions(BudgetWasteThreshold: 3));

        var result = await engine.CorrelateAsync(1, fixture.ImportId);

        // orphan-page (3 hits, query-free, 200, not crawled) still not waste:
        // threshold met but no low-value facet applies.
        Assert.DoesNotContain(result.BudgetWaste, r => r.CanonicalUrl == "https://example.com/orphan-page");
        Assert.Equal(3, result.BudgetWaste.Count);
    }

    [Fact]
    public async Task ExplicitProjectHostsOverrideDerivedScope()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();
        var engine = new LogCorrelationEngine(
            fixture.Database.Provider,
            new LogCorrelationOptions(ProjectHosts: ["example.com", "other.example"]));

        var detailed = await engine.CorrelateDetailedAsync(1, fixture.ImportId);

        Assert.Empty(detailed.OutOfScopeOrphans);
        Assert.Equal(4, detailed.TrueOrphans.Count);
    }

    [Fact]
    public async Task ReadModelPagesAndSummarizesViews()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var orphansPage = await fixture.Engine.GetCorrelationPageAsync(1, fixture.ImportId, LogCorrelationView.Orphans, offset: 0, limit: 2);
        Assert.Equal(LogCorrelationView.Orphans, orphansPage.View);
        Assert.Equal(2, orphansPage.Rows.Count);
        Assert.Equal(3, orphansPage.TotalCount);
        Assert.Single(orphansPage.OutOfScopeOrphans);

        var summary = await fixture.Engine.GetCorrelationPageAsync(1, fixture.ImportId, LogCorrelationView.Summary);
        Assert.Equal(3, summary.Rows.Count);
        Assert.Contains(summary.Rows, r => r.CanonicalUrl == LogRuleKeys.TrueOrphan && r.HitCount == 3);
        Assert.Contains(summary.Rows, r => r.CanonicalUrl == LogRuleKeys.CrawlBudgetWaste && r.HitCount == 3);
        Assert.Contains(summary.Rows, r => r.CanonicalUrl == LogRuleKeys.UncrawledIndexable && r.HitCount == 2);
    }

    [Fact]
    public async Task CorrelationIsRecomputableOnDemand()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        var first = await fixture.Engine.CorrelateAsync(1, fixture.ImportId);
        var second = await fixture.Engine.CorrelateAsync(1, fixture.ImportId);

        Assert.Equal(first.TrueOrphans.Count, second.TrueOrphans.Count);
        Assert.Equal(first.BudgetWaste.Count, second.BudgetWaste.Count);
        Assert.Equal(first.UncrawledIndexable.Count, second.UncrawledIndexable.Count);
    }

    [Fact]
    public async Task MissingImportThrows()
    {
        await using var fixture = await CorrelationFixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.CorrelateAsync(1, 9999));
    }
}
