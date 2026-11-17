using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Eight-step pipeline orchestration (FS-CRAWL-001 §7.2): stored artifacts
/// only (re-audit never re-fetches), scope resolution priority, scoped-text
/// propagation (§4.3/§4.5), per-rule isolation, metric aggregation, and the
/// baseline-preserving default (criterion #37).
/// </summary>
public class AuditPipelineTests : IClassFixture<AuditSqliteStackFixture>
{
    private readonly AuditSqliteStackFixture _stack;
    private readonly AngleSharpDomParser _parser = new();

    public AuditPipelineTests(AuditSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private static AuditPipeline CreatePipeline(
        IAuditRuleAnalyzer? host = null,
        IReadRepository<ProjectDto>? projects = null,
        ContentScopeProfileStore? profiles = null,
        IIndexabilityAnalyzer? indexability = null)
    {
        return new AuditPipeline(
            new AngleSharpDomParser(),
            new ElementInventoryExtractor(),
            new ContentScopeResolver(),
            host ?? new AuditRuleHost([]),
            projects,
            profiles,
            indexability);
    }

    private static HttpFetchResult StoredFetch(string html) => new(
        "https://example.test/page",
        200,
        new Dictionary<string, string>
        {
            ["content-type"] = "text/html",
            ["x-robots-tag"] = "noindex",
        },
        Encoding.UTF8.GetBytes(html),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(50));

    private static JsonElement PayloadOf(PageAuditResult result, string group) =>
        JsonDocument.Parse(result.Inventory.Single(i => i.Group == group).PayloadJson).RootElement;

    [Fact]
    public async Task PipelineConsumesStoredArtifactsAndNeverFetches()
    {
        // The fetcher produced the stored artifact once (during the crawl);
        // the pipeline itself has no fetch dependency and must not re-fetch.
        var fetcher = new FakeHttpFetcher(PipelineTestContext.SampleHtml);
        var stored = await fetcher.FetchAsync(new HttpFetchRequest("https://example.test/page"));
        Assert.Equal(1, fetcher.FetchCount);

        var pipeline = CreatePipeline();
        var context = PipelineTestContext.Create(fetch: stored);
        var first = await pipeline.RunAsync(context);
        var second = await pipeline.RunAsync(context); // re-audit

        Assert.Equal(1, fetcher.FetchCount);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.NotEmpty(first.Inventory);
    }

    [Fact]
    public async Task ParsesStoredBodyWhenNoDocumentIsProvided()
    {
        var pipeline = CreatePipeline();
        var result = await pipeline.RunAsync(PipelineTestContext.Create(fetch: StoredFetch(PipelineTestContext.SampleHtml)));

        var head = PayloadOf(result, "document_head");
        Assert.Equal("Sample Page", head.GetProperty("title").GetString());
    }

    [Fact]
    public async Task MissingDocumentYieldsDiagnosticNotFailure()
    {
        var pipeline = CreatePipeline();
        var result = await pipeline.RunAsync(PipelineTestContext.Create());

        var diagnostic = Assert.Single(result.Findings);
        Assert.Equal(PipelineDiagnostics.NoDocumentRuleKey, diagnostic.RuleKey);
        Assert.Empty(result.Inventory);
    }

    [Fact]
    public async Task UnconfiguredProjectReproducesBaselineExactly()
    {
        // Criterion #37: nothing configured anywhere → identical behavior to
        // the explicit baseline (null scope), byte-for-byte.
        var pipeline = CreatePipeline();
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");

        var unconfigured = await pipeline.RunAsync(PipelineTestContext.Create(document: document));
        var explicitBaseline = await pipeline.RunAsync(PipelineTestContext.Create(
            document: document,
            config: PipelineTestContext.NewConfig(scope: null)));

        Assert.Equal(JsonSerializer.Serialize(explicitBaseline), JsonSerializer.Serialize(unconfigured));

        var scopeItem = PayloadOf(unconfigured, ElementGroups.ContentScope);
        Assert.False(scopeItem.GetProperty("configured").GetBoolean());
        Assert.Equal(
            scopeItem.GetProperty("fullWordCount").GetInt32(),
            scopeItem.GetProperty("scopedWordCount").GetInt32());
        Assert.Equal(0, scopeItem.GetProperty("removedWordCount").GetInt32());
    }

    [Fact]
    public async Task ScopePropagatesToTextPassesButNotHeadOrLinks()
    {
        var contentFinding = new FindingDraft("content.test", AuditCategory.Content, FindingSeverity.Warning, 1, "{}", null);
        var host = new AuditRuleHost([new RecordingRuleAnalyzer(["content.test"], [contentFinding])]);
        var pipeline = CreatePipeline(host);
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var scope = new ContentScopeDefinition(["main"], ["nav"]);

        var scoped = await pipeline.RunAsync(PipelineTestContext.Create(document: document, config: PipelineTestContext.NewConfig(scope: scope)));
        var baseline = await pipeline.RunAsync(PipelineTestContext.Create(document: document));

        // Head facts and link discovery are scoping non-consumers (§4.3).
        Assert.Equal(
            JsonSerializer.Serialize(PayloadOf(baseline, "document_head")),
            JsonSerializer.Serialize(PayloadOf(scoped, "document_head")));
        Assert.Equal(
            baseline.Inventory.Where(i => i.Group == "anchors").Select(i => i.PayloadJson),
            scoped.Inventory.Where(i => i.Group == "anchors").Select(i => i.PayloadJson));

        // Text statistics stay the full visible count; scoped counts ride content_scope.
        Assert.Equal(
            PayloadOf(baseline, "text_statistics").GetProperty("visibleWordCount").GetInt32(),
            PayloadOf(scoped, "text_statistics").GetProperty("visibleWordCount").GetInt32());
        var scopeItem = PayloadOf(scoped, ElementGroups.ContentScope);
        Assert.True(scopeItem.GetProperty("configured").GetBoolean());
        Assert.True(scopeItem.GetProperty("scopedWordCount").GetInt32() < scopeItem.GetProperty("fullWordCount").GetInt32());
        Assert.True(scopeItem.GetProperty("removedWordCount").GetInt32() > 0);

        // Text-based findings note the scoped basis in evidence (§4.5).
        var finding = scoped.Findings.Single(f => f.RuleKey == "content.test");
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("scopedBasis").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("scopedWordCount").GetInt32() > 0);
    }

    [Fact]
    public async Task ProjectDefaultScopeIsInheritedWhenAuditHasNone()
    {
        var (_, projectId, _) = await _stack.CreateAuditAsync();
        var project = await _stack.Projects.GetByIdAsync(projectId);
        await _stack.Projects.UpdateAsync(project! with
        {
            ContentScope = new ContentScopeDefinition(["main"], []),
        });

        var pipeline = CreatePipeline(projects: _stack.Projects);
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var result = await pipeline.RunAsync(PipelineTestContext.Create(
            document: document,
            config: PipelineTestContext.NewConfig(projectId: projectId)));

        var scopeItem = PayloadOf(result, ElementGroups.ContentScope);
        Assert.True(scopeItem.GetProperty("configured").GetBoolean());
        Assert.True(scopeItem.GetProperty("removedWordCount").GetInt32() > 0);
    }

    [Fact]
    public async Task AuditLevelScopeOverridesProjectDefault()
    {
        var (_, projectId, _) = await _stack.CreateAuditAsync();
        var project = await _stack.Projects.GetByIdAsync(projectId);
        await _stack.Projects.UpdateAsync(project! with
        {
            ContentScope = new ContentScopeDefinition([], ["main"]), // project strips main
        });

        var pipeline = CreatePipeline(projects: _stack.Projects);
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var auditScope = new ContentScopeDefinition(["main"], []); // audit keeps only main
        var result = await pipeline.RunAsync(PipelineTestContext.Create(
            document: document,
            config: PipelineTestContext.NewConfig(projectId: projectId, scope: auditScope)));

        var scopeItem = PayloadOf(result, ElementGroups.ContentScope);
        Assert.Equal(1, scopeItem.GetProperty("includeMatchCount").GetInt32());
        Assert.Equal(0, scopeItem.GetProperty("excludeMatchCount").GetInt32());
    }

    [Fact]
    public async Task ProfileReferenceResolvesThroughTheStore()
    {
        var (_, projectId, _) = await _stack.CreateAuditAsync();
        var store = new ContentScopeProfileStore(_stack.Profiles, _stack.Profiles);
        var profile = await store.CreateAsync(projectId, "main-only", new ContentScopeDefinition(["main"], ["nav"]));

        var pipeline = CreatePipeline(profiles: store);
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var config = PipelineTestContext.NewConfig(projectId: projectId) with { ContentScopeProfileId = profile.Id };
        var result = await pipeline.RunAsync(PipelineTestContext.Create(document: document, config: config));

        var scopeItem = PayloadOf(result, ElementGroups.ContentScope);
        Assert.True(scopeItem.GetProperty("configured").GetBoolean());
        Assert.Equal(1, scopeItem.GetProperty("includeMatchCount").GetInt32());
        Assert.Equal(1, scopeItem.GetProperty("excludeMatchCount").GetInt32());
    }

    [Fact]
    public async Task ScopeAppliesToTheRenderedTreeWhenPresent()
    {
        var pipeline = CreatePipeline();
        var response = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var rendered = _parser.Parse(
            "<html><body><main>Rendered dynamic words appear here.</main><nav>Rendered nav</nav></body></html>",
            "https://example.test/page");
        var scope = new ContentScopeDefinition(["main"], []);

        var result = await pipeline.RunAsync(PipelineTestContext.Create(
            document: response,
            rendered: rendered,
            config: PipelineTestContext.NewConfig(scope: scope)));

        var metric = Assert.Single(result.Metrics);
        Assert.Equal(MetricKeys.ScopedWordCount, metric.MetricKey);
        Assert.Equal(5, metric.Value); // "Rendered dynamic words appear here."
    }

    [Fact]
    public async Task AnalyzerFailuresAreIsolatedAtPipelineLevel()
    {
        var host = new AuditRuleHost([new ThrowingRuleAnalyzer(), new RecordingRuleAnalyzer(["rule.ok"], [
            new FindingDraft("rule.ok", AuditCategory.Linking, FindingSeverity.Notice, 1, "{}", null)])]);
        var pipeline = CreatePipeline(host);
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");

        var result = await pipeline.RunAsync(PipelineTestContext.Create(document: document));

        Assert.Contains(result.Findings, f => f.RuleKey == "rule.ok");
        Assert.Contains(result.Findings, f => f.RuleKey == PipelineDiagnostics.AnalyzerFailureRuleKey);
        Assert.NotEmpty(result.Inventory);
    }

    [Fact]
    public async Task MetricAggregationCarriesScopedWordCountAndDimensions()
    {
        var pipeline = CreatePipeline();
        var document = _parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var result = await pipeline.RunAsync(PipelineTestContext.Create(document: document, urlId: 42));

        var metric = Assert.Single(result.Metrics);
        Assert.Equal(MetricKeys.ScopedWordCount, metric.MetricKey);
        Assert.True(metric.Value > 0);
        using var dimensions = JsonDocument.Parse(metric.DimensionsJson);
        Assert.Equal(42, dimensions.RootElement.GetProperty("urlId").GetInt64());
        Assert.Equal("example.test", dimensions.RootElement.GetProperty("host").GetString());
    }

    [Fact]
    public async Task HeaderDirectivesGroupComesFromStoredFetchHeaders()
    {
        var pipeline = CreatePipeline();
        var result = await pipeline.RunAsync(PipelineTestContext.Create(fetch: StoredFetch(PipelineTestContext.SampleHtml)));

        var headers = PayloadOf(result, ElementGroups.HeaderDirectives);
        Assert.Equal("noindex", headers.GetProperty("xRobotsTag").GetString());
        Assert.Equal("text/html", headers.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task ParseWarningsSurfaceAsDiagnostics()
    {
        var pipeline = CreatePipeline();
        var document = _parser.Parse("<html><body><p>Unclosed <b>bold</body>", "https://example.test/broken");
        var result = await pipeline.RunAsync(PipelineTestContext.Create(document: document));

        Assert.Contains(result.Findings, f => f.RuleKey == PipelineDiagnostics.ParseWarningsRuleKey);
    }
}
