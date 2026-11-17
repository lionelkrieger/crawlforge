using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Tests.Scoring.Support;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// Audit scoring suite (FS-CRAWL-001 §8.1, criterion #32): coverage-weighted
/// severity deductions, suppression exclusion, the enabledCategories snapshot
/// convention, and cross-audit comparability. Real stack: migrated SQLite.
/// </summary>
public class AuditScoringServiceTests
{
    private static async Task<FindingSuppressionDto> AddSuppressionAsync(
        ScoringRealStack stack, string? ruleKey, long? urlId = null, string? scope = null, bool active = true)
    {
        return await new SqlRepository<FindingSuppressionDto>(stack.Provider).CreateAsync(
            new FindingSuppressionDto(0, stack.ProjectId, Guid.NewGuid().ToString("N"),
                ruleKey, urlId, scope, active, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task EmptyAudit_AllCategoriesScore100()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(Enum.GetValues<AuditCategory>().Length + 1, drafts.Count);
        Assert.All(drafts, d => Assert.Equal(100.0, d.Score));
        Assert.Null(drafts[^1].Category);
    }

    [Fact]
    public async Task ErrorDeducts_WeightedByIndexableCoverage()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        // 3 indexable URLs → capacity 30; one error (10) → 100 × (1 − 10/30).
        var content = Assert.Single(drafts, d => d.Category == AuditCategory.Content);
        Assert.Equal(66.67, content.Score);
        Assert.Equal(1, content.ErrorCount);
        Assert.All(drafts.Where(d => d.Category is not null and not AuditCategory.Content),
            d => Assert.Equal(100.0, d.Score));
    }

    [Fact]
    public async Task SeverityWeights_AreError10Warning3Notice1Pass0()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "r.e", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(stack.AuditId, "r.w", AuditCategory.Content, FindingSeverity.Warning);
        await stack.AddFindingAsync(stack.AuditId, "r.n", AuditCategory.Content, FindingSeverity.Notice);
        await stack.AddFindingAsync(stack.AuditId, "r.p", AuditCategory.Content, FindingSeverity.Pass);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        var content = Assert.Single(drafts, d => d.Category == AuditCategory.Content);
        Assert.Equal(53.33, content.Score); // penalty 14 / capacity 30
        Assert.Equal(1, content.ErrorCount);
        Assert.Equal(1, content.WarningCount);
        Assert.Equal(1, content.NoticeCount);
        Assert.Equal(1, content.PassCount);
    }

    [Fact]
    public async Task Overall_IsMeanOfCategoryScores_WithSummedCounts()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(stack.AuditId, "link.warn", AuditCategory.Linking, FindingSeverity.Warning);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        var overall = Assert.Single(drafts, d => d.Category is null);
        var categories = drafts.Where(d => d.Category is not null).ToArray();
        Assert.Equal(Math.Round(categories.Average(d => d.Score), 2), overall.Score);
        Assert.Equal(categories.Sum(d => d.ErrorCount), overall.ErrorCount);
        Assert.Equal(categories.Sum(d => d.WarningCount), overall.WarningCount);
    }

    [Fact]
    public async Task ActiveRuleSuppression_ExcludesFindingFromScore()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await AddSuppressionAsync(stack, "content.error");
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.All(drafts, d => Assert.Equal(100.0, d.Score));
        Assert.All(drafts, d => Assert.Equal(0, d.ErrorCount));
    }

    [Fact]
    public async Task InactiveSuppression_DoesNotExclude()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await AddSuppressionAsync(stack, "content.error", active: false);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(66.67, Assert.Single(drafts, d => d.Category == AuditCategory.Content).Score);
    }

    [Fact]
    public async Task NoDimensionSuppression_DoesNotExclude()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await AddSuppressionAsync(stack, null, null, null);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(66.67, Assert.Single(drafts, d => d.Category == AuditCategory.Content).Score);
    }

    [Fact]
    public async Task UrlScopedSuppression_ExcludesOnlyThatUrl()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var urls = await new SqlRepository<UrlRecordDto>(stack.Provider).ListAsync(new Contracts.Common.ListQuery(0, 100));
        var firstUrl = urls.Items[0].Id;
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error, firstUrl);
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error, urls.Items[1].Id);
        await AddSuppressionAsync(stack, null, firstUrl);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        var content = Assert.Single(drafts, d => d.Category == AuditCategory.Content);
        Assert.Equal(1, content.ErrorCount);
        Assert.Equal(66.67, content.Score);
    }

    [Fact]
    public async Task ScopeSuppression_MatchesRuleKey()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await AddSuppressionAsync(stack, null, null, "content.error");
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.All(drafts, d => Assert.Equal(100.0, d.Score));
    }

    [Fact]
    public async Task EnabledCategoriesConvention_DisabledCategoriesContributeNoScore()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.SetSnapshotAsync("""{ "enabledCategories": ["Content", "Indexability"] }""");
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(stack.AuditId, "link.error", AuditCategory.Linking, FindingSeverity.Error);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(3, drafts.Count);
        Assert.Equal(66.67, Assert.Single(drafts, d => d.Category == AuditCategory.Content).Score);
        Assert.Equal(100.0, Assert.Single(drafts, d => d.Category == AuditCategory.Indexability).Score);
        var overall = Assert.Single(drafts, d => d.Category is null);
        Assert.Equal(83.34, overall.Score); // mean(66.67, 100) = 83.335 → 83.34
        Assert.Equal(1, overall.ErrorCount); // Linking finding excluded entirely
    }

    [Fact]
    public async Task EnabledCategoriesConvention_NumericAndCaseInsensitiveNames()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.SetSnapshotAsync("""{ "EnabledCategories": [1, "structuredDATA"] }""");
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(3, drafts.Count);
        Assert.Contains(drafts, d => d.Category == AuditCategory.Content);
        Assert.Contains(drafts, d => d.Category == AuditCategory.StructuredData);
    }

    [Fact]
    public async Task EnabledCategoriesConvention_EmptyArray_OnlyOverall()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.SetSnapshotAsync("""{ "enabledCategories": [] }""");
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        var overall = Assert.Single(drafts);
        Assert.Null(overall.Category);
        Assert.Equal(100.0, overall.Score);
    }

    [Fact]
    public async Task MalformedSnapshot_FallsBackToAllEnabled()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.SetSnapshotAsync("{ not json");
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(Enum.GetValues<AuditCategory>().Length + 1, drafts.Count);
    }

    [Fact]
    public async Task NonIndexableUrls_DoNotCountTowardCoverage()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var audit2 = await stack.AddAuditAsync();
        await stack.AddUrlAsync(audit2, "https://example.test/ok", isIndexable: true);
        await stack.AddUrlAsync(audit2, "https://example.test/noindex", isIndexable: false);
        await stack.AddFindingAsync(audit2, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(audit2);

        // 1 indexable URL → capacity 10; one error exhausts it.
        Assert.Equal(0.0, Assert.Single(drafts, d => d.Category == AuditCategory.Content).Score);
    }

    [Fact]
    public async Task ZeroIndexableUrls_UsesDenominatorFloor()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var audit2 = await stack.AddAuditAsync();
        await stack.AddUrlAsync(audit2, "https://example.test/noindex", isIndexable: false);
        await stack.AddFindingAsync(audit2, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(audit2);

        Assert.Equal(0.0, Assert.Single(drafts, d => d.Category == AuditCategory.Content).Score);
    }

    [Fact]
    public async Task Scores_AreComparableAcrossAudits()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var audit2 = await stack.AddAuditAsync();
        await stack.AddUrlAsync(audit2, "https://example.test/a");
        await stack.AddUrlAsync(audit2, "https://example.test/b");
        await stack.AddUrlAsync(audit2, "https://example.test/c");
        await stack.AddFindingAsync(stack.AuditId, "content.error", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(audit2, "content.error", AuditCategory.Content, FindingSeverity.Error);
        var service = new AuditScoringService(stack.Provider);

        var first = await service.ComputeAsync(stack.AuditId);
        var second = await service.ComputeAsync(audit2);

        Assert.Equal(
            Assert.Single(first, d => d.Category == AuditCategory.Content).Score,
            Assert.Single(second, d => d.Category == AuditCategory.Content).Score);
    }

    [Fact]
    public async Task UnknownAudit_Throws()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var service = new AuditScoringService(stack.Provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ComputeAsync(9999));
    }

    [Fact]
    public void ParseEnabledCategories_AbsentProperty_AllEnabled()
    {
        Assert.Equal(Enum.GetValues<AuditCategory>().Length, AuditScoringService.ParseEnabledCategories(null).Count);
        Assert.Equal(Enum.GetValues<AuditCategory>().Length, AuditScoringService.ParseEnabledCategories("{}").Count);
        Assert.Equal(Enum.GetValues<AuditCategory>().Length, AuditScoringService.ParseEnabledCategories("""{ "enabledCategories": "not-an-array" }""").Count);
    }

    [Fact]
    public void ParseEnabledCategories_UnknownEntriesIgnored()
    {
        var enabled = AuditScoringService.ParseEnabledCategories("""{ "enabledCategories": ["Content", "FutureCategory", 999] }""");

        var single = Assert.Single(enabled);
        Assert.Equal(AuditCategory.Content, single);
    }
}
