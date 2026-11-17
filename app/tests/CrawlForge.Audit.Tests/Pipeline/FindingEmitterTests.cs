using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Suppression-aware finding emission (FS-CRAWL-001 §7.5) on the real stack:
/// project-scoped, reversible suppressions; suppressed findings stay
/// inspectable; evidence carries first-seen audit reference and rule metadata.
/// Findings reference stored URL records (findings.url_id FK).
/// </summary>
public class FindingEmitterTests : IClassFixture<AuditSqliteStackFixture>
{
    private readonly AuditSqliteStackFixture _stack;

    public FindingEmitterTests(AuditSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private FindingEmitter CreateEmitter() =>
        new(_stack.Suppressions, _stack.Findings, new AuditRuleCatalog());

    private static FindingDraft Draft(string ruleKey, long? urlId = null) =>
        new(ruleKey, AuditCategory.Content, FindingSeverity.Warning, urlId, "{\"detail\":\"x\"}", null);

    private async Task<FindingSuppressionDto> AddSuppression(
        long projectId, string? ruleKey, long? urlId = null, string? findingScope = null, bool active = true)
    {
        return await _stack.Suppressions.CreateAsync(new FindingSuppressionDto(
            Id: 0,
            ProjectId: projectId,
            SuppressionKey: Guid.NewGuid().ToString("N"),
            RuleKey: ruleKey,
            UrlId: urlId,
            FindingScope: findingScope,
            IsSuppressed: active,
            CreatedAt: DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task EmittedFindingsPersistWithEvidenceEnrichment()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId);
        var emitter = CreateEmitter();

        var persisted = await emitter.EmitAsync(auditId, projectId, [Draft("content.keyword-cannibalization", urlId)]);

        var finding = Assert.Single(persisted);
        Assert.True(finding.Id > 0);
        Assert.False(finding.IsSuppressed);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(auditId, evidence.RootElement.GetProperty("firstSeenAuditId").GetInt64());
        Assert.Equal("x", evidence.RootElement.GetProperty("detail").GetString());
        Assert.True(evidence.RootElement.GetProperty("priority").GetInt32() > 0);
        Assert.Equal("medium", evidence.RootElement.GetProperty("remediationDifficulty").GetString());

        var readBack = await _stack.Findings.GetByIdAsync(finding.Id);
        Assert.NotNull(readBack);
        Assert.Equal("content.keyword-cannibalization", readBack.RuleKey);
    }

    [Fact]
    public async Task RuleScopedSuppressionFlagsAllFindingsOfThatRule()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urlA = await _stack.CreateUrlAsync(auditId);
        var urlB = await _stack.CreateUrlAsync(auditId, "/page2");
        await AddSuppression(projectId, "rule.suppressed");
        var emitter = CreateEmitter();

        var persisted = await emitter.EmitAsync(auditId, projectId,
            [Draft("rule.suppressed", urlA), Draft("rule.suppressed", urlB), Draft("rule.other", urlA)]);

        Assert.True(persisted[0].IsSuppressed);
        Assert.True(persisted[1].IsSuppressed);
        Assert.False(persisted[2].IsSuppressed);
    }

    [Fact]
    public async Task UrlScopedSuppressionOnlyCoversThatUrl()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urlA = await _stack.CreateUrlAsync(auditId);
        var urlB = await _stack.CreateUrlAsync(auditId, "/page2");
        await AddSuppression(projectId, "rule.x", urlId: urlA);
        var emitter = CreateEmitter();

        var persisted = await emitter.EmitAsync(auditId, projectId,
            [Draft("rule.x", urlA), Draft("rule.x", urlB)]);

        Assert.True(persisted[0].IsSuppressed);
        Assert.False(persisted[1].IsSuppressed);
    }

    [Fact]
    public async Task SuppressionsAreProjectScoped()
    {
        var (auditA, projectA, _) = await _stack.CreateAuditAsync();
        var (auditB, projectB, _) = await _stack.CreateAuditAsync();
        var urlA = await _stack.CreateUrlAsync(auditA);
        var urlB = await _stack.CreateUrlAsync(auditB);
        await AddSuppression(projectA, "rule.x");
        var emitter = CreateEmitter();

        var inA = await emitter.EmitAsync(auditA, projectA, [Draft("rule.x", urlA)]);
        var inB = await emitter.EmitAsync(auditB, projectB, [Draft("rule.x", urlB)]);

        Assert.True(inA[0].IsSuppressed);
        Assert.False(inB[0].IsSuppressed);
    }

    [Fact]
    public async Task SuppressionsAreReversibleAndCarryAcrossReAudits()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId);
        var suppression = await AddSuppression(projectId, "rule.x");
        var emitter = CreateEmitter();

        var first = await emitter.EmitAsync(auditId, projectId, [Draft("rule.x", urlId)]);
        Assert.True(first[0].IsSuppressed);

        // Re-audit of the same project: suppression still applies.
        var reAudit = await emitter.EmitAsync(auditId, projectId, [Draft("rule.x", urlId)]);
        Assert.True(reAudit[0].IsSuppressed);

        // Reverse the suppression; new emissions are no longer flagged.
        await _stack.Suppressions.UpdateAsync(suppression with { IsSuppressed = false });
        var after = await emitter.EmitAsync(auditId, projectId, [Draft("rule.x", urlId)]);
        Assert.False(after[0].IsSuppressed);
    }

    [Fact]
    public void EmptySuppressionRowSuppressesNothing()
    {
        var empty = new FindingSuppressionDto(1, 1, "k", null, null, null, true, DateTimeOffset.UtcNow);
        Assert.False(FindingEmitter.IsSuppressed(Draft("rule.x"), [empty]));
    }

    [Fact]
    public void FindingScopeDescriptorMatchesAgainstTheRuleKey()
    {
        var scoped = new FindingSuppressionDto(1, 1, "k", null, null, "rule.x", true, DateTimeOffset.UtcNow);
        Assert.True(FindingEmitter.IsSuppressed(Draft("rule.x"), [scoped]));
        Assert.False(FindingEmitter.IsSuppressed(Draft("rule.y"), [scoped]));
    }

    [Fact]
    public async Task SuppressedFindingsRemainPersistedAndInspectable()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId);
        await AddSuppression(projectId, "rule.x");
        var emitter = CreateEmitter();

        var persisted = await emitter.EmitAsync(auditId, projectId, [Draft("rule.x", urlId)]);
        var readBack = await _stack.Findings.GetByIdAsync(persisted[0].Id);

        Assert.NotNull(readBack);
        Assert.True(readBack.IsSuppressed);
        Assert.Equal("rule.x", readBack.RuleKey);
    }
}
