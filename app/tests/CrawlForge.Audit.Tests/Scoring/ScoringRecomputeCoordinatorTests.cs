using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Tests.Scoring.Support;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// Recompute coordinator suite: the zero-fetch path serving re-audit and
/// suppression change. The fixture has NO content store — recomputation must
/// succeed from stored rows alone. Real stack: migrated SQLite.
/// </summary>
public class ScoringRecomputeCoordinatorTests
{
    private static async Task<FindingSuppressionDto> AddSuppressionRowAsync(
        ScoringRealStack stack, string? ruleKey, bool active = true)
    {
        return await new SqlRepository<FindingSuppressionDto>(stack.Provider).CreateAsync(
            new FindingSuppressionDto(0, stack.ProjectId, Guid.NewGuid().ToString("N"),
                ruleKey, null, null, active, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task RecomputeForAudit_ReflagsFindingsFromCurrentSuppressions()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var finding = await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await AddSuppressionRowAsync(stack, "content.error");
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);

        Assert.True(await coordinator.RecomputeForAuditAsync(stack.AuditId));

        var flagged = await new SqlRepository<FindingDto>(stack.Provider).GetByIdAsync(finding.Id);
        Assert.True(flagged!.IsSuppressed);
        var scores = await new AuditScoreRecorder(stack.Provider).GetScoresAsync(stack.AuditId);
        Assert.All(scores, s => Assert.Equal(100.0, s.Score));
    }

    [Fact]
    public async Task RecomputeForAudit_InactiveSuppression_RestoresFlags()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var finding = await stack.AddFindingAsync(
            stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error, isSuppressed: true);
        await AddSuppressionRowAsync(stack, "content.error", active: false);
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);

        await coordinator.RecomputeForAuditAsync(stack.AuditId);

        var unflagged = await new SqlRepository<FindingDto>(stack.Provider).GetByIdAsync(finding.Id);
        Assert.False(unflagged!.IsSuppressed);
        var content = Assert.Single(
            await new AuditScoreRecorder(stack.Provider).GetScoresAsync(stack.AuditId),
            s => s.Category == AuditCategory.Content);
        Assert.Equal(66.67, content.Score);
    }

    [Fact]
    public async Task RecomputeForAudit_IsIdempotent()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);
        var recorder = new AuditScoreRecorder(stack.Provider);

        await coordinator.RecomputeForAuditAsync(stack.AuditId);
        var first = await recorder.GetScoresAsync(stack.AuditId);
        await coordinator.RecomputeForAuditAsync(stack.AuditId);
        var second = await recorder.GetScoresAsync(stack.AuditId);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(s => s.Score), second.Select(s => s.Score));
    }

    [Fact]
    public async Task RecomputeForAudit_UnknownAudit_ReturnsFalse()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);

        Assert.False(await coordinator.RecomputeForAuditAsync(9999));
    }

    [Fact]
    public async Task RecomputeForProject_CoversAllAudits()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var audit2 = await stack.AddAuditAsync();
        await stack.AddUrlAsync(audit2, "https://example.test/other");
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(audit2, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);
        var recorder = new AuditScoreRecorder(stack.Provider);

        var count = await coordinator.RecomputeForProjectAsync(stack.ProjectId);

        Assert.Equal(2, count);
        Assert.NotEmpty(await recorder.GetScoresAsync(stack.AuditId));
        Assert.NotEmpty(await recorder.GetScoresAsync(audit2));
    }

    [Fact]
    public async Task RecomputeForProject_SkipsDeletedAudits()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var audit2 = await stack.AddAuditAsync();
        var repository = new SqlRepository<AuditDto>(stack.Provider);
        var deleted = await repository.GetByIdAsync(audit2);
        await repository.UpdateAsync(deleted! with { IsDeleted = true });
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);

        var count = await coordinator.RecomputeForProjectAsync(stack.ProjectId);

        Assert.Equal(1, count);
        Assert.Empty(await new AuditScoreRecorder(stack.Provider).GetScoresAsync(audit2));
    }

    [Fact]
    public async Task Recompute_RefreshesAggregatesConsistentlyWithSuppressionSet()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);
        await coordinator.RecomputeForAuditAsync(stack.AuditId);

        await AddSuppressionRowAsync(stack, "content.error");
        await coordinator.RecomputeForAuditAsync(stack.AuditId);

        var metrics = await new SqlRepository<MetricSummaryDto>(stack.Provider)
            .ListAsync(new Contracts.Common.ListQuery(0, 200));
        Assert.Equal(0, Assert.Single(metrics.Items, m => m.MetricKey == ScoringMetricKeys.FindingsTotal).Value);
        Assert.Equal(1, Assert.Single(metrics.Items, m => m.MetricKey == ScoringMetricKeys.SuppressedTotal).Value);
    }
}
