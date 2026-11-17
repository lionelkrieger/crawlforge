using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Semantic linking pipeline end-to-end on the real stack (FS-CRAWL-002 §12):
/// stored-artifact inputs, content-hash reuse, band/threshold passes,
/// suggestions-only persistence, suppression-aware findings, metrics,
/// provider-switch re-embed, and graceful unavailability.
/// </summary>
public class SemanticPipelineTests : IClassFixture<IntelligenceSqliteStackFixture>
{
    private readonly IntelligenceSqliteStackFixture _stack;

    // Controlled 4-d vectors: cos(A,B)≈0.990 (cluster), cos(A,C)=0.80 and
    // cos(B,C)≈0.876 (opportunity band), cos(*,D)=0 (low relevance).
    private static readonly (string, float[])[] Rules =
    [
        ("aardvark", new float[] { 1, 0, 0, 0 }),
        ("buffalo", new float[] { 0.99f, 0.14f, 0, 0 }),
        ("cheetah", new float[] { 0.8f, 0.6f, 0, 0 }),
        ("dolphin", new float[] { 0, 0, 1, 0 }),
    ];

    public SemanticPipelineTests(IntelligenceSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private SemanticLinkingPipeline CreatePipeline(
        FakeEmbeddingProvider provider,
        SemanticPipelineOptions? options = null,
        IProgress<SemanticJobProgress>? progress = null) =>
        new(provider, new AngleSharpDomParser(), new ContentScopeResolver(),
            _stack.ContentStore, _stack.BuildPersistence(), options, progress);

    private static string Html(string marker, string? canonical = null) =>
        "<html><head><title>Page " + marker + "</title>" +
        (canonical is null ? "" : $"<link rel=\"canonical\" href=\"{canonical}\">") +
        "</head><body><h1>Heading " + marker + "</h1><p>" + marker + " content words here.</p></body></html>";

    private async Task<(long AuditId, long ProjectId, Dictionary<string, long> Urls)> SeedAsync(bool canonicalShared = false)
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        var urls = new Dictionary<string, long>();
        foreach (var marker in new[] { "aardvark", "buffalo", "cheetah", "dolphin" })
        {
            var urlId = await _stack.CreateUrlAsync(auditId, "/" + marker);
            await _stack.StoreBodyAsync(auditId, urlId,
                Html(marker, canonicalShared ? "https://example.test/shared" : null));
            urls[marker] = urlId;
        }

        return (auditId, projectId, urls);
    }

    private static SemanticAnalysisOptions Options(double threshold = 0.75, int topK = 10) =>
        new(threshold, topK);

    [Fact]
    public async Task OpportunitiesArePersistedAsSuggestionsOnly()
    {
        var (auditId, _, urls) = await SeedAsync();
        var provider = new FakeEmbeddingProvider(rules: Rules);
        var pipeline = CreatePipeline(provider);

        var result = await pipeline.RunAsync(auditId, Options());

        // Band pairs: A↔C (0.80) and B↔C (0.876); A–B is a cluster, not a band pair.
        Assert.Contains(result.Opportunities, o => o.SourceUrlId == urls["aardvark"] && o.TargetUrlId == urls["cheetah"]);
        Assert.Contains(result.Opportunities, o => o.SourceUrlId == urls["cheetah"] && o.TargetUrlId == urls["aardvark"]);
        Assert.Contains(result.Opportunities, o => o.SourceUrlId == urls["buffalo"] && o.TargetUrlId == urls["cheetah"]);
        Assert.DoesNotContain(result.Opportunities, o =>
            (o.SourceUrlId == urls["aardvark"] && o.TargetUrlId == urls["buffalo"]) ||
            (o.SourceUrlId == urls["buffalo"] && o.TargetUrlId == urls["aardvark"]));
        Assert.All(result.Opportunities, o => Assert.InRange(o.Similarity, 0.75, 0.88));

        // SUGGESTIONS ONLY: no link edges or URL records were created/modified.
        var edges = await _stack.LinkEdges.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = auditId.ToString() }));
        Assert.Equal(0, edges.TotalCount);
        var persisted = await _stack.Opportunities.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = auditId.ToString() }));
        Assert.Equal(result.Opportunities.Count, persisted.TotalCount);
    }

    [Fact]
    public async Task ExistingLinksAreExcludedInEitherDirection()
    {
        var (auditId, _, urls) = await SeedAsync();
        await _stack.CreateLinkAsync(auditId, urls["aardvark"], urls["cheetah"], "a to c");
        await _stack.CreateLinkAsync(auditId, urls["cheetah"], urls["buffalo"], "c to b");
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        var result = await pipeline.RunAsync(auditId, Options());

        Assert.DoesNotContain(result.Opportunities, o =>
            o.SourceUrlId == urls["aardvark"] && o.TargetUrlId == urls["cheetah"]);
        Assert.DoesNotContain(result.Opportunities, o =>
            o.SourceUrlId == urls["cheetah"] && o.TargetUrlId == urls["aardvark"]);
        Assert.DoesNotContain(result.Opportunities, o =>
            o.SourceUrlId == urls["buffalo"] && o.TargetUrlId == urls["cheetah"]);
        Assert.DoesNotContain(result.Opportunities, o =>
            o.SourceUrlId == urls["cheetah"] && o.TargetUrlId == urls["buffalo"]);
    }

    [Fact]
    public async Task TopKBoundsRecommendationsPerSource()
    {
        var (auditId, _, urls) = await SeedAsync();
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        var result = await pipeline.RunAsync(auditId, Options(topK: 1));

        var cheetahTargets = result.Opportunities.Where(o => o.SourceUrlId == urls["cheetah"]).ToArray();
        Assert.Single(cheetahTargets);
        // Highest similarity wins: buffalo (0.876) over aardvark (0.80).
        Assert.Equal(urls["buffalo"], cheetahTargets[0].TargetUrlId);
    }

    [Fact]
    public async Task CannibalizationClusterAndFindingArePersisted()
    {
        var (auditId, _, urls) = await SeedAsync();
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        var result = await pipeline.RunAsync(auditId, Options());

        var clusterRows = result.Clusters.Where(c => c.UrlId == urls["aardvark"] || c.UrlId == urls["buffalo"]).ToArray();
        Assert.Equal(2, clusterRows.Length);
        Assert.Equal(clusterRows[0].ClusterId, clusterRows[1].ClusterId);
        Assert.All(clusterRows, row => Assert.True(row.CentroidSimilarity > 0.9));

        var findings = await _stack.Findings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["RuleKey"] = FindingRuleKeys.ContentSemanticCannibalization,
            }));
        var finding = Assert.Single(findings.Items);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.False(finding.IsSuppressed);
        Assert.Contains("members", finding.EvidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedCanonicalTargetDowngradesClusterToNotice()
    {
        var (auditId, _, _) = await SeedAsync(canonicalShared: true);
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        await pipeline.RunAsync(auditId, Options());

        var findings = await _stack.Findings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["RuleKey"] = FindingRuleKeys.ContentSemanticCannibalization,
            }));
        Assert.Equal(FindingSeverity.Notice, Assert.Single(findings.Items).Severity);
    }

    [Fact]
    public async Task LowRelevanceExistingLinkEmitsNotice()
    {
        var (auditId, _, urls) = await SeedAsync();
        await _stack.CreateLinkAsync(auditId, urls["aardvark"], urls["dolphin"], "unrelated anchor");
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        await pipeline.RunAsync(auditId, Options());

        var findings = await _stack.Findings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["RuleKey"] = FindingRuleKeys.LinkingLowRelevanceLink,
            }));
        var finding = Assert.Single(findings.Items);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Equal(urls["aardvark"], finding.UrlId);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("similarity").GetDouble() < 0.35);
        Assert.Equal("unrelated anchor", evidence.RootElement.GetProperty("anchorText").GetString());
    }

    [Fact]
    public async Task MetricSummariesAreWritten()
    {
        var (auditId, _, _) = await SeedAsync();
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        var result = await pipeline.RunAsync(auditId, Options());

        var opportunities = await _stack.Metrics.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["MetricKey"] = MetricKeys.SemanticOpportunities,
            }));
        Assert.Equal(result.Opportunities.Count, Assert.Single(opportunities.Items).Value);

        var clusters = await _stack.Metrics.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["MetricKey"] = MetricKeys.SemanticClusters,
            }));
        Assert.Equal(1, Assert.Single(clusters.Items).Value);
    }

    [Fact]
    public async Task ReAuditReusesStoredVectorsViaContentHash()
    {
        var (auditId, _, _) = await SeedAsync();
        var provider = new FakeEmbeddingProvider(rules: Rules);
        var pipeline = CreatePipeline(provider);

        await pipeline.RunAsync(auditId, Options());
        var firstRunCalls = provider.EmbedCallCount;
        Assert.Equal(4, firstRunCalls);

        var second = await pipeline.RunAsync(auditId, Options());
        Assert.Equal(firstRunCalls, provider.EmbedCallCount); // nothing re-embedded
        Assert.NotEmpty(second.Opportunities);

        var rows = await _stack.Embeddings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = auditId.ToString() }));
        Assert.Equal(4, rows.TotalCount);
    }

    [Fact]
    public async Task ChangedContentReEmbedsOnlyTheChangedUrl()
    {
        var (auditId, _, urls) = await SeedAsync();
        var provider = new FakeEmbeddingProvider(rules: Rules);
        var pipeline = CreatePipeline(provider);

        await pipeline.RunAsync(auditId, Options());
        Assert.Equal(4, provider.EmbedCallCount);

        // New stored body for aardvark → new content hash → re-embed that URL only.
        await _stack.StoreBodyAsync(auditId, urls["aardvark"], Html("aardvark") + "<p>updated</p>");
        await pipeline.RunAsync(auditId, Options());

        Assert.Equal(5, provider.EmbedCallCount);
    }

    [Fact]
    public async Task ModelTagSwitchFullyReEmbedsWithoutMigration()
    {
        var (auditId, _, _) = await SeedAsync();
        var first = new FakeEmbeddingProvider("fake:model-a:4", Rules);
        await CreatePipeline(first).RunAsync(auditId, Options());

        var second = new FakeEmbeddingProvider("fake:model-b:4", Rules);
        await CreatePipeline(second).RunAsync(auditId, Options());

        Assert.Equal(4, second.EmbedCallCount); // full re-embed
        var rows = await _stack.Embeddings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = auditId.ToString() }));
        Assert.Equal(4, rows.TotalCount);
        Assert.All(rows.Items, row => Assert.Equal("fake:model-b:4", row.ModelTag));
    }

    [Fact]
    public async Task UnavailableProviderDegradesToDiagnosticNeverFailure()
    {
        var (auditId, _, _) = await SeedAsync();
        var provider = new FakeEmbeddingProvider(rules: Rules)
        {
            TestResult = new EmbeddingProviderTestResult(false, false, "daemon not running"),
        };
        var pipeline = CreatePipeline(provider);

        var result = await pipeline.RunAsync(auditId, Options());

        Assert.Empty(result.Opportunities);
        Assert.Empty(result.Clusters);
        Assert.Equal(0, provider.EmbedCallCount);
        var findings = await _stack.Findings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["RuleKey"] = SemanticLinkingPipeline.EmbeddingsUnavailableRuleKey,
            }));
        Assert.Equal(FindingSeverity.Notice, Assert.Single(findings.Items).Severity);
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        var (auditId, _, _) = await SeedAsync();
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.RunAsync(auditId, Options(), cts.Token));
    }

    [Fact]
    public async Task AnchorCandidatesComeFromTargetNgrams()
    {
        var (auditId, _, urls) = await SeedAsync();
        await _stack.Ngrams.CreateAsync(new NgramFrequencyDto(
            0, auditId, urls["cheetah"], TextZone.Body, 2, "semantic linking", 5, 0.1));
        await _stack.Ngrams.CreateAsync(new NgramFrequencyDto(
            0, auditId, urls["cheetah"], TextZone.Headings, 3, "internal link candidates", 3, 0.05));
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        var result = await pipeline.RunAsync(auditId, Options());

        var toCheetah = result.Opportunities.First(o => o.TargetUrlId == urls["cheetah"]);
        Assert.NotNull(toCheetah.AnchorCandidatesJson);
        Assert.Contains("semantic linking", toCheetah.AnchorCandidatesJson, StringComparison.Ordinal);
        Assert.Contains("internal link candidates", toCheetah.AnchorCandidatesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppressionsFlagSemanticFindings()
    {
        var (auditId, projectId, _) = await SeedAsync();
        await _stack.Suppressions.CreateAsync(new FindingSuppressionDto(
            0, projectId, "suppress-semantic", FindingRuleKeys.ContentSemanticCannibalization,
            null, null, true, DateTimeOffset.UtcNow));
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));

        await pipeline.RunAsync(auditId, Options());

        var findings = await _stack.Findings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(),
                ["RuleKey"] = FindingRuleKeys.ContentSemanticCannibalization,
            }));
        Assert.True(Assert.Single(findings.Items).IsSuppressed);
    }

    [Fact]
    public async Task NonIndexableUrlsAreExcluded()
    {
        var (auditId, _, _) = await SeedAsync();
        var excluded = await _stack.CreateUrlAsync(auditId, "/excluded", indexable: false);
        await _stack.StoreBodyAsync(auditId, excluded, Html("aardvark"));
        var provider = new FakeEmbeddingProvider(rules: Rules);
        var pipeline = CreatePipeline(provider);

        await pipeline.RunAsync(auditId, Options());

        Assert.Equal(4, provider.EmbedCallCount);
        var rows = await _stack.Embeddings.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = auditId.ToString() }));
        Assert.DoesNotContain(rows.Items, row => row.UrlId == excluded);
    }

    [Fact]
    public async Task MissingAuditReturnsEmptyResult()
    {
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules));
        var result = await pipeline.RunAsync(999999, Options());
        Assert.Empty(result.Opportunities);
        Assert.Empty(result.Clusters);
    }

    [Fact]
    public async Task ProgressReportsFinalCompletion()
    {
        var (auditId, _, _) = await SeedAsync();
        var reports = new List<SemanticJobProgress>();
        var progress = new Progress<SemanticJobProgress>(reports.Add);
        var pipeline = CreatePipeline(new FakeEmbeddingProvider(rules: Rules), progress: progress);

        await pipeline.RunAsync(auditId, Options());
        await Task.Delay(50); // Progress<T> posts asynchronously

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.IsFinal && r.Phase == "completed");
    }

    [Fact]
    public async Task AuditScopeSnapshotAppliesToEmbeddingInput()
    {
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/scoped");
        await _stack.StoreBodyAsync(auditId, urlId,
            "<html><head><title>Scoped</title></head><body><main>aardvark main text</main><nav>dolphin navigation</nav></body></html>");
        var audit = await _stack.Audits.GetByIdAsync(auditId);
        await _stack.Audits.UpdateAsync(audit! with
        {
            ResolvedContentScopeJson = "{\"includeSelectors\":[\"main\"],\"excludeSelectors\":[]}",
        });

        var provider = new FakeEmbeddingProvider(rules: Rules);
        await CreatePipeline(provider).RunAsync(auditId, Options());

        var input = Assert.Single(provider.Texts);
        Assert.Contains("aardvark", input, StringComparison.Ordinal);
        Assert.DoesNotContain("dolphin", input, StringComparison.Ordinal);
    }
}
