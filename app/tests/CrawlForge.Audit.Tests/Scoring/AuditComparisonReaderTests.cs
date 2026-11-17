using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Tests.Scoring.Support;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// Cross-audit comparison suite (FS-CRAWL-001 §8.2): new/resolved findings
/// keyed by (rule key, canonical URL) across audits with different URL
/// surrogate keys, metric deltas, and score deltas — fully offline from
/// stored rows. Real stack: migrated SQLite.
/// </summary>
public class AuditComparisonReaderTests
{
    private static async Task<(long BaseAudit, long TargetAudit, ScoringRealStack Stack)> CreatePairAsync()
    {
        var stack = await ScoringRealStack.CreateAsync();
        var target = await stack.AddAuditAsync();
        await stack.AddUrlAsync(target, "https://example.test/one");
        await stack.AddUrlAsync(target, "https://example.test/two");
        await stack.AddUrlAsync(target, "https://example.test/three");
        return (stack.AuditId, target, stack);
    }

    [Fact]
    public async Task Compare_IdentifiesNewAndResolved_ByCanonicalUrl()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        // Same canonical URLs, different surrogate keys per audit.
        var baseUrl1 = (await new Data.Repositories.SqlRepository<Contracts.Entities.UrlRecordDto>(stack.Provider)
            .ListAsync(new Contracts.Common.ListQuery(0, 100))).Items.First(u => u.AuditId == baseAudit && u.CanonicalUrl.EndsWith("/one", StringComparison.Ordinal)).Id;
        var targetUrl1 = (await new Data.Repositories.SqlRepository<Contracts.Entities.UrlRecordDto>(stack.Provider)
            .ListAsync(new Contracts.Common.ListQuery(0, 100))).Items.First(u => u.AuditId == targetAudit && u.CanonicalUrl.EndsWith("/one", StringComparison.Ordinal)).Id;
        Assert.NotEqual(baseUrl1, targetUrl1);

        await stack.AddFindingAsync(baseAudit, "rule.persisting", AuditCategory.Content, FindingSeverity.Warning, baseUrl1);
        await stack.AddFindingAsync(baseAudit, "rule.resolved", AuditCategory.Content, FindingSeverity.Error, baseUrl1);
        await stack.AddFindingAsync(targetAudit, "rule.persisting", AuditCategory.Content, FindingSeverity.Warning, targetUrl1);
        await stack.AddFindingAsync(targetAudit, "rule.new", AuditCategory.Linking, FindingSeverity.Notice, targetUrl1);

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        var newly = Assert.Single(result.NewFindings);
        Assert.Equal("rule.new", newly.RuleKey);
        Assert.Equal("https://example.test/one", newly.CanonicalUrl);
        var resolved = Assert.Single(result.ResolvedFindings);
        Assert.Equal("rule.resolved", resolved.RuleKey);
    }

    [Fact]
    public async Task Compare_SiteWideFindings_MatchByRuleKey()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        await stack.AddFindingAsync(baseAudit, "site.rule", AuditCategory.Linking, FindingSeverity.Warning);
        await stack.AddFindingAsync(targetAudit, "site.rule", AuditCategory.Linking, FindingSeverity.Warning);
        await stack.AddFindingAsync(targetAudit, "site.new", AuditCategory.Linking, FindingSeverity.Notice);

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        Assert.Empty(result.ResolvedFindings);
        var newly = Assert.Single(result.NewFindings);
        Assert.Equal("site.new", newly.RuleKey);
        Assert.Null(newly.CanonicalUrl);
    }

    [Fact]
    public async Task Compare_ExcludesSuppressedFindings()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        await stack.AddFindingAsync(baseAudit, "rule.hidden", AuditCategory.Content, FindingSeverity.Error, isSuppressed: true);
        await stack.AddFindingAsync(targetAudit, "rule.visible", AuditCategory.Content, FindingSeverity.Error);

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        Assert.Empty(result.ResolvedFindings); // suppressed base finding is invisible
        Assert.Single(result.NewFindings);
    }

    [Fact]
    public async Task Compare_DeduplicatesSameIdentity()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        await stack.AddFindingAsync(targetAudit, "rule.dup", AuditCategory.Content, FindingSeverity.Warning);
        await stack.AddFindingAsync(targetAudit, "rule.dup", AuditCategory.Content, FindingSeverity.Warning);

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        Assert.Single(result.NewFindings);
    }

    [Fact]
    public async Task Compare_MetricDeltas_FromStoredAggregates()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        var recorder = new AuditScoreRecorder(stack.Provider);
        var scoring = new AuditScoringService(stack.Provider);
        await stack.AddFindingAsync(baseAudit, "r.e", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(baseAudit, "r.e2", AuditCategory.Content, FindingSeverity.Error);
        await stack.AddFindingAsync(targetAudit, "r.e", AuditCategory.Content, FindingSeverity.Error);
        await recorder.RecordAsync(baseAudit, await scoring.ComputeAsync(baseAudit));
        await recorder.RecordAsync(targetAudit, await scoring.ComputeAsync(targetAudit));

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        var total = Assert.Single(result.MetricDeltas, d => d.MetricKey == ScoringMetricKeys.FindingsTotal);
        Assert.Equal(2, total.BaseValue);
        Assert.Equal(1, total.TargetValue);
        Assert.Equal(-1, total.Delta);
        var indexable = Assert.Single(result.MetricDeltas, d => d.MetricKey == ScoringMetricKeys.UrlsIndexable);
        Assert.Equal(0, indexable.Delta);
    }

    [Fact]
    public async Task Compare_MetricPresentInOnlyOneAudit_HasNullDelta()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        await new Data.Repositories.SqlRepository<Contracts.Entities.MetricSummaryDto>(stack.Provider)
            .CreateAsync(new Contracts.Entities.MetricSummaryDto(0, baseAudit, "base.only", "{}", 5));

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        var delta = Assert.Single(result.MetricDeltas, d => d.MetricKey == "base.only");
        Assert.Equal(5, delta.BaseValue);
        Assert.Null(delta.TargetValue);
        Assert.Null(delta.Delta);
    }

    [Fact]
    public async Task Compare_ScoreDeltas_PerCategoryAndOverall()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;
        var recorder = new AuditScoreRecorder(stack.Provider);
        var scoring = new AuditScoringService(stack.Provider);
        await stack.AddFindingAsync(baseAudit, "r.e", AuditCategory.Content, FindingSeverity.Error);
        await recorder.RecordAsync(baseAudit, await scoring.ComputeAsync(baseAudit));
        await recorder.RecordAsync(targetAudit, await scoring.ComputeAsync(targetAudit));

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        var content = Assert.Single(result.ScoreDeltas, d => d.Category == AuditCategory.Content);
        Assert.Equal(66.67, content.BaseScore);
        Assert.Equal(100.0, content.TargetScore);
        Assert.Equal(33.33, content.Delta);
        var overall = Assert.Single(result.ScoreDeltas, d => d.Category is null);
        Assert.NotNull(overall.Delta);
        Assert.True(overall.Delta > 0);
        Assert.Equal(result.ScoreDeltas.Count, Enum.GetValues<AuditCategory>().Length + 1);
    }

    [Fact]
    public async Task Compare_EmptyAudits_ProduceEmptyDeltas()
    {
        var (baseAudit, targetAudit, stack) = await CreatePairAsync();
        await using var _ = stack;

        var result = await new AuditComparisonReader(stack.Provider).CompareAsync(baseAudit, targetAudit);

        Assert.Empty(result.NewFindings);
        Assert.Empty(result.ResolvedFindings);
        Assert.Empty(result.MetricDeltas);
        Assert.Empty(result.ScoreDeltas);
        Assert.Equal(baseAudit, result.BaseAuditId);
        Assert.Equal(targetAudit, result.TargetAuditId);
    }
}
