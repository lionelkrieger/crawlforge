using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// All fifteen read-only resources (FS-CRAWL-001 §11.4 + Appendix D) over the
/// real stack: baseline and additive reads, additive parent fields, filters,
/// bounds, and unknown-URI rejection.
/// </summary>
public class McpResourceSurfaceTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public McpResourceSurfaceTests(McpSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private async Task<(long AuditId, long UrlId, long LogImportId)> SeedAsync()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/page-a");
        await _stack.CreateUrlAsync(auditId, "/page-b");
        await _stack.StoreBodyAsync(auditId, urlId, "<html><head><title>Page A</title></head><body><p>hello world</p></body></html>");

        await _stack.Findings.CreateAsync(new FindingDto(0, auditId, "content.missing-title", AuditCategory.Content, FindingSeverity.Error, urlId, "{\"x\":1}", "rec.missing-title", false));
        await _stack.Findings.CreateAsync(new FindingDto(0, auditId, "linking.broken-link", AuditCategory.Linking, FindingSeverity.Warning, urlId, "{}", null, false));
        await _stack.Findings.CreateAsync(new FindingDto(0, auditId, "content.notice", AuditCategory.Content, FindingSeverity.Notice, null, "{}", null, false));
        await _stack.Findings.CreateAsync(new FindingDto(0, auditId, "header.canonical-conflict", AuditCategory.Indexability, FindingSeverity.Warning, urlId, "{}", null, false));
        await _stack.Scores.CreateAsync(new AuditScoreDto(0, auditId, null, 82.5, 1, 2, 1, 4));
        await _stack.Metrics.CreateAsync(new MetricSummaryDto(0, auditId, "serp.pixel-budget", "{}", 1234));
        await _stack.WafDetections.CreateAsync(new WafDetectionDto(0, auditId, urlId, WafProvider.Cloudflare, WafChallengeClass.Interstitial, "{}", "cf-js", true, DateTimeOffset.UtcNow));
        await _stack.ThrottleEvents.CreateAsync(new HostThrottleEventDto(0, auditId, "example.test", 2.0, 1.0, "aimd-backoff", DateTimeOffset.UtcNow));
        await _stack.RedirectMappings.CreateAsync(new RedirectMappingDto(0, auditId, "https://example.test/old", "https://example.test/page-a", 0));
        await _stack.Opportunities.CreateAsync(new SemanticLinkOpportunityDto(0, auditId, urlId, urlId + 1, 0.82, "[\"hello world\"]"));
        await _stack.Clusters.CreateAsync(new SemanticClusterDto(0, auditId, 1, urlId, 0.95));
        await _stack.AnalyticsDetections.CreateAsync(new AnalyticsDetectionDto(0, auditId, urlId, "google-analytics", "G-ABC123", "dom", "head"));
        await _stack.ExtractedContent.CreateAsync(new ExtractedContentDto(0, auditId, urlId, "price", 0, "\"19.99\"", ExtractedValueType.Number, false, false));
        await _stack.GovernanceRows.CreateAsync(new GovernanceMatrixRowDto(0, auditId, "GPTBot", "/", AiAgentPurpose.Training, GovernanceVerdict.Deny, "{}"));
        await _stack.NgramFrequencies.CreateAsync(new NgramFrequencyDto(0, auditId, urlId, TextZone.Body, 2, "hello world", 3, 0.1));
        await _stack.NgramSiteSummaries.CreateAsync(new NgramSiteSummaryDto(0, auditId, TextZone.Body, 2, "hello world", 2, 5, 0.1, 1));

        var import = await _stack.LogImports.CreateAsync(new LogImportDto(
            0, projectId, LogFileFormat.ApacheNginxCombined,
            DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, "{}",
            BotVerificationMode.CidrOnly, LogImportStatus.Completed, DateTimeOffset.UtcNow, null));
        await _stack.LogAggregates.CreateAsync(new LogUrlAggregateDto(0, import.Id, "https://example.test/page-a", "example.test", 10, 6, null, null));

        var schedule = await _stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
            0, projectId, 1, "weekly", true, null, null, DateTimeOffset.UtcNow.AddDays(1)));
        await _stack.ScheduleRuns.CreateAsync(new ScheduleRunDto(
            0, schedule.Id, auditId, ScheduleTriggerType.Scheduled, ScheduleRunOutcome.Success, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        return (auditId, urlId, import.Id);
    }

    private async Task<JsonElement> ReadAsync(string uri)
    {
        var surface = new McpResourceSurface(_stack.BuildPorts());
        var content = await surface.ReadResourceAsync(uri);
        Assert.Equal("application/json", content.MimeType);
        Assert.Equal(uri, content.Uri);
        return JsonDocument.Parse(content.Text).RootElement;
    }

    [Fact]
    public async Task ListsAllFifteenResources()
    {
        var surface = new McpResourceSurface(_stack.BuildPorts());
        var definitions = await surface.ListResourcesAsync();
        Assert.Equal(15, definitions.Count);
        Assert.Contains(definitions, d => d.Name == "audit-summary");
        Assert.Contains(definitions, d => d.Name == "semantic-link-opportunities");
        Assert.Contains(definitions, d => d.Name == "analytics-coverage");
        Assert.All(definitions, d => Assert.StartsWith("crawlforge://", d.Uri));
    }

    [Fact]
    public async Task AuditSummaryIncludesScoresMetricsCountsAndWaf()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/summary");
        Assert.Equal(auditId, payload.GetProperty("audit").GetProperty("id").GetInt64());
        Assert.Equal(82.5, payload.GetProperty("scores")[0].GetProperty("score").GetDouble());
        Assert.Equal(1, payload.GetProperty("findingCountsBySeverity").GetProperty("Error").GetInt32());
        Assert.Equal(1, payload.GetProperty("waf").GetProperty("detectionCount").GetInt32());
        Assert.Equal(1, payload.GetProperty("waf").GetProperty("resolvedByRendering").GetInt32());
    }

    [Fact]
    public async Task HighSeverityReturnsOnlyErrorAndWarning()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/high-severity-findings");
        var findings = payload.GetProperty("findings");
        Assert.Equal(3, findings.GetArrayLength()); // Error + 2 Warning; Notice excluded
        Assert.All(findings.EnumerateArray(), f =>
            Assert.Contains(f.GetProperty("severity").GetString(), new[] { "Error", "Warning" }));
    }

    [Fact]
    public async Task UrlDetailCarriesAdditiveDiagnostics()
    {
        var (auditId, urlId, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/urls/{urlId}");
        Assert.Equal(urlId, payload.GetProperty("url").GetProperty("id").GetInt64());
        Assert.True(payload.GetProperty("findings").GetArrayLength() >= 3);
        Assert.Equal(1, payload.GetProperty("wafDetection").GetArrayLength());
        Assert.Equal("hello world", payload.GetProperty("topNgrams")[0].GetProperty("term").GetString());
        Assert.Equal(1, payload.GetProperty("serpMetrics").GetArrayLength());
        Assert.Equal(1, payload.GetProperty("headerConflicts").GetArrayLength());
        Assert.Equal(1, payload.GetProperty("semanticRelations").GetProperty("opportunities").GetArrayLength());
        Assert.Equal(1, payload.GetProperty("semanticRelations").GetProperty("clusters").GetArrayLength());
    }

    [Fact]
    public async Task AuditListReturnsAudits()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync("crawlforge://audits");
        Assert.Contains(payload.GetProperty("audits").EnumerateArray(),
            a => a.GetProperty("id").GetInt64() == auditId);
    }

    [Fact]
    public async Task ScheduleStatusReturnsDefinitionsWithRuns()
    {
        await SeedAsync();
        var payload = await ReadAsync("crawlforge://schedules");
        Assert.True(payload.GetProperty("schedules").GetArrayLength() >= 1);
        Assert.True(payload.GetProperty("recentRuns").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task CrawlProgressIncludesThrottleAndMatrixCounters()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/progress");
        Assert.Equal(auditId, payload.GetProperty("progress").GetProperty("auditId").GetInt64());
        Assert.Equal(1.0, payload.GetProperty("throttleSummary").GetProperty("example.test").GetDouble());
        Assert.Equal(1, payload.GetProperty("matrixCounters").GetProperty("redirectMappings").GetInt32());
    }

    [Fact]
    public async Task ExtractedContentReturnsValuesAndBodyText()
    {
        var (auditId, urlId, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/urls/{urlId}/content");
        Assert.Equal("price", payload.GetProperty("extractedValues")[0].GetProperty("ruleName").GetString());
        Assert.Contains("hello world", payload.GetProperty("bodyText").GetString());
    }

    [Fact]
    public async Task NgramAnalysisReadsSiteSummaries()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/ngrams");
        Assert.Equal(1, payload.GetProperty("ngrams").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task WafBlocksReturnsDetections()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/waf-blocks");
        Assert.Equal("Cloudflare", payload.GetProperty("detections")[0].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task HostThrottleReturnsEvents()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/host-throttle");
        Assert.Equal("aimd-backoff", payload.GetProperty("events")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task RedirectMatrixReturnsRowsAndCounters()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/redirect-matrix");
        Assert.Equal(1, payload.GetProperty("counters").GetProperty("total").GetInt32());
        Assert.Equal("https://example.test/old", payload.GetProperty("mappings")[0].GetProperty("sourceUrl").GetString());
    }

    [Fact]
    public async Task LogCorrelationReturnsViewsForImport()
    {
        var (auditId, _, importId) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/log-correlation?import={importId}");
        Assert.Equal(importId, payload.GetProperty("logImportId").GetInt64());
        Assert.True(payload.GetProperty("correlation").TryGetProperty("trueOrphans", out _));
        Assert.True(payload.GetProperty("correlation").TryGetProperty("budgetWaste", out _));
        Assert.True(payload.GetProperty("correlation").TryGetProperty("uncrawledIndexable", out _));
    }

    [Fact]
    public async Task LogCorrelationWithoutImportIsRejected()
    {
        var (auditId, _, _) = await SeedAsync();
        var surface = new McpResourceSurface(_stack.BuildPorts());
        await Assert.ThrowsAsync<McpResourceNotFoundException>(
            () => surface.ReadResourceAsync($"crawlforge://audits/{auditId}/log-correlation"));
    }

    [Fact]
    public async Task SemanticLinksReturnsOpportunitiesAndClusters()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/semantic-links");
        Assert.Equal(0.82, payload.GetProperty("opportunities")[0].GetProperty("similarity").GetDouble());
        Assert.Equal(1, payload.GetProperty("clusters").GetArrayLength());
        Assert.Contains("Suggestions only", payload.GetProperty("note").GetString());
    }

    [Fact]
    public async Task GeoReadinessIncludesGovernanceSummaryAndAgentFilter()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/geo-readiness");
        Assert.Equal(1, payload.GetProperty("governanceSummary").GetProperty("deny").GetInt32());
        Assert.True(payload.TryGetProperty("readinessScore", out _));

        var filtered = await ReadAsync($"crawlforge://audits/{auditId}/geo-readiness?agent=GPTBot");
        Assert.Equal(1, filtered.GetProperty("governanceSummary").GetProperty("rows").GetArrayLength());
        var none = await ReadAsync($"crawlforge://audits/{auditId}/geo-readiness?agent=NoSuchBot");
        Assert.Equal(0, none.GetProperty("governanceSummary").GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task AnalyticsCoverageGroupsByContainerWithVendorFilter()
    {
        var (auditId, _, _) = await SeedAsync();
        var payload = await ReadAsync($"crawlforge://audits/{auditId}/analytics-coverage");
        Assert.Equal(2, payload.GetProperty("indexableUrlCount").GetInt32());
        Assert.Equal(1, payload.GetProperty("coveredUrlCount").GetInt32());
        Assert.Equal("G-ABC123", payload.GetProperty("byContainer")[0].GetProperty("containerId").GetString());

        var filtered = await ReadAsync($"crawlforge://audits/{auditId}/analytics-coverage?vendor=google");
        Assert.Equal(1, filtered.GetProperty("detections").GetArrayLength());
        var none = await ReadAsync($"crawlforge://audits/{auditId}/analytics-coverage?vendor=nope");
        Assert.Equal(0, none.GetProperty("detections").GetArrayLength());
    }

    [Theory]
    [InlineData("crawlforge://unknown")]
    [InlineData("crawlforge://audits/not-a-number/summary")]
    [InlineData("https://example.test/audits")]
    [InlineData("crawlforge://audits/1/nope")]
    public async Task UnknownOrMalformedUrisAreRejected(string uri)
    {
        var surface = new McpResourceSurface(_stack.BuildPorts());
        await Assert.ThrowsAsync<McpResourceNotFoundException>(() => surface.ReadResourceAsync(uri));
    }

    [Fact]
    public async Task UrlDetailForForeignAuditIsRejected()
    {
        var (auditId, urlId, _) = await SeedAsync();
        var (otherAuditId, _, _) = await _stack.CreateAuditAsync();
        var surface = new McpResourceSurface(_stack.BuildPorts());
        await Assert.ThrowsAsync<McpResourceNotFoundException>(
            () => surface.ReadResourceAsync($"crawlforge://audits/{otherAuditId}/urls/{urlId}"));
        _ = auditId;
    }
}
