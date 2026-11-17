using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.Tests.Geo.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Geo;

/// <summary>
/// GEO readiness analyzer over the real stored-data stack (§13): offline
/// llms.txt discovery/validation, crawled-set link verification, readiness
/// checks, and metric recording. Zero network access.
/// </summary>
public sealed class GeoReadinessTests
{
    private const string WellFormedLlms = """
        # Example Site

        > Example Site platform documentation.

        ## Docs

        - [Start](https://example.test/docs/a)
        - [API](/docs/b)
        """;

    private static GeoReadinessAnalyzer CreateAnalyzer(GeoRealStack stack) =>
        new(stack.Provider, stack.ContentStore, new TestDomParser());

    [Fact]
    public async Task MissingLlmsTxt_EmitsNotice()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await stack.AddUrlAsync("https://example.test/");
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        var finding = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.GeoLlmsTxtMissing);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Equal(AuditCategory.AiSearchGeo, finding.Category);
        var host = Assert.Single(result.HostResults);
        Assert.False(host.IsPresent);
        Assert.Equal("example.test", host.Host);
    }

    [Fact]
    public async Task WellFormedLlms_WithCrawledLinks_AndFullVariant_HasNoGeoFindings()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var llms = await stack.AddUrlAsync("https://example.test/llms.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(llms.Id, WellFormedLlms);
        var full = await stack.AddUrlAsync("https://example.test/llms-full.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(full.Id, WellFormedLlms);
        await stack.AddUrlAsync("https://example.test/docs/a");
        await stack.AddUrlAsync("https://example.test/docs/b");
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        Assert.Empty(result.Findings);
        var host = Assert.Single(result.HostResults);
        Assert.True(host.IsPresent);
        Assert.True(host.IsWellFormed);
        Assert.Empty(host.BrokenLinks);
    }

    [Fact]
    public async Task AbsentLlmsFull_WhileLlmsPresent_IsNoticeAnnotation()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var llms = await stack.AddUrlAsync("https://example.test/llms.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(llms.Id, WellFormedLlms);
        await stack.AddUrlAsync("https://example.test/docs/a");
        await stack.AddUrlAsync("https://example.test/docs/b");
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        var annotation = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.GeoLlmsTxtMissing);
        Assert.Equal(FindingSeverity.Notice, annotation.Severity);
        using var evidence = JsonDocument.Parse(annotation.EvidenceJson);
        Assert.Equal("llms-full.txt", evidence.RootElement.GetProperty("file").GetString());
        Assert.True(evidence.RootElement.GetProperty("annotation").GetBoolean());
    }

    [Fact]
    public async Task LinkToNonSuccessCrawledUrl_IsBrokenWarning()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var llms = await stack.AddUrlAsync("https://example.test/llms.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(llms.Id, WellFormedLlms);
        await stack.AddUrlAsync("https://example.test/docs/a");
        await stack.AddUrlAsync("https://example.test/docs/b", statusCode: 404);
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        var broken = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.GeoLlmsTxtBrokenLink);
        Assert.Equal(FindingSeverity.Warning, broken.Severity);
        using var evidence = JsonDocument.Parse(broken.EvidenceJson);
        Assert.Equal("non-success-status", evidence.RootElement.GetProperty("reason").GetString());
        Assert.Equal("https://example.test/docs/b", evidence.RootElement.GetProperty("target").GetString());
        Assert.Contains("https://example.test/docs/b", result.HostResults[0].BrokenLinks);
    }

    [Fact]
    public async Task LinkOutsideCrawledSet_IsBrokenWarning()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var llms = await stack.AddUrlAsync("https://example.test/llms.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(llms.Id, WellFormedLlms);
        await stack.AddUrlAsync("https://example.test/docs/a");
        // /docs/b deliberately NOT crawled.
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        var broken = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.GeoLlmsTxtBrokenLink);
        using var evidence = JsonDocument.Parse(broken.EvidenceJson);
        Assert.Equal("not-in-crawled-set", evidence.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task MalformedLlmsTxt_EmitsWarningWithIssues()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var llms = await stack.AddUrlAsync("https://example.test/llms.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(llms.Id, "no structure here at all\n");
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        var malformed = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.GeoLlmsTxtMalformed);
        Assert.Equal(FindingSeverity.Warning, malformed.Severity);
        Assert.False(result.HostResults[0].IsWellFormed);
        Assert.Equal(3, result.HostResults[0].Issues.Count);
    }

    [Fact]
    public async Task ReadinessChecks_ScorePerfectSite_At100()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var llms = await stack.AddUrlAsync("https://example.test/llms.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(llms.Id, WellFormedLlms);
        var full = await stack.AddUrlAsync("https://example.test/llms-full.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(full.Id, WellFormedLlms);
        await stack.AddUrlAsync("https://example.test/docs/a");
        await stack.AddUrlAsync("https://example.test/docs/b");

        var passage = string.Join(' ', Enumerable.Repeat("word", 40));
        var page = await stack.AddUrlAsync("https://example.test/");
        await stack.StoreBodyAsync(page.Id,
            $"<html><body><p>{passage}</p><script type=\"application/ld+json\">{{\"@type\":\"Organization\"}}</script></body></html>");
        var analyzer = CreateAnalyzer(stack);

        var (result, checks) = await analyzer.AnalyzeWithChecksAsync(stack.AuditId);

        Assert.Equal(100.0, result.ReadinessScore);
        Assert.Equal(5, checks.Count);
        Assert.All(checks, c => Assert.Equal(1.0, c.Score));
    }

    [Fact]
    public async Task EmptyAudit_ScoresZero()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var analyzer = CreateAnalyzer(stack);

        var result = await analyzer.AnalyzeAsync(stack.AuditId);

        Assert.Equal(0.0, result.ReadinessScore);
        Assert.Empty(result.Findings);
        Assert.Empty(result.HostResults);
    }

    [Fact]
    public async Task ReadinessMetrics_ReplaceOnlyGeoKey_AndAreIdempotent()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await stack.AddUrlAsync("https://example.test/");
        var analyzer = CreateAnalyzer(stack);
        var (result, checks) = await analyzer.AnalyzeWithChecksAsync(stack.AuditId);
        var recorder = new GeoAggregateRecorder(stack.Provider);

        // Foreign metric row must survive replace semantics.
        await using (var connection = await stack.Provider.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO metric_summaries (audit_id, metric_key, dimensions_json, metric_value) " +
                "VALUES (@a, 'foreign-key', '{}', 1)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@a";
            parameter.Value = stack.AuditId;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync();
        }

        var written = await recorder.RecordReadinessAsync(stack.AuditId, checks, result.ReadinessScore);
        Assert.Equal(6, written);
        await recorder.RecordReadinessAsync(stack.AuditId, checks, result.ReadinessScore);

        await using var verify = await stack.Provider.OpenConnectionAsync();
        await using var countCommand = verify.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM metric_summaries WHERE audit_id = @a AND metric_key = 'geo-readiness'";
        var p1 = countCommand.CreateParameter();
        p1.ParameterName = "@a";
        p1.Value = stack.AuditId;
        countCommand.Parameters.Add(p1);
        Assert.Equal(6L, await countCommand.ExecuteScalarAsync());

        await using var foreignCommand = verify.CreateCommand();
        foreignCommand.CommandText = "SELECT COUNT(*) FROM metric_summaries WHERE audit_id = @a AND metric_key = 'foreign-key'";
        var p2 = foreignCommand.CreateParameter();
        p2.ParameterName = "@a";
        p2.Value = stack.AuditId;
        foreignCommand.Parameters.Add(p2);
        Assert.Equal(1L, await foreignCommand.ExecuteScalarAsync());
    }
}
