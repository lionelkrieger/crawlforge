using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Tests.Scoring.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// Score recorder suite: transactional replace semantics into audit_scores,
/// suppression-aware scoring.* aggregates, and strict metric-key ownership
/// (sibling metric rows are never touched). Real stack: migrated SQLite.
/// </summary>
public class AuditScoreRecorderTests
{
    private static AuditScoreDraft Draft(AuditCategory? category, double score, int errors = 0, int warnings = 0, int notices = 0, int passes = 0) =>
        new(category, score, errors, warnings, notices, passes);

    [Fact]
    public async Task Record_ReplacesPriorScores_Idempotently()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var recorder = new AuditScoreRecorder(stack.Provider);
        var first = new[] { Draft(AuditCategory.Content, 50, errors: 2), Draft(null, 50, errors: 2) };
        var second = new[] { Draft(AuditCategory.Content, 90, warnings: 1), Draft(null, 90, warnings: 1) };

        await recorder.RecordAsync(stack.AuditId, first);
        await recorder.RecordAsync(stack.AuditId, first);
        await recorder.RecordAsync(stack.AuditId, second);

        var scores = await recorder.GetScoresAsync(stack.AuditId);
        Assert.Equal(2, scores.Count);
        var content = Assert.Single(scores, s => s.Category == AuditCategory.Content);
        Assert.Equal(90, content.Score);
        Assert.Equal(0, content.ErrorCount);
        Assert.Equal(1, content.WarningCount);
    }

    [Fact]
    public async Task GetScores_OrdersOverallLast()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var recorder = new AuditScoreRecorder(stack.Provider);

        await recorder.RecordAsync(stack.AuditId,
        [
            Draft(null, 80),
            Draft(AuditCategory.Linking, 70),
            Draft(AuditCategory.Content, 90),
        ]);

        var scores = await recorder.GetScoresAsync(stack.AuditId);
        Assert.Equal(AuditCategory.Content, scores[0].Category);
        Assert.Equal(AuditCategory.Linking, scores[1].Category);
        Assert.Null(scores[2].Category);
    }

    [Fact]
    public async Task Record_WritesSuppressionAwareScoringMetrics()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "r.e", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(stack.AuditId, "r.w", AuditCategory.Content, FindingSeverity.Warning);
        await stack.AddFindingAsync(stack.AuditId, "r.s", AuditCategory.Content, FindingSeverity.Error, isSuppressed: true);
        var recorder = new AuditScoreRecorder(stack.Provider);
        var drafts = await new AuditScoringService(stack.Provider).ComputeAsync(stack.AuditId);

        await recorder.RecordAsync(stack.AuditId, drafts);

        var metrics = await new SqlRepository<MetricSummaryDto>(stack.Provider).ListAsync(new ListQuery(0, 200));
        var byKey = metrics.Items.Where(m => m.MetricKey.StartsWith("scoring.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            2 + 2 + 4 + Enum.GetValues<AuditCategory>().Length,
            byKey.Length); // findings+suppressed totals, 2 URL counts, 4 severities, per-category
        Assert.Equal(2, Assert.Single(byKey, m => m.MetricKey == ScoringMetricKeys.FindingsTotal).Value);
        Assert.Equal(1, Assert.Single(byKey, m => m.MetricKey == ScoringMetricKeys.SuppressedTotal).Value);
        Assert.Equal(3, Assert.Single(byKey, m => m.MetricKey == ScoringMetricKeys.UrlsTotal).Value);
        Assert.Equal(3, Assert.Single(byKey, m => m.MetricKey == ScoringMetricKeys.UrlsIndexable).Value);
        var errorSeries = Assert.Single(byKey, m =>
            m.MetricKey == ScoringMetricKeys.FindingsBySeverity && m.DimensionsJson.Contains("Error"));
        Assert.Equal(1, errorSeries.Value);
        var contentSeries = Assert.Single(byKey, m =>
            m.MetricKey == ScoringMetricKeys.FindingsByCategory && m.DimensionsJson.Contains("Content"));
        Assert.Equal(2, contentSeries.Value);
    }

    [Fact]
    public async Task Record_PreservesSiblingMetricKeys()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var repository = new SqlRepository<MetricSummaryDto>(stack.Provider);
        var sibling = await repository.CreateAsync(new MetricSummaryDto(
            0, stack.AuditId, Contracts.Constants.MetricKeys.WafBlocks, "{}", 7));
        var recorder = new AuditScoreRecorder(stack.Provider);

        await recorder.RecordAsync(stack.AuditId, [Draft(null, 100)]);
        await recorder.RecordAsync(stack.AuditId, [Draft(null, 95)]);

        var readBack = await repository.GetByIdAsync(sibling.Id);
        Assert.NotNull(readBack);
        Assert.Equal(7, readBack!.Value);
    }

    [Fact]
    public async Task Record_MetricRewrite_IsIdempotent()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var recorder = new AuditScoreRecorder(stack.Provider);
        var drafts = new[] { Draft(AuditCategory.Content, 100), Draft(null, 100) };

        await recorder.RecordAsync(stack.AuditId, drafts);
        await recorder.RecordAsync(stack.AuditId, drafts);

        var metrics = await new SqlRepository<MetricSummaryDto>(stack.Provider).ListAsync(new ListQuery(0, 200));
        var scoringRows = metrics.Items.Where(m => m.MetricKey.StartsWith("scoring.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(scoringRows.Length, scoringRows.Select(m => (m.MetricKey, m.DimensionsJson)).Distinct().Count());
    }
}
