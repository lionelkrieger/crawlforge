using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Indexability analyzer rules (FS-CRAWL-001 §7.6): status classification,
/// robots state, meta/header directives, canonical validation, soft-404
/// integration, and offline determinism.
/// </summary>
public sealed class IndexabilityAnalyzerTests
{
    [Fact]
    public async Task CleanSuccessPage_RecordsIndexablePass()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Healthy page", new[] { DomBuilder.Link("canonical", "/page") }, new[] { DomBuilder.Text("p", "Real content here.") }));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.Indexable && f.Severity == FindingSeverity.Pass);
        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalSelfReference && f.Severity == FindingSeverity.Pass);
        Assert.All(findings, f => Assert.Equal(AuditCategory.Indexability, f.Category));
        Assert.All(findings, f => Assert.Equal(IndexabilityContextFactory.UrlId, f.UrlId));
    }

    [Theory]
    [InlineData(CrawlStatusClass.ClientError, 404, FindingSeverity.Error)]
    [InlineData(CrawlStatusClass.ServerError, 500, FindingSeverity.Error)]
    [InlineData(CrawlStatusClass.FetchError, null, FindingSeverity.Error)]
    [InlineData(CrawlStatusClass.Redirect, 301, FindingSeverity.Warning)]
    [InlineData(CrawlStatusClass.NotCrawled, null, FindingSeverity.Notice)]
    public async Task NonSuccessStatus_ClassifiedPerTaxonomy(CrawlStatusClass statusClass, int? statusCode, FindingSeverity severity)
    {
        var analyzer = new IndexabilityAnalyzer();
        PageAuditContext context = IndexabilityContextFactory.Context(
            url: IndexabilityContextFactory.UrlRecord(statusClass, statusCode));

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft finding = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.NonIndexableStatus);
        Assert.Equal(severity, finding.Severity);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(statusClass.ToString(), evidence.RootElement.GetProperty("statusClass").GetString());
    }

    [Fact]
    public async Task BlockedPage_CarriesWafSubclassificationEvidence()
    {
        UrlRecordDto url = IndexabilityContextFactory.UrlRecord(CrawlStatusClass.Blocked, 200) with
        {
            BlockReason = "waf-challenge",
            WafProvider = WafProvider.Cloudflare,
            ChallengeClass = WafChallengeClass.Interstitial,
        };
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(url: url));

        FindingDraft finding = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.NonIndexableStatus);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("waf-challenge", evidence.RootElement.GetProperty("blockReason").GetString());
        Assert.Equal("Cloudflare", evidence.RootElement.GetProperty("wafProvider").GetString());
        Assert.Equal("Interstitial", evidence.RootElement.GetProperty("challengeClass").GetString());
    }

    [Fact]
    public async Task RobotsDisallowed_EmitsWarning()
    {
        var analyzer = new IndexabilityAnalyzer();
        PageAuditContext context = IndexabilityContextFactory.Context(
            url: IndexabilityContextFactory.UrlRecord(robotsState: RobotsState.Disallowed));

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.RobotsBlocked && f.Severity == FindingSeverity.Warning);
        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.Indexable);
    }

    [Fact]
    public async Task MetaRobotsNoindex_EmitsNotice_AndSuppressesIndexablePass()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Hidden", new[] { DomBuilder.Meta("robots", "noindex, nofollow") }, null));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        FindingDraft noindex = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.Noindex);
        Assert.Equal(FindingSeverity.Notice, noindex.Severity);
        using JsonDocument evidence = JsonDocument.Parse(noindex.EvidenceJson);
        Assert.Equal("meta-robots", evidence.RootElement.GetProperty("source").GetString());
        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.Nofollow);
        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.Indexable);
        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalMissing);
    }

    [Fact]
    public async Task MetaRobotsNone_ExpandsToNoindexAndNofollow()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Hidden", new[] { DomBuilder.Meta("robots", "none") }, null));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.Noindex);
        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.Nofollow);
    }

    [Fact]
    public async Task XRobotsTagNoindex_EmitsNoticeWithHeaderSource()
    {
        var analyzer = new IndexabilityAnalyzer();
        PageAuditContext context = IndexabilityContextFactory.Context(
            fetch: IndexabilityContextFactory.Fetch(headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" }));

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft noindex = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.Noindex);
        using JsonDocument evidence = JsonDocument.Parse(noindex.EvidenceJson);
        Assert.Equal("x-robots-tag", evidence.RootElement.GetProperty("source").GetString());
        Assert.Equal("*", evidence.RootElement.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task XRobotsTagAgentScoped_EmitsAgentEvidence()
    {
        var analyzer = new IndexabilityAnalyzer();
        PageAuditContext context = IndexabilityContextFactory.Context(
            fetch: IndexabilityContextFactory.Fetch(headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "googlebot: noindex" }));

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft noindex = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.Noindex);
        using JsonDocument evidence = JsonDocument.Parse(noindex.EvidenceJson);
        Assert.Equal("googlebot", evidence.RootElement.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task MissingCanonical_EmitsWarning()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("No canonical", null, new[] { DomBuilder.Text("p", "content") }));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalMissing && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public async Task CanonicalMismatch_EmitsWarningWithBothValues()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Mismatch", new[] { DomBuilder.Link("canonical", "https://example.com/other") }, null));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        FindingDraft mismatch = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalMismatch);
        using JsonDocument evidence = JsonDocument.Parse(mismatch.EvidenceJson);
        Assert.Equal("https://example.com/other", evidence.RootElement.GetProperty("canonicalTarget").GetString());
        Assert.Equal(IndexabilityContextFactory.PageUrl, evidence.RootElement.GetProperty("pageUrl").GetString());
    }

    [Fact]
    public async Task CanonicalCrossHost_EmitsWarning()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Cross host", new[] { DomBuilder.Link("canonical", "https://other.example.com/page") }, null));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalCrossHost && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public async Task MultipleCanonicals_EmitsError()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Two canonicals", new[] { DomBuilder.Link("canonical", "/a"), DomBuilder.Link("canonical", "/b") }, null));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalMultiple && f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public async Task InvalidCanonicalScheme_EmitsWarning()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Bad canonical", new[] { DomBuilder.Link("canonical", "javascript:void(0)") }, null));
        var analyzer = new IndexabilityAnalyzer();

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.Contains(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalInvalid && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public async Task HeaderCanonical_UsedWhenHtmlCanonicalAbsent()
    {
        var analyzer = new IndexabilityAnalyzer();
        PageAuditContext context = IndexabilityContextFactory.Context(
            fetch: IndexabilityContextFactory.Fetch(headers: new Dictionary<string, string>
            {
                ["Link"] = "<https://example.com/other>; rel=\"canonical\"",
            }));

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(context);

        FindingDraft mismatch = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.CanonicalMismatch);
        using JsonDocument evidence = JsonDocument.Parse(mismatch.EvidenceJson);
        Assert.Equal("header", evidence.RootElement.GetProperty("source").GetString());
    }

    /// <summary>
    /// WP-09-05 single-emission contract, DEFAULT mode: with the detector
    /// injected but no opt-in, the analyzer does NOT emit soft-404 — the audit
    /// pipeline's dedicated ISoft404Detector step is the sole emitter (probe-2
    /// measured two findings per URL when both paths emitted).
    /// </summary>
    [Fact]
    public async Task Soft404Page_DefaultComposition_DelegatesEmissionToPipelineStep()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, new[] { DomBuilder.Text("p", "Sorry, the page you requested could not be found.") }));
        var analyzer = new IndexabilityAnalyzer(new Soft404Detector());

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404);
        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.NonIndexableStatus);
    }

    /// <summary>
    /// WP-09-05 single-emission contract, OPT-IN mode: standalone compositions
    /// that run the analyzer WITHOUT the pipeline's dedicated detector step
    /// enable EmitSoft404Findings and get exactly one distinct soft-404
    /// Warning (criterion #28), separate from hard HTTP errors.
    /// </summary>
    [Fact]
    public async Task Soft404Page_OptInEmission_EmitsDistinctWarning()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, new[] { DomBuilder.Text("p", "Sorry, the page you requested could not be found.") }));
        var analyzer = new IndexabilityAnalyzer(
            new Soft404Detector(), new IndexabilityAnalyzerOptions(EmitSoft404Findings: true));

        IReadOnlyList<FindingDraft> findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(document: document));

        FindingDraft soft404 = Assert.Single(findings, f => f.RuleKey == IndexabilityRuleKeys.Soft404);
        Assert.Equal(FindingSeverity.Warning, soft404.Severity);
        Assert.DoesNotContain(findings, f => f.RuleKey == IndexabilityRuleKeys.NonIndexableStatus);
        using JsonDocument evidence = JsonDocument.Parse(soft404.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("confidence").GetDouble() >= 0.6);
    }

    [Fact]
    public async Task Rerun_FromSameStoredContext_IsDeterministic()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Determinism", new[] { DomBuilder.Meta("robots", "noindex"), DomBuilder.Link("canonical", "/elsewhere") }, null));
        PageAuditContext context = IndexabilityContextFactory.Context(document: document);
        var analyzer = new IndexabilityAnalyzer(new Soft404Detector());

        IReadOnlyList<FindingDraft> first = await analyzer.AnalyzeAsync(context);
        IReadOnlyList<FindingDraft> second = await analyzer.AnalyzeAsync(context);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].RuleKey, second[i].RuleKey);
            Assert.Equal(first[i].Severity, second[i].Severity);
            Assert.Equal(first[i].EvidenceJson, second[i].EvidenceJson);
        }
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        var analyzer = new IndexabilityAnalyzer();
        await Assert.ThrowsAsync<ArgumentNullException>(() => analyzer.AnalyzeAsync(null!));
    }
}