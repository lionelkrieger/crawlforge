using System.Text;
using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// WP-09-05 duplicate-content site pass (FS-CRAWL-001 §7.4 near-duplicate
/// bodies, §7.5 shingle similarity; CAP-B-062/063). This is the G23 wiring
/// proof: the previously caller-less ShingleNearDuplicateDetector now produces
/// persisted-shape findings through the adapter, alongside exact-duplicate
/// SHA-256 grouping. Real parser, real detector, real scope resolver; stored
/// bodies only (offline re-audit semantics, §9.6).
/// </summary>
public class DuplicateContentAnalyzerTests
{
    private static readonly AngleSharpDomParser Parser = new();

    private sealed class Fixture
    {
        public InMemoryContentStore Store { get; } = new();
        public InMemoryArtifactRepository Artifacts { get; } = new();
        public InMemoryUrlRecordRepository Urls { get; } = new();

        public async Task AddPageAsync(long urlId, string html, long auditId = 1)
        {
            var url = $"https://example.com/page-{urlId}";
            Urls.Add(ContextFactory.Url(urlId, auditId) with
            {
                CanonicalUrl = url,
                Host = "example.com",
                Path = $"/page-{urlId}",
            });
            var artifact = await Store.PutAsync(auditId, urlId, ContentArtifactKind.Body,
                new MemoryStream(Encoding.UTF8.GetBytes(html)));
            Artifacts.Add(artifact);
        }

        public DuplicateContentAnalyzer CreateAnalyzer(
            DuplicateContentOptions? options = null,
            IContentScopeResolver? scopeResolver = null) =>
            new(
                new ShingleNearDuplicateDetector(Artifacts, Store, Parser, scopeResolver),
                Artifacts,
                Urls,
                Store,
                Parser,
                scopeResolver,
                options);
    }

    /// <summary>120-word body with one token swapped mid-text: shingle Jaccard
    /// ≈0.92 (≥0.9) but NOT an exact duplicate. Tokens are UNIQUE (i, not the
    /// fixture's i%17 generator): periodic text would collapse the shingle set
    /// to 17 distinct members and distort the similarity.</summary>
    private static string NearDuplicateBody(string seed, bool swap)
    {
        var words = Enumerable.Range(0, 120).Select(i => $"{seed}{i}").ToList();
        if (swap)
        {
            words[60] = "zzzunique";
        }

        return $"<p>{string.Join(" ", words)}</p>";
    }

    /// <summary>Exact duplicates (identical visible text, different titles and
    /// markup) group into ONE finding; the shingle pair is excluded.</summary>
    [Fact]
    public async Task ExactDuplicateBodies_GroupOnce_AndExcludeNearPair()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("Title A", body: FixtureSitePages.Para(60, "shared")));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("Title B", head: "<meta name=\"x\" content=\"y\">", body: FixtureSitePages.Para(60, "shared")));
        var analyzer = fixture.CreateAnalyzer();

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var exact = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateContent);
        Assert.Equal(FindingSeverity.Warning, exact.Severity);
        Assert.Equal(AuditCategory.Content, exact.Category);
        Assert.Null(exact.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(exact.EvidenceJson);
        Assert.Equal(2, evidence.RootElement.GetProperty("urlCount").GetInt32());
        Assert.False(evidence.RootElement.GetProperty("membersTruncated").GetBoolean());
        Assert.False(string.IsNullOrEmpty(evidence.RootElement.GetProperty("contentHash").GetString()));
        var members = evidence.RootElement.GetProperty("urls").EnumerateArray().ToArray();
        Assert.Equal(1, members[0].GetProperty("urlId").GetInt64());
        Assert.Equal("https://example.com/page-1", members[0].GetProperty("url").GetString());
        Assert.Equal(2, members[1].GetProperty("urlId").GetInt64());

        // Identical text is reported ONCE (as exact), never also as near.
        Assert.DoesNotContain(findings, f => f.RuleKey == ContentQualityRuleKeys.NearDuplicateContent);
    }

    /// <summary>G23 PROOF: near-duplicate bodies (similarity ≥ threshold, not
    /// exact) produce a pair finding through the shingle detector.</summary>
    [Fact]
    public async Task NearDuplicateBodies_EmitPairFinding_ThroughShingleDetector()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("Near A", body: NearDuplicateBody("alpha", swap: false)));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("Near B", body: NearDuplicateBody("alpha", swap: true)));
        var analyzer = fixture.CreateAnalyzer();

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var near = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.NearDuplicateContent);
        Assert.Equal(FindingSeverity.Warning, near.Severity);
        Assert.Null(near.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(near.EvidenceJson);
        Assert.Equal(1, evidence.RootElement.GetProperty("urlIdA").GetInt64());
        Assert.Equal(2, evidence.RootElement.GetProperty("urlIdB").GetInt64());
        Assert.Equal("https://example.com/page-1", evidence.RootElement.GetProperty("urlA").GetString());
        Assert.Equal("https://example.com/page-2", evidence.RootElement.GetProperty("urlB").GetString());
        Assert.True(evidence.RootElement.GetProperty("similarity").GetDouble() >= 0.9);
        Assert.Equal(0.9, evidence.RootElement.GetProperty("threshold").GetDouble());
        Assert.DoesNotContain(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateContent);
    }

    /// <summary>Distinct bodies produce no duplicate findings.</summary>
    [Fact]
    public async Task DistinctBodies_ProduceNoFindings()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("A", body: FixtureSitePages.Para(120, "alpha")));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("B", body: FixtureSitePages.Para(120, "beta")));
        var analyzer = fixture.CreateAnalyzer();

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Empty bodies are thin-content territory, not duplicates.</summary>
    [Fact]
    public async Task EmptyBodies_AreNotGrouped()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("Empty A"));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("Empty B"));
        var analyzer = fixture.CreateAnalyzer();

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>With a content scope, BOTH signals consume scoped text
    /// (FS-CRAWL-002 §4.3): shared boilerplate does not make pages duplicates,
    /// and identical main content does — even under different boilerplate.</summary>
    [Fact]
    public async Task ScopedText_DrivesBothSignals()
    {
        var fixture = new Fixture();
        // Same boilerplate, different main content → NOT duplicates under scope.
        await fixture.AddPageAsync(1, FixtureSitePages.Page("S1",
            body: $"<main><p>unique main one</p></main>{FixtureSitePages.Para(60, "boiler")}"));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("S2",
            body: $"<main><p>unique main two</p></main>{FixtureSitePages.Para(60, "boiler")}"));
        // Different boilerplate, identical main content → duplicates under scope.
        await fixture.AddPageAsync(3, FixtureSitePages.Page("S3",
            body: $"<main><p>same main text here</p></main>{FixtureSitePages.Para(60, "boilA")}"));
        await fixture.AddPageAsync(4, FixtureSitePages.Page("S4",
            body: $"<main><p>same main text here</p></main>{FixtureSitePages.Para(60, "boilB")}"));
        var resolver = new ContentScopeResolver();
        var analyzer = fixture.CreateAnalyzer(scopeResolver: resolver);
        var scope = new ContentScopeDefinition(["main"], []);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1, scope: scope);

        var exact = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateContent);
        using JsonDocument evidence = JsonDocument.Parse(exact.EvidenceJson);
        var ids = evidence.RootElement.GetProperty("urls").EnumerateArray()
            .Select(m => m.GetProperty("urlId").GetInt64())
            .ToArray();
        Assert.Equal([3L, 4L], ids);
    }

    /// <summary>Pair findings are bounded by MaxReportedPairs (scale guard).</summary>
    [Fact]
    public async Task MaxReportedPairs_BoundsNearFindings()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("A", body: NearDuplicateBody("alpha", swap: false)));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("B", body: NearDuplicateBody("alpha", swap: true)));
        await fixture.AddPageAsync(3, FixtureSitePages.Page("C", body: NearDuplicateBody("beta", swap: false)));
        await fixture.AddPageAsync(4, FixtureSitePages.Page("D", body: NearDuplicateBody("beta", swap: true)));
        var analyzer = fixture.CreateAnalyzer(new DuplicateContentOptions(MaxReportedPairs: 1));

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var near = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.NearDuplicateContent);
        using JsonDocument evidence = JsonDocument.Parse(near.EvidenceJson);
        Assert.Equal(1, evidence.RootElement.GetProperty("urlIdA").GetInt64());
        Assert.Equal(2, evidence.RootElement.GetProperty("urlIdB").GetInt64());
    }

    /// <summary>Group members are bounded by MaxGroupMembers; the full count
    /// and truncation flag ride evidence.</summary>
    [Fact]
    public async Task MaxGroupMembers_BoundsEvidence_KeepsFullCount()
    {
        var fixture = new Fixture();
        for (long id = 1; id <= 3; id++)
        {
            await fixture.AddPageAsync(id, FixtureSitePages.Page($"T{id}", body: FixtureSitePages.Para(60, "shared")));
        }

        var analyzer = fixture.CreateAnalyzer(new DuplicateContentOptions(MaxGroupMembers: 2));

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var exact = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateContent);
        using JsonDocument evidence = JsonDocument.Parse(exact.EvidenceJson);
        Assert.Equal(3, evidence.RootElement.GetProperty("urlCount").GetInt32());
        Assert.True(evidence.RootElement.GetProperty("membersTruncated").GetBoolean());
        Assert.Equal(2, evidence.RootElement.GetProperty("urls").GetArrayLength());
    }

    /// <summary>The pass is audit-scoped.</summary>
    [Fact]
    public async Task SitePass_IsAuditScoped()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("A", body: FixtureSitePages.Para(60, "shared")), auditId: 1);
        await fixture.AddPageAsync(2, FixtureSitePages.Page("B", body: FixtureSitePages.Para(60, "shared")), auditId: 2);
        var analyzer = fixture.CreateAnalyzer();

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Offline determinism (§9.6): byte-identical findings on re-run.</summary>
    [Fact]
    public async Task RepeatedRuns_AreDeterministic()
    {
        var fixture = new Fixture();
        await fixture.AddPageAsync(1, FixtureSitePages.Page("A", body: FixtureSitePages.Para(60, "shared")));
        await fixture.AddPageAsync(2, FixtureSitePages.Page("B", body: FixtureSitePages.Para(60, "shared")));
        await fixture.AddPageAsync(3, FixtureSitePages.Page("C", body: NearDuplicateBody("alpha", swap: false)));
        await fixture.AddPageAsync(4, FixtureSitePages.Page("D", body: NearDuplicateBody("alpha", swap: true)));
        var analyzer = fixture.CreateAnalyzer();

        var first = await analyzer.AnalyzeSiteAsync(auditId: 1);
        var second = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].RuleKey, second[i].RuleKey);
            Assert.Equal(first[i].EvidenceJson, second[i].EvidenceJson);
        }
    }
}
