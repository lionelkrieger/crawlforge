using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.Tests.Geo.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Geo;

/// <summary>
/// Advisory AI-bot governance matrix (§13.2, criterion #46) over the real
/// stored-data stack, using the ACCEPTED Crawler robots parser (injected via
/// the locked interface — never duplicated). The matrix must never modify
/// robots.txt or crawl state.
/// </summary>
public sealed class GovernanceMatrixTests
{
    private const string RobotsBody = "User-agent: GPTBot\nDisallow: /private/\n\nUser-agent: *\nAllow: /\n";

    // Explicit Allow for GPTBot outside /private/ — needed for conflict tests:
    // a conflict requires an EXPLICIT robots verdict (Unspecified is not Allow).
    private const string RobotsBodyExplicitAllow =
        "User-agent: GPTBot\nAllow: /\nDisallow: /private/\n\nUser-agent: *\nAllow: /\n";

    private static AiBotGovernanceMatrix CreateMatrix(GeoRealStack stack) =>
        new(stack.Provider, stack.ContentStore, new RobotsTxtParser(),
            AiAgentProfileCatalogue.Load(GeoRealStack.AgentProfileAssetPath), new TestDomParser());

    private static async Task SeedRobotsAsync(GeoRealStack stack, string? body = null)
    {
        var robots = await stack.AddUrlAsync("https://example.test/robots.txt", contentType: "text/plain");
        await stack.StoreBodyAsync(robots.Id, body ?? RobotsBody);
    }

    [Fact]
    public async Task RobotsDeny_ProducesPrefixRows_ForAllPurposes()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack);
        await stack.AddUrlAsync("https://example.test/private/secret");
        await stack.AddUrlAsync("https://example.test/public/page");
        var matrix = CreateMatrix(stack);

        var rows = await matrix.EvaluateAsync(stack.AuditId);

        var gptPrivate = rows.Where(r =>
            r.AgentName == "GPTBot" && r.ScopeValue == "example.test/private/").ToArray();
        Assert.Equal(3, gptPrivate.Length);
        Assert.All(gptPrivate, r => Assert.Equal(GovernanceVerdict.Deny, r.Verdict));

        var googlePrivate = rows.Where(r =>
            r.AgentName == "Googlebot" && r.ScopeValue == "example.test/private/").ToArray();
        Assert.Equal(3, googlePrivate.Length);
        Assert.All(googlePrivate, r => Assert.Equal(GovernanceVerdict.Allow, r.Verdict));

        // Scope kind rides in evidence (locked schema has no scope_kind column).
        using var evidence = JsonDocument.Parse(gptPrivate[0].EvidenceJson);
        Assert.Equal("prefix", evidence.RootElement.GetProperty("scopeKind").GetString());
    }

    [Fact]
    public async Task AgentQualifiedXRobotsTag_Noai_DeniesRagAndTraining_WithConflictEvidence()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack, RobotsBodyExplicitAllow);
        var page = await stack.AddUrlAsync("https://example.test/public/page");
        await stack.StoreBodyAsync(page.Id, "<html><body><p>content</p></body></html>");
        await stack.AddElementAsync(page.Id, "header_directives", "{\"xRobotsTag\":\"GPTBot: noai\"}");
        var matrix = CreateMatrix(stack);

        var (rows, findings) = await matrix.EvaluateWithFindingsAsync(stack.AuditId);

        var urlRows = rows.Where(r => r.AgentName == "GPTBot" && r.ScopeValue == page.CanonicalUrl).ToArray();
        Assert.Equal(GovernanceVerdict.Allow, urlRows.Single(r => r.Purpose == AiAgentPurpose.Search).Verdict);
        Assert.Equal(GovernanceVerdict.Deny, urlRows.Single(r => r.Purpose == AiAgentPurpose.Rag).Verdict);
        Assert.Equal(GovernanceVerdict.Deny, urlRows.Single(r => r.Purpose == AiAgentPurpose.Training).Verdict);

        // robots Allow vs directive Deny → one conflict per denied purpose.
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal(FindingRuleKeys.GeoGovernanceConflict, f.RuleKey);
            Assert.Equal(FindingSeverity.Notice, f.Severity);
            using var evidence = JsonDocument.Parse(f.EvidenceJson);
            Assert.Equal("Allow", evidence.RootElement.GetProperty("robotsVerdict").GetString());
            Assert.Equal("Deny", evidence.RootElement.GetProperty("directiveVerdict").GetString());
            Assert.Equal("GPTBot", evidence.RootElement.GetProperty("agent").GetString());
        });

        // Other agents are unaffected by the GPTBot-qualified directive.
        var perplexity = rows.Single(r =>
            r.AgentName == "PerplexityBot" && r.ScopeValue == page.CanonicalUrl && r.Purpose == AiAgentPurpose.Rag);
        Assert.Equal(GovernanceVerdict.Allow, perplexity.Verdict);
    }

    [Fact]
    public async Task MetaRobotsNoindex_DeniesSearch_ForEveryAgent()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack, RobotsBodyExplicitAllow);
        var page = await stack.AddUrlAsync("https://example.test/public/page");
        await stack.StoreBodyAsync(page.Id,
            "<html><head><meta name=\"robots\" content=\"noindex\"></head><body><p>content</p></body></html>");
        var matrix = CreateMatrix(stack);
        var catalogue = AiAgentProfileCatalogue.Load(GeoRealStack.AgentProfileAssetPath);

        var (rows, findings) = await matrix.EvaluateWithFindingsAsync(stack.AuditId);

        var searchRows = rows.Where(r => r.ScopeValue == page.CanonicalUrl && r.Purpose == AiAgentPurpose.Search).ToArray();
        Assert.Equal(catalogue.Profiles.Count, searchRows.Length);
        Assert.All(searchRows, r => Assert.Equal(GovernanceVerdict.Deny, r.Verdict));
        Assert.Equal(catalogue.Profiles.Count, findings.Count);
    }

    [Fact]
    public async Task AgentSpecificMetaTag_AppliesOnlyToThatAgent()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack, RobotsBodyExplicitAllow);
        var page = await stack.AddUrlAsync("https://example.test/public/page");
        await stack.StoreBodyAsync(page.Id,
            "<html><head><meta name=\"ClaudeBot\" content=\"noai\"></head><body><p>content</p></body></html>");
        var matrix = CreateMatrix(stack);

        var rows = await matrix.EvaluateAsync(stack.AuditId);

        Assert.Equal(GovernanceVerdict.Deny, rows.Single(r =>
            r.AgentName == "ClaudeBot" && r.ScopeValue == page.CanonicalUrl && r.Purpose == AiAgentPurpose.Rag).Verdict);
        Assert.Equal(GovernanceVerdict.Deny, rows.Single(r =>
            r.AgentName == "ClaudeBot" && r.ScopeValue == page.CanonicalUrl && r.Purpose == AiAgentPurpose.Training).Verdict);
        Assert.Equal(GovernanceVerdict.Allow, rows.Single(r =>
            r.AgentName == "GPTBot" && r.ScopeValue == page.CanonicalUrl && r.Purpose == AiAgentPurpose.Rag).Verdict);
    }

    [Fact]
    public async Task AdvisoryInvariant_NeverModifiesRobotsOrCrawlState()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack);
        var page = await stack.AddUrlAsync("https://example.test/public/page");
        await stack.StoreBodyAsync(page.Id, "<html><body><p>content</p></body></html>");
        var matrix = CreateMatrix(stack);
        var recorder = new GovernanceMatrixRecorder(stack.Provider);

        var filesBefore = StableFiles(stack.RootDirectory);
        var robotsStateBefore = await ReadRobotsStatesAsync(stack);
        var artifactCountBefore = await CountArtifactsAsync(stack);

        var rows = await matrix.EvaluateAsync(stack.AuditId);
        await recorder.RecordAsync(stack.AuditId, rows);

        var filesAfter = StableFiles(stack.RootDirectory);
        Assert.Equal(filesBefore, filesAfter);
        Assert.DoesNotContain(filesAfter, f => Path.GetFileName(f) == "robots.txt");
        Assert.Equal(robotsStateBefore, await ReadRobotsStatesAsync(stack));
        Assert.Equal(artifactCountBefore, await CountArtifactsAsync(stack));
    }

    [Fact]
    public async Task Recorder_ReplaceSemantics_AreIdempotent()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack);
        await stack.AddUrlAsync("https://example.test/public/page");
        var matrix = CreateMatrix(stack);
        var recorder = new GovernanceMatrixRecorder(stack.Provider);

        var rows = await matrix.EvaluateAsync(stack.AuditId);
        var first = await recorder.RecordAsync(stack.AuditId, rows);
        var second = await recorder.RecordAsync(stack.AuditId, rows);

        Assert.Equal(first, second);
        Assert.Equal(rows.Count, first);
        var persisted = await recorder.GetRowsAsync(stack.AuditId);
        Assert.Equal(rows.Count, persisted.Count);
        Assert.All(persisted, r => Assert.True(r.Id > 0));
    }

    [Fact]
    public async Task GovernanceCoverage_RecordsPerPurposeAndOverall()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedRobotsAsync(stack);
        await stack.AddUrlAsync("https://example.test/public/page");
        var matrix = CreateMatrix(stack);
        var recorder = new GovernanceMatrixRecorder(stack.Provider);
        var aggregates = new GeoAggregateRecorder(stack.Provider);

        var rows = await matrix.EvaluateAsync(stack.AuditId);
        await recorder.RecordAsync(stack.AuditId, rows);
        var written = await aggregates.RecordGovernanceCoverageAsync(stack.AuditId, rows);
        Assert.Equal(4, written);
        await aggregates.RecordGovernanceCoverageAsync(stack.AuditId, rows);

        await using var connection = await stack.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT metric_value FROM metric_summaries WHERE audit_id = @a AND metric_key = 'governance-coverage'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@a";
        parameter.Value = stack.AuditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<double>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetDouble(0));
        }

        Assert.Equal(4, values.Count);
        Assert.All(values, v => Assert.InRange(v, 0.0, 1.0));
        Assert.Contains(values, v => v > 0); // robots rules give explicit verdicts
    }

    // SQLite WAL/SHM sidecars are transient connection-pool artifacts, not
    // product data; the invariant concerns durable files only.
    private static string[] StableFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private static async Task<string> ReadRobotsStatesAsync(GeoRealStack stack)
    {
        await using var connection = await stack.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, robots_state FROM url_records ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var parts = new List<string>();
        while (await reader.ReadAsync())
        {
            parts.Add($"{reader.GetInt64(0)}:{reader.GetInt32(1)}");
        }

        return string.Join("|", parts);
    }

    private static async Task<long> CountArtifactsAsync(GeoRealStack stack)
    {
        await using var connection = await stack.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM content_artifacts";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
