using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Tests.RenderedComparison.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.RenderedComparison;

/// <summary>
/// Contract test (WP-10-02 YAML): response/rendered diff findings with the
/// §7.18 (CR-009) severities and evidence. Title / canonical / meta-robots /
/// hreflang deltas are high severity (Error); a one-sided noindex is CRITICAL
/// (Error, the top of the locked vocabulary) with dual evidence in BOTH
/// directions; the service-worker registration is informational; text/link
/// deltas carry the word-count and rendered-only-link evidence the phase-gate
/// harness reads. Uses the REAL AngleSharp parser and the REAL comparer.
/// </summary>
public sealed class RawRenderedDiffFindingTests
{
    private readonly AngleSharpDomParser _parser = new();
    private readonly ResponseRenderedComparer _comparer = new();

    private DomDocument Parse(string html, string url = FixturePages.BaseUrl + "/page") => _parser.Parse(html, url);

    private static string Page(string title, string head, string body) =>
        $"<!doctype html><html><head><title>{title}</title>{head}</head><body>{body}</body></html>";

    [Fact]
    public async Task TitleDelta_IsHighSeverity_Error()
    {
        DomDocument raw = Parse(Page("Original title", "", "<p>content</p>"));
        DomDocument rendered = Parse(Page("Rewritten title", "", "<p>content</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderTitleDelta);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Contains(comparison.Differences, d => d.Field == "title" && d.ResponseValue == "Original title" && d.RenderedValue == "Rewritten title");
    }

    [Fact]
    public async Task CanonicalDelta_IsHighSeverity_Error()
    {
        DomDocument raw = Parse(Page("T", "<link rel=\"canonical\" href=\"/a\">", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "<link rel=\"canonical\" href=\"/b\">", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderCanonicalDelta);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Contains(comparison.Differences, d => d.Field == "canonical");
    }

    [Fact]
    public async Task MetaRobotsDelta_IsHighSeverity_Error()
    {
        DomDocument raw = Parse(Page("T", "<meta name=\"robots\" content=\"index, follow\">", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "<meta name=\"robots\" content=\"index, nofollow\">", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderMetaRobotsDelta);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Contains(comparison.Differences, d => d.Field == "meta-robots");
    }

    [Fact]
    public async Task HreflangDelta_IsHighSeverity_Error()
    {
        DomDocument raw = Parse(Page("T", "<link rel=\"alternate\" hreflang=\"en\" href=\"/en\">", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "<link rel=\"alternate\" hreflang=\"fr\" href=\"/fr\">", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderMetadataDelta && f.Severity == FindingSeverity.Error);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("hreflang", evidence.RootElement.GetProperty("field").GetString());
        Assert.Contains(comparison.Differences, d => d.Field == "hreflang");
    }

    [Fact]
    public async Task RenderedOnlyNoindex_IsCritical_WithDualEvidence()
    {
        DomDocument raw = Parse(Page("T", "", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "<meta name=\"robots\" content=\"noindex, nofollow\">", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderNoindexDelta);
        Assert.Equal(FindingSeverity.Error, finding.Severity); // critical = top of locked vocabulary
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("critical").GetBoolean());
        Assert.Equal("rendered-only", evidence.RootElement.GetProperty("direction").GetString());
        Assert.False(evidence.RootElement.GetProperty("rawNoindex").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("renderedNoindex").GetBoolean());
        // Dual evidence: both captures' directive state is present.
        Assert.True(evidence.RootElement.TryGetProperty("rawDirectives", out _));
        Assert.True(evidence.RootElement.TryGetProperty("renderedDirectives", out _));
        Assert.Contains("noindex", evidence.RootElement.GetProperty("renderedDirectives").GetString());
    }

    [Fact]
    public async Task RawOnlyNoindex_IsCritical_ReverseDirection()
    {
        DomDocument raw = Parse(Page("T", "<meta name=\"robots\" content=\"noindex\">", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderNoindexDelta);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("raw-only", evidence.RootElement.GetProperty("direction").GetString());
        Assert.True(evidence.RootElement.GetProperty("rawNoindex").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("renderedNoindex").GetBoolean());
    }

    [Fact]
    public async Task RawHeaderNoindex_RemovedInRender_IsCritical()
    {
        DomDocument raw = Parse(Page("T", "", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "", "<p>c</p>"));
        var headers = new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" };

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(
            ComparisonContext.Context(raw, rendered, headers: headers));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderNoindexDelta);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("raw-only", evidence.RootElement.GetProperty("direction").GetString());
        Assert.True(evidence.RootElement.GetProperty("rawHeaderNoindex").GetBoolean());
    }

    [Fact]
    public async Task NoindexOnBothSides_NoCriticalFinding()
    {
        DomDocument raw = Parse(Page("T", "<meta name=\"robots\" content=\"noindex\">", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "<meta name=\"robots\" content=\"noindex\">", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        Assert.DoesNotContain(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderNoindexDelta);
    }

    [Fact]
    public async Task ServiceWorkerRegistration_IsInformational_AndBypassed()
    {
        DomDocument raw = Parse(Page("T", "", "<p>c</p><script>navigator.serviceWorker.register('/sw.js');</script>"));
        DomDocument rendered = Parse(Page("T", "", "<p>c</p><script>navigator.serviceWorker.register('/sw.js');</script>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderServiceWorkerRegistered);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("registered").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("bypassed").GetBoolean());
        Assert.Equal("/sw.js", evidence.RootElement.GetProperty("scriptUrl").GetString());
        Assert.Equal("both", evidence.RootElement.GetProperty("observedIn").GetString());
    }

    [Fact]
    public async Task NoServiceWorker_NoFinding()
    {
        DomDocument raw = Parse(Page("T", "", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        Assert.DoesNotContain(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderServiceWorkerRegistered);
    }

    [Fact]
    public async Task TextDelta_EvidenceCarriesRenderedWordCount()
    {
        DomDocument raw = Parse(FixturePages.JsContentRaw, FixturePages.BaseUrl + "/pages/js-content.html");
        DomDocument rendered = Parse(FixturePages.JsContentRendered, FixturePages.BaseUrl + "/pages/js-content.html");

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderTextDelta);
        Assert.Equal(AuditCategory.Content, finding.Category);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        int words = evidence.RootElement.GetProperty("words").GetInt32();
        int totalWordCount = evidence.RootElement.GetProperty("totalWordCount").GetInt32();
        Assert.True(words >= 100, $"rendered words={words}");
        Assert.True(totalWordCount >= 100, $"totalWordCount={totalWordCount}");
        Assert.True(evidence.RootElement.GetProperty("rawWordCount").GetInt32() < 10);
        Assert.True(evidence.RootElement.GetProperty("renderedWordCount").GetInt32() >= 100);
    }

    [Fact]
    public async Task LinksDelta_EvidenceCarriesRenderedOnlyLinks()
    {
        DomDocument raw = Parse(FixturePages.JsLinkRaw, FixturePages.BaseUrl + "/pages/js-link.html");
        DomDocument rendered = Parse(FixturePages.JsLinkRendered, FixturePages.BaseUrl + "/pages/js-link.html");

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderLinksDelta);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        var renderedOnly = evidence.RootElement.GetProperty("renderedOnlyLinks").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("/pages/js-only-target.html", renderedOnly);
    }

    [Fact]
    public async Task GeneralMetadataDelta_StaysWarning()
    {
        DomDocument raw = Parse(Page("T", "<meta name=\"description\" content=\"Original description\">", "<p>c</p>"));
        DomDocument rendered = Parse(Page("T", "<meta name=\"description\" content=\"Rewritten description\">", "<p>c</p>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(raw, rendered));

        FindingDraft finding = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderMetadataDelta);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("metadata", evidence.RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public async Task IdenticalDocuments_NoDifferencesOrFindings()
    {
        DomDocument doc = Parse(Page("Same", "", "<p>same content</p><a href=\"/x\">x</a>"));

        ResponseRenderedComparison comparison = await _comparer.CompareAsync(ComparisonContext.Context(doc, doc));

        Assert.Empty(comparison.Differences);
        Assert.Empty(comparison.Findings);
    }

    [Fact]
    public async Task Rerun_IsDeterministic()
    {
        DomDocument raw = Parse(FixturePages.LateMetaRaw, FixturePages.BaseUrl + "/pages/late-meta.html");
        DomDocument rendered = Parse(FixturePages.LateMetaRendered, FixturePages.BaseUrl + "/pages/late-meta.html");
        PageAuditContext context = ComparisonContext.Context(raw, rendered);

        ResponseRenderedComparison first = await _comparer.CompareAsync(context);
        ResponseRenderedComparison second = await _comparer.CompareAsync(context);

        Assert.Equal(first.Differences, second.Differences);
        Assert.Equal(first.Findings.Select(f => f.RuleKey), second.Findings.Select(f => f.RuleKey));
        Assert.Equal(first.Findings.Select(f => f.EvidenceJson), second.Findings.Select(f => f.EvidenceJson));
    }
}
