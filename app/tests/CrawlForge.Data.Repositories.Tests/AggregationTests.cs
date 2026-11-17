using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// Aggregation-into-DB (§8.1): metric_summaries read models and audit scores are
/// computed transactionally, suppressed findings are excluded, disabled categories
/// contribute no score, and recomputation is idempotent.
/// </summary>
public sealed class AggregationTests
{
    [Fact]
    public async Task Recompute_WritesUrlFindingAndHealthMetrics()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        var result = await service.RecomputeAsync(1);

        Assert.True(result.MetricRowsWritten > 20);
        Assert.True(result.ScoreRowsWritten >= 2);
        Assert.Equal(3, await MetricValueAsync(database, "url-count", "%\"dimension\":\"status-class\",\"value\":\"Success\"%"));
        Assert.Equal(1, await MetricValueAsync(database, "url-count", "%\"dimension\":\"status-class\",\"value\":\"Blocked\"%"));
        Assert.Equal(3, await MetricValueAsync(database, "finding-count", "%\"dimension\":\"severity\",\"value\":\"Error\"%"));
        Assert.Equal(1, await MetricValueAsync(database, "finding-count", "%\"dimension\":\"severity\",\"value\":\"Warning\"%"));
        Assert.Equal(1, await MetricValueAsync(database, "crawl-health", "%\"measure\":\"errors\"%"));
        Assert.Equal(1, await MetricValueAsync(database, "crawl-health", "%\"measure\":\"blocked\"%"));
        Assert.Equal(1, await MetricValueAsync(database, "crawl-health", "%\"measure\":\"redirects\"%"));
    }

    [Fact]
    public async Task Recompute_ExcludesSuppressedFindingsFromCountsAndScores()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        await service.RecomputeAsync(1);

        // Seed has 3 Indexability errors, one of them suppressed → 2 counted.
        var score = await ScoreAsync(database, (long)AuditCategory.Indexability);
        Assert.Equal(2, score.ErrorCount);
        Assert.Equal(1, score.WarningCount);
        Assert.Equal(1, score.NoticeCount);
        Assert.Equal(1, score.PassCount);
    }

    [Fact]
    public async Task Recompute_ScoresUseIndexableCoverageAndClamp()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        await service.RecomputeAsync(1);

        // 4 indexable URLs; Indexability: 2 errors + 1 warning →
        // penalty = (3*2+1)/4 = 1.75 → score = 100*(1-1.75/3) ≈ 41.67.
        var score = await ScoreAsync(database, (long)AuditCategory.Indexability);
        Assert.Equal(100.0 * (1.0 - 1.75 / 3.0), score.Score, precision: 6);

        var overall = await OverallScoreAsync(database);
        Assert.InRange(overall.Score, 0, 100);
        Assert.Equal(3, overall.ErrorCount); // 2 Indexability + 1 other category
    }

    [Fact]
    public async Task Recompute_DisabledCategoryContributesNoScore()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        var result = await service.RecomputeAsync(1, new HashSet<AuditCategory> { AuditCategory.Indexability });

        Assert.Equal(0L, await database.ScalarAsync(
            $"SELECT COUNT(*) FROM audit_scores WHERE audit_id = 1 AND category = {(long)AuditCategory.Indexability}"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT(*) FROM audit_scores WHERE audit_id = 1 AND category IS NULL"));
        Assert.True(result.ScoreRowsWritten >= 2);
    }

    [Fact]
    public async Task Recompute_IsIdempotent_ReplacesPreviousAggregates()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        var first = await service.RecomputeAsync(1);
        var second = await service.RecomputeAsync(1);

        Assert.Equal(first.MetricRowsWritten, second.MetricRowsWritten);
        Assert.Equal(first.ScoreRowsWritten, second.ScoreRowsWritten);
        Assert.Equal(first.MetricRowsWritten, await database.CountAsync("metric_summaries"));
        Assert.Equal(first.ScoreRowsWritten, await database.CountAsync("audit_scores"));
    }

    [Fact]
    public async Task Recompute_ComputesLinkGraphStatistics()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        await service.RecomputeAsync(1);

        Assert.Equal(3, await MetricValueAsync(database, "link-graph", "%\"measure\":\"internal-edges\"%"));
        Assert.Equal(1, await MetricValueAsync(database, "link-graph", "%\"measure\":\"orphan-pages\"%"));
        Assert.Equal(5, await MetricValueAsync(database, "link-graph", "%\"measure\":\"reachable-pages\"%"));
        Assert.Equal(3, await MetricValueAsync(database, "link-equity", "%\"measure\":\"inlink-total\"%"));
    }

    [Fact]
    public async Task Recompute_ComputesDistributionsFromPayloads()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAuditDataAsync(database);
        var service = new AuditAggregationService(database.Provider);

        await service.RecomputeAsync(1);

        Assert.Equal(200, await MetricValueAsync(database, "distribution", "%\"measure\":\"word-count\",\"statistic\":\"mean\"%"));
        Assert.Equal(300, await MetricValueAsync(database, "distribution", "%\"measure\":\"word-count\",\"statistic\":\"max\"%"));
        Assert.Equal(162.5, await MetricValueAsync(database, "distribution", "%\"measure\":\"response-time\",\"statistic\":\"mean\"%"));
        Assert.True(await MetricValueAsync(database, "distribution", "%\"measure\":\"page-weight\",\"statistic\":\"mean\"%") > 0);
    }

    private static async Task<double> MetricValueAsync(TestDatabase database, string metricKey, string dimensionsPattern)
    {
        var result = await database.ScalarAsync(
            $"SELECT metric_value FROM metric_summaries WHERE metric_key = '{metricKey}' AND dimensions_json LIKE '{dimensionsPattern}'");
        Assert.NotNull(result);
        return Convert.ToDouble(result);
    }

    private static async Task<AuditScoreDto> ScoreAsync(TestDatabase database, long category)
    {
        var repository = new SqlRepository<AuditScoreDto>(database.Provider);
        await foreach (var score in repository.StreamWhereAsync(
            "audit_id = 1 AND category = @category",
            new Dictionary<string, object?> { ["category"] = category }))
        {
            return score;
        }

        throw new Xunit.Sdk.XunitException($"No score row for category {category}.");
    }

    private static async Task<AuditScoreDto> OverallScoreAsync(TestDatabase database)
    {
        var repository = new SqlRepository<AuditScoreDto>(database.Provider);
        await foreach (var score in repository.StreamWhereAsync("audit_id = 1 AND category IS NULL"))
        {
            return score;
        }

        throw new Xunit.Sdk.XunitException("No overall score row.");
    }

    /// <summary>
    /// Deterministic audit fixture on the base chain (audit 1, urls 1–2):
    /// 6 URLs (4 indexable), 6 findings (1 suppressed), 3 internal edges + 1
    /// external, word-count and timing payloads.
    /// </summary>
    private static async Task SeedAuditDataAsync(TestDatabase database)
    {
        var urls = new SqlRepository<UrlRecordDto>(database.Provider);
        var findings = new SqlRepository<FindingDto>(database.Provider);
        var edges = new SqlRepository<LinkEdgeDto>(database.Provider);
        var elements = new SqlRepository<UrlElementDto>(database.Provider);

        // Base chain urls 1–2 stay; add urls 3–6.
        var statuses = new[]
        {
            CrawlStatusClass.Success, CrawlStatusClass.Success, CrawlStatusClass.Redirect,
            CrawlStatusClass.ClientError, CrawlStatusClass.Blocked, CrawlStatusClass.Success
        };
        var urlIds = new List<long> { 1, 2 };
        for (var i = 0; i < 6; i++)
        {
            var indexable = i is 0 or 1 or 2 or 5;
            var record = new UrlRecordDto(
                i < 2 ? urlIds[i] : 0, 1, $"https://example.test/p/{i}", "example.test", $"/p/{i}",
                Depth: i % 3, i == 4 ? DiscoverySource.Link : DiscoverySource.Seed,
                statuses[i], FinalStatusCode: 200, RedirectTargetUrl: null,
                ContentType: i % 2 == 0 ? "text/html" : "application/pdf",
                SizeBytes: 10_000 * (i + 1), TimingJson: $"{{\"totalMs\":{100 + i * 25}}}",
                RobotsState.Allowed, IndexabilityFlags.None, indexable);
            if (i < 2)
            {
                await urls.UpdateAsync(record);
            }
            else
            {
                var created = await urls.CreateAsync(record);
                urlIds.Add(created.Id);
            }
        }

        // Findings: Indexability 2 errors + 1 warning + 1 notice + 1 pass + 1 suppressed error;
        // category 1: 1 error.
        await findings.CreateAsync(new FindingDto(0, 1, "idx-err-1", AuditCategory.Indexability, FindingSeverity.Error, 1, "{}", null, false));
        await findings.CreateAsync(new FindingDto(0, 1, "idx-err-2", AuditCategory.Indexability, FindingSeverity.Error, 2, "{}", null, false));
        await findings.CreateAsync(new FindingDto(0, 1, "idx-err-suppressed", AuditCategory.Indexability, FindingSeverity.Error, 3, "{}", null, true));
        await findings.CreateAsync(new FindingDto(0, 1, "idx-warn", AuditCategory.Indexability, FindingSeverity.Warning, 1, "{}", null, false));
        await findings.CreateAsync(new FindingDto(0, 1, "idx-notice", AuditCategory.Indexability, FindingSeverity.Notice, null, "{}", null, false));
        await findings.CreateAsync(new FindingDto(0, 1, "idx-pass", AuditCategory.Indexability, FindingSeverity.Pass, 2, "{}", null, false));
        await findings.CreateAsync(new FindingDto(0, 1, "other-err", (AuditCategory)1, FindingSeverity.Error, 4, "{}", null, false));

        // Internal edges 1→2, 1→3, 2→4; external edge 3→off-site. Url 5 is an orphan
        // (discovered via Link, no inlinks); reachable set from seed url 1 = {1,2,3,4}.
        await edges.CreateAsync(new LinkEdgeDto(0, 1, 1, "https://example.test/p/1", 2, "to 2", LinkRelationFlags.None, true));
        await edges.CreateAsync(new LinkEdgeDto(0, 1, 1, "https://example.test/p/2", 3, "to 3", LinkRelationFlags.None, true));
        await edges.CreateAsync(new LinkEdgeDto(0, 1, 2, "https://example.test/p/3", 4, "to 4", LinkRelationFlags.None, true) { WeightedEquity = 0.5 });
        await edges.CreateAsync(new LinkEdgeDto(0, 1, 3, "https://off-site.example/", null, "out", LinkRelationFlags.Nofollow, false));

        // Word-count element payloads: 100 and 300 → mean 200, max 300.
        await elements.CreateAsync(new UrlElementDto(0, 1, "word-count", "{\"count\":100}", 0));
        await elements.CreateAsync(new UrlElementDto(0, 2, "word-count", "{\"count\":300}", 0));
    }
}
