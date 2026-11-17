using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// All five prompts (FS-CRAWL-001 §11, Appendix D): composition from read-only
/// resources, declared arguments, and the side-effect-free guarantee.
/// </summary>
public class McpPromptSurfaceTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public McpPromptSurfaceTests(McpSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private async Task<long> SeedAsync()
    {
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/prompted");
        await _stack.Findings.CreateAsync(new FindingDto(0, auditId, "content.missing-title", AuditCategory.Content, FindingSeverity.Error, urlId, "{\"detail\":true}", "rec.title", false));
        await _stack.Scores.CreateAsync(new AuditScoreDto(0, auditId, null, 75, 1, 0, 0, 1));
        await _stack.NgramSiteSummaries.CreateAsync(new NgramSiteSummaryDto(0, auditId, TextZone.Body, 2, "semantic linking", 1, 2, 0.2, 1));
        await _stack.Opportunities.CreateAsync(new SemanticLinkOpportunityDto(0, auditId, urlId, urlId, 0.8, null));
        return auditId;
    }

    [Fact]
    public async Task ListsAllFivePromptsWithArguments()
    {
        var host = new McpTestHost(_stack);
        var prompts = await host.Prompts.ListPromptsAsync();
        Assert.Equal(5, prompts.Count);
        Assert.Equal(
            new[] { "audit-health-briefing", "high-severity-triage", "audit-delta-briefing", "ngram-topical-briefing", "semantic-topical-briefing" },
            prompts.Select(p => p.Name).ToArray());
        var delta = prompts.Single(p => p.Name == "audit-delta-briefing");
        Assert.Equal(2, delta.Arguments.Count);
        Assert.All(delta.Arguments, a => Assert.True(a.Required));
    }

    [Fact]
    public async Task AuditHealthBriefingComposesSummary()
    {
        var auditId = await SeedAsync();
        var host = new McpTestHost(_stack);
        var content = await host.Prompts.GetPromptAsync("audit-health-briefing",
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() });
        var message = Assert.Single(content.Messages);
        Assert.Equal("user", message.Role);
        Assert.Contains("75", message.Text);
        Assert.Contains("Audit summary", message.Text);
    }

    [Fact]
    public async Task HighSeverityTriageComposesFindings()
    {
        var auditId = await SeedAsync();
        var host = new McpTestHost(_stack);
        var content = await host.Prompts.GetPromptAsync("high-severity-triage",
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() });
        Assert.Contains("content.missing-title", Assert.Single(content.Messages).Text);
    }

    [Fact]
    public async Task AuditDeltaBriefingComposesComparison()
    {
        var baseAuditId = await SeedAsync();
        var (targetAuditId, _, _) = await _stack.CreateAuditAsync();
        var host = new McpTestHost(_stack);
        var content = await host.Prompts.GetPromptAsync("audit-delta-briefing",
            new Dictionary<string, string>
            {
                ["baseAuditId"] = baseAuditId.ToString(),
                ["targetAuditId"] = targetAuditId.ToString(),
            });
        var text = Assert.Single(content.Messages).Text;
        Assert.Contains("newFindings", text);
        Assert.Contains("resolvedFindings", text);
    }

    [Fact]
    public async Task NgramTopicalBriefingComposesNgramsAndSummary()
    {
        var auditId = await SeedAsync();
        var host = new McpTestHost(_stack);
        var content = await host.Prompts.GetPromptAsync("ngram-topical-briefing",
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() });
        var text = Assert.Single(content.Messages).Text;
        Assert.Contains("semantic linking", text);
        Assert.Contains("Audit summary", text);
    }

    [Fact]
    public async Task SemanticTopicalBriefingComposesSemanticsAndSummary()
    {
        var auditId = await SeedAsync();
        var host = new McpTestHost(_stack);
        var content = await host.Prompts.GetPromptAsync("semantic-topical-briefing",
            new Dictionary<string, string> { ["auditId"] = auditId.ToString() });
        var text = Assert.Single(content.Messages).Text;
        Assert.Contains("suggestions only", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Audit summary", text);
    }

    [Fact]
    public async Task PromptMaterializationHasNoSideEffects()
    {
        var auditId = await SeedAsync();
        var host = new McpTestHost(_stack);
        var findingsBefore = (await _stack.Findings.ListAsync(new Contracts.Common.ListQuery())).TotalCount;
        var auditsBefore = (await _stack.Audits.ListAsync(new Contracts.Common.ListQuery())).TotalCount;
        var urlsBefore = (await _stack.Urls.ListAsync(new Contracts.Common.ListQuery())).TotalCount;

        foreach (var name in new[] { "audit-health-briefing", "high-severity-triage", "ngram-topical-briefing", "semantic-topical-briefing" })
        {
            await host.Prompts.GetPromptAsync(name, new Dictionary<string, string> { ["auditId"] = auditId.ToString() });
        }

        Assert.Equal(findingsBefore, (await _stack.Findings.ListAsync(new Contracts.Common.ListQuery())).TotalCount);
        Assert.Equal(auditsBefore, (await _stack.Audits.ListAsync(new Contracts.Common.ListQuery())).TotalCount);
        Assert.Equal(urlsBefore, (await _stack.Urls.ListAsync(new Contracts.Common.ListQuery())).TotalCount);
    }

    [Fact]
    public async Task MissingPromptArgumentIsRejected()
    {
        var host = new McpTestHost(_stack);
        await Assert.ThrowsAsync<McpArgumentMissingException>(
            () => host.Prompts.GetPromptAsync("audit-health-briefing", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task UnknownPromptIsRejected()
    {
        var host = new McpTestHost(_stack);
        await Assert.ThrowsAsync<McpArgumentMissingException>(
            () => host.Prompts.GetPromptAsync("no-such-prompt", null));
    }
}
