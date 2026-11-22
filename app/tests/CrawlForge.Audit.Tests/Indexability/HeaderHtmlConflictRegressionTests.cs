using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Audit.Tests.ContentQuality;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// WP-09-05 header/HTML directive-conflict regressions (FS-CRAWL-002 §15.4,
/// criterion #48): conflicts are ERROR findings with DUAL evidence (both raw
/// source values + directive diff), never auto-resolved; agreement records a
/// Pass. The canonical-conflict case runs the REAL fixture markup
/// (pages.mjs lines 48–52) plus the REAL served Link header (server.mjs line
/// 62) through the REAL tolerant parser and analyzer.
/// </summary>
public class HeaderHtmlConflictRegressionTests
{
    private const string ConflictUrl = FixtureSitePages.BaseUrl + "/pages/canonical-conflict.html";

    private static readonly AngleSharpDomParser Parser = new();

    private static PageAuditContext Context(string html, IReadOnlyDictionary<string, string> headers, string url = ConflictUrl) =>
        IndexabilityContextFactory.Context(
            url: IndexabilityContextFactory.UrlRecord(canonicalUrl: url),
            document: Parser.Parse(html, url),
            fetch: IndexabilityContextFactory.Fetch(url: url, headers: headers, body: html));

    /// <summary>
    /// THE fixture regression: canonical-conflict.html declares
    /// /pages/canonical-target-html.html while the server sends
    /// Link: &lt;/pages/canonical-target-header.html&gt;; rel="canonical" —
    /// an Error with both sources as evidence.
    /// </summary>
    [Fact]
    public async Task RealFixtureCanonicalConflict_EmitsError_WithDualEvidence()
    {
        var headers = new Dictionary<string, string> { ["Link"] = FixtureSitePages.CanonicalConflictLinkHeader };
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(FixtureSitePages.CanonicalConflictHtml, headers));

        var conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        Assert.Equal(AuditCategory.Indexability, conflict.Category);
        Assert.Equal(IndexabilityContextFactory.UrlId, conflict.UrlId);

        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal(FixtureSitePages.CanonicalConflictLinkHeader, root.GetProperty("headerValue").GetString());
        Assert.Equal("/pages/canonical-target-html.html", root.GetProperty("htmlValue").GetString());
        Assert.Equal(FixtureSitePages.BaseUrl + "/pages/canonical-target-header.html", root.GetProperty("resolvedHeader").GetString());
        Assert.Equal(FixtureSitePages.BaseUrl + "/pages/canonical-target-html.html", root.GetProperty("resolvedHtml").GetString());
        Assert.Equal(ConflictUrl, root.GetProperty("pageUrl").GetString());

        // The diff carries BOTH sides; the finding never picks a winner.
        var diff = root.GetProperty("diff");
        Assert.Equal(root.GetProperty("resolvedHeader").GetString(), diff.GetProperty("header").GetString());
        Assert.Equal(root.GetProperty("resolvedHtml").GetString(), diff.GetProperty("html").GetString());
    }

    /// <summary>Robots conflict: X-Robots-Tag noindex vs meta "index, follow"
    /// is an Error with both raw values and the directive-level diff.</summary>
    [Fact]
    public async Task RobotsConflict_EmitsError_WithDualEvidenceAndDiff()
    {
        var html = FixtureSitePages.Page(
            "Robots conflict",
            head: "<meta name=\"robots\" content=\"index, follow\">",
            body: "<h1>Conflict</h1><p>Body content.</p>");
        var headers = new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" };
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html, headers));

        var conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal("noindex", root.GetProperty("directive").GetString());
        Assert.Equal("noindex", root.GetProperty("headerValue").GetString());
        Assert.Equal("index, follow", root.GetProperty("htmlValue").GetString());
        Assert.Equal("*", root.GetProperty("agent").GetString());
        Assert.Equal("noindex", root.GetProperty("diff").GetProperty("header").GetString());
        Assert.Equal("index", root.GetProperty("diff").GetProperty("html").GetString());
    }

    /// <summary>Agreement on both robots and canonical records Pass findings
    /// (same rule keys, agreement evidence) — no conflict noise.</summary>
    [Fact]
    public async Task Agreement_EmitsPassAnnotations()
    {
        var html = FixtureSitePages.Page(
            "Agreement",
            head: "<meta name=\"robots\" content=\"noindex, follow\"><link rel=\"canonical\" href=\"/target\">",
            body: "<p>Body content.</p>");
        var headers = new Dictionary<string, string>
        {
            ["X-Robots-Tag"] = "noindex, follow",
            ["Link"] = "</target>; rel=\"canonical\"",
        };
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html, headers));

        var robots = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Pass, robots.Severity);
        using (JsonDocument evidence = JsonDocument.Parse(robots.EvidenceJson))
        {
            Assert.True(evidence.RootElement.GetProperty("agreement").GetBoolean());
        }

        var canonical = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Pass, canonical.Severity);
    }

    /// <summary>"none" vs "all" expands to opposing directives: Error.</summary>
    [Fact]
    public async Task NoneVsAll_ExpandsToConflict()
    {
        var html = FixtureSitePages.Page(
            "None vs all",
            head: "<meta name=\"robots\" content=\"all\">",
            body: "<p>Body content.</p>");
        var headers = new Dictionary<string, string> { ["X-Robots-Tag"] = "none" };
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html, headers));

        Assert.Contains(findings, f =>
            f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && f.Severity == FindingSeverity.Error);
        Assert.DoesNotContain(findings, f =>
            f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && f.Severity == FindingSeverity.Pass);
    }

    /// <summary>hreflang conflicts are Errors too; the locked enum maps the
    /// spec's Internationalization facet to StructuredData (handoff note).</summary>
    [Fact]
    public async Task HreflangConflict_EmitsError_WithDualEvidence()
    {
        var html = FixtureSitePages.Page(
            "Hreflang conflict",
            head: "<link rel=\"alternate\" hreflang=\"de\" href=\"/de/page\">",
            body: "<p>Body content.</p>");
        var headers = new Dictionary<string, string>
        {
            ["Link"] = "</fr/page>; rel=\"alternate\"; hreflang=\"de\"",
        };
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html, headers));

        var conflict = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.HreflangHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        Assert.Equal(AuditCategory.Internationalization, conflict.Category); // D-021: hreflang -> Internationalization
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("de", evidence.RootElement.GetProperty("hreflang").GetString());
        Assert.Equal("/de/page", evidence.RootElement.GetProperty("htmlValue").GetString());
        Assert.NotNull(evidence.RootElement.GetProperty("diff").GetProperty("header").GetString());
    }

    /// <summary>Pipeline composition emits the canonical conflict exactly once
    /// (no duplicate emission path for §15.4 findings).</summary>
    [Fact]
    public async Task PipelineComposition_RealFixture_EmitsConflictOnce()
    {
        var pipeline = new AuditPipeline(
            new AngleSharpDomParser(),
            new ElementInventoryExtractor(),
            new ContentScopeResolver(),
            new AuditRuleHost([]),
            headerHtmlEquivalency: new HeaderHtmlEquivalencyAnalyzer());
        var headers = new Dictionary<string, string> { ["Link"] = FixtureSitePages.CanonicalConflictLinkHeader };
        var context = IndexabilityContextFactory.Context(
            url: IndexabilityContextFactory.UrlRecord(canonicalUrl: ConflictUrl),
            fetch: IndexabilityContextFactory.Fetch(url: ConflictUrl, headers: headers, body: FixtureSitePages.CanonicalConflictHtml));

        var result = await pipeline.RunAsync(context);

        var conflict = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
    }

    /// <summary>Offline determinism (§9.6): identical dual evidence on re-run.</summary>
    [Fact]
    public async Task RepeatedAnalysis_IsDeterministic()
    {
        var headers = new Dictionary<string, string> { ["Link"] = FixtureSitePages.CanonicalConflictLinkHeader };
        var context = Context(FixtureSitePages.CanonicalConflictHtml, headers);
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();

        var first = await analyzer.AnalyzeAsync(context);
        var second = await analyzer.AnalyzeAsync(context);

        Assert.Equal(
            first.Single(f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict).EvidenceJson,
            second.Single(f => f.RuleKey == FindingRuleKeys.CanonicalHeaderHtmlConflict).EvidenceJson);
    }
}
