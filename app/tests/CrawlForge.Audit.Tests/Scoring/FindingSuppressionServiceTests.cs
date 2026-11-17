using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Tests.Scoring.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// Finding suppression service suite (FS-CRAWL-001 §7.5): project-scoped,
/// reversible, idempotent suppressions that trigger offline re-flagging and
/// rescoring on every state change. Real stack: migrated SQLite + repositories.
/// </summary>
public class FindingSuppressionServiceTests
{
    private static (FindingSuppressionService Service, ScoringRecomputeCoordinator Coordinator) CreateServices(ScoringRealStack stack)
    {
        var coordinator = new ScoringRecomputeCoordinator(stack.Provider);
        var service = new FindingSuppressionService(
            new SqlRepository<FindingSuppressionDto>(stack.Provider),
            new SqlRepository<FindingSuppressionDto>(stack.Provider),
            coordinator);
        return (service, coordinator);
    }

    [Fact]
    public async Task Suppress_PersistsActiveRow_AndListsIt()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var (service, _) = CreateServices(stack);

        var persisted = await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest("content.error", null, null));

        Assert.True(persisted.IsSuppressed);
        Assert.Equal(stack.ProjectId, persisted.ProjectId);
        Assert.Equal("content.error", persisted.RuleKey);
        var listed = await service.ListAsync(stack.ProjectId);
        Assert.Contains(listed, s => s.SuppressionKey == persisted.SuppressionKey);
    }

    [Fact]
    public async Task Suppress_SameDimensions_IsIdempotent()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var (service, _) = CreateServices(stack);
        var request = new FindingSuppressionRequest("content.error", null, null);

        var first = await service.SuppressAsync(stack.ProjectId, request);
        var second = await service.SuppressAsync(stack.ProjectId, request);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await service.ListAsync(stack.ProjectId));
    }

    [Fact]
    public async Task Suppress_ReactivatesInactiveRow()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var (service, _) = CreateServices(stack);
        var request = new FindingSuppressionRequest("content.error", null, null);
        var first = await service.SuppressAsync(stack.ProjectId, request);
        await service.UnsuppressAsync(stack.ProjectId, first.SuppressionKey);

        var reactivated = await service.SuppressAsync(stack.ProjectId, request);

        Assert.Equal(first.Id, reactivated.Id);
        Assert.True(reactivated.IsSuppressed);
        Assert.Single(await service.ListAsync(stack.ProjectId));
    }

    [Fact]
    public async Task Unsuppress_FlipsFlag_Reversibly()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var (service, _) = CreateServices(stack);
        var persisted = await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest("content.error", null, null));

        Assert.True(await service.UnsuppressAsync(stack.ProjectId, persisted.SuppressionKey));
        Assert.False(await service.UnsuppressAsync(stack.ProjectId, persisted.SuppressionKey));
        Assert.False(await service.UnsuppressAsync(stack.ProjectId, "missing-key"));

        // Row retained for history, now inactive.
        var listed = Assert.Single(await service.ListAsync(stack.ProjectId));
        Assert.False(listed.IsSuppressed);
    }

    [Fact]
    public async Task Suppress_TriggersOfflineRecompute_FlagsAndScores()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var finding = await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var (service, _) = CreateServices(stack);
        var recorder = new AuditScoreRecorder(stack.Provider);

        await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest("content.error", null, null));

        var flagged = await new SqlRepository<FindingDto>(stack.Provider).GetByIdAsync(finding.Id);
        Assert.True(flagged!.IsSuppressed);
        var scores = await recorder.GetScoresAsync(stack.AuditId);
        Assert.NotEmpty(scores);
        Assert.All(scores, s => Assert.Equal(100.0, s.Score));
        Assert.All(scores, s => Assert.Equal(0, s.ErrorCount));
    }

    [Fact]
    public async Task Unsuppress_RestoresFlagsAndScores()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var finding = await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var (service, _) = CreateServices(stack);
        var recorder = new AuditScoreRecorder(stack.Provider);
        var persisted = await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest("content.error", null, null));

        await service.UnsuppressAsync(stack.ProjectId, persisted.SuppressionKey);

        var unflagged = await new SqlRepository<FindingDto>(stack.Provider).GetByIdAsync(finding.Id);
        Assert.False(unflagged!.IsSuppressed);
        var content = Assert.Single(await recorder.GetScoresAsync(stack.AuditId), s => s.Category == AuditCategory.Content);
        Assert.Equal(66.67, content.Score);
        Assert.Equal(1, content.ErrorCount);
    }

    [Fact]
    public async Task SuppressionChange_RecomputesAllProjectAudits()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var audit2 = await stack.AddAuditAsync();
        await stack.AddUrlAsync(audit2, "https://example.test/other");
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(audit2, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var (service, _) = CreateServices(stack);
        var recorder = new AuditScoreRecorder(stack.Provider);

        await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest("content.error", null, null));

        Assert.All(await recorder.GetScoresAsync(stack.AuditId), s => Assert.Equal(100.0, s.Score));
        Assert.All(await recorder.GetScoresAsync(audit2), s => Assert.Equal(100.0, s.Score));
    }

    [Fact]
    public async Task NoDimensionRequest_CreatesInertRow()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var (service, _) = CreateServices(stack);
        var recorder = new AuditScoreRecorder(stack.Provider);

        await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest(null, null, null));

        var content = Assert.Single(await recorder.GetScoresAsync(stack.AuditId), s => s.Category == AuditCategory.Content);
        Assert.Equal(66.67, content.Score);
    }

    [Fact]
    public async Task LongScope_UsesSha256Key_WithinSchemaLimit()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var (service, _) = CreateServices(stack);

        var persisted = await service.SuppressAsync(stack.ProjectId, new FindingSuppressionRequest(null, null, new string('x', 300)));

        Assert.StartsWith("sha256:", persisted.SuppressionKey, StringComparison.Ordinal);
        Assert.True(persisted.SuppressionKey.Length <= 191);
    }

    [Fact]
    public async Task IsSuppressed_DelegatesToKeystoneAlignedMatcher()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var (service, _) = CreateServices(stack);
        var suppression = new FindingSuppressionDto(1, 1, "k", "rule.a", null, null, true, DateTimeOffset.UnixEpoch);

        Assert.True(service.IsSuppressed(suppression, "rule.a", 5));
        Assert.False(service.IsSuppressed(suppression, "rule.b", 5));
    }
}
