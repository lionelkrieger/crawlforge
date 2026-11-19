using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Tests.ContentQuality;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// WP-09-05 URL-structure rules (FS-CRAWL-001 §7.4; CAP-B-064 parameters,
/// CAP-B-065 trailing slashes, CAP-B-066 duplicate URLs): per-URL session and
/// tracking parameter detection (including the fixture's ASP.NET cookieless
/// session trap, postback.aspx → /(S(abc123xyz))/pages/session.aspx) and the
/// cross-URL site pass over stored URL records.
/// </summary>
public class UrlStructureAnalyzerTests
{
    private static PageAuditContext Context(string url) =>
        IndexabilityContextFactory.Context(url: IndexabilityContextFactory.UrlRecord(canonicalUrl: url));

    private static UrlRecordDto Record(long id, string url, long auditId = 1) =>
        IndexabilityContextFactory.UrlRecord(canonicalUrl: url) with { Id = id, AuditId = auditId };

    // ---- Per-URL rules ------------------------------------------------------

    /// <summary>Session identifiers in the query emit a Warning with the
    /// parameter as evidence.</summary>
    [Theory]
    [InlineData("https://example.com/page?PHPSESSID=abc123")]
    [InlineData("https://example.com/page?jsessionid=ABC")]
    [InlineData("https://example.com/page?ASPSESSIONIDQWBRTQAS=abc")]
    [InlineData("https://example.com/page?sid=42")]
    public async Task SessionQueryParameter_EmitsWarning(string url)
    {
        var analyzer = new UrlStructureAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(url));

        var finding = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.SessionParameters);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Equal(IndexabilityContextFactory.UrlId, finding.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(url, evidence.RootElement.GetProperty("url").GetString());
        Assert.True(evidence.RootElement.GetProperty("parameters").GetArrayLength() >= 1);
    }

    /// <summary>The fixture's cookieless session path segment is detected.</summary>
    [Fact]
    public async Task CookielessSessionPathSegment_EmitsWarning()
    {
        var analyzer = new UrlStructureAnalyzer();
        const string url = "https://example.com/(S(abc123xyz))/pages/session.aspx";

        var findings = await analyzer.AnalyzeAsync(Context(url));

        var finding = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.SessionParameters);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        var segments = evidence.RootElement.GetProperty("pathSegments");
        Assert.Equal("(S(abc123xyz))", Assert.Single(segments.EnumerateArray()).GetString());
    }

    /// <summary>Marketing/tracking parameters emit a Notice listing each.</summary>
    [Fact]
    public async Task TrackingParameters_EmitNotice_WithAllParameters()
    {
        var analyzer = new UrlStructureAnalyzer();
        const string url = "https://example.com/page?utm_source=newsletter&utm_medium=email&gclid=xyz";

        var findings = await analyzer.AnalyzeAsync(Context(url));

        var finding = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.TrackingParameters);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        var names = evidence.RootElement.GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["utm_source", "utm_medium", "gclid"], names);
        Assert.DoesNotContain(findings, f => f.RuleKey == UrlStructureRuleKeys.SessionParameters);
    }

    /// <summary>Session and tracking parameters classify into distinct findings.</summary>
    [Fact]
    public async Task MixedParameters_ClassifyIntoDistinctFindings()
    {
        var analyzer = new UrlStructureAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context("https://example.com/page?sid=9&utm_campaign=spring&fbclid=abc"));

        Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.SessionParameters);
        var tracking = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.TrackingParameters);
        using JsonDocument evidence = JsonDocument.Parse(tracking.EvidenceJson);
        Assert.Equal(2, evidence.RootElement.GetProperty("parameters").GetArrayLength());
    }

    /// <summary>A clean URL emits nothing.</summary>
    [Fact]
    public async Task CleanUrl_EmitsNothing()
    {
        var analyzer = new UrlStructureAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context("https://example.com/products?id=7"));

        Assert.Empty(findings);
    }

    /// <summary>Local vocabulary extensions are honored (E2 configurability).</summary>
    [Fact]
    public async Task CustomVocabulary_ExtendsDetection()
    {
        var analyzer = new UrlStructureAnalyzer(options: new UrlStructureOptions(
            AdditionalSessionParameters: ["customsess"],
            AdditionalTrackingParameters: ["ref"]));

        var findings = await analyzer.AnalyzeAsync(Context("https://example.com/page?customsess=1&ref=home"));

        Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.SessionParameters);
        Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.TrackingParameters);
    }

    /// <summary>Non-http(s) or malformed URLs are skipped without throwing.</summary>
    [Fact]
    public async Task NonHttpUrl_IsSkipped()
    {
        var analyzer = new UrlStructureAnalyzer();
        // The record factory derives Host/Path from a valid URL; override only
        // the canonical URL to the malformed value under test.
        var url = IndexabilityContextFactory.UrlRecord() with { CanonicalUrl = "not-a-valid-url" };

        var findings = await analyzer.AnalyzeAsync(IndexabilityContextFactory.Context(url: url));

        Assert.Empty(findings);
    }

    // ---- Cross-URL site pass -------------------------------------------------

    /// <summary>The same path reachable with and without a trailing slash is
    /// one finding grouping BOTH variants.</summary>
    [Fact]
    public async Task TrailingSlashVariants_GroupIntoOneFinding()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/products"));
        urls.Add(Record(2, "https://example.com/products/"));
        urls.Add(Record(3, "https://example.com/other"));
        var analyzer = new UrlStructureAnalyzer(urls);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.TrailingSlashInconsistency);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("example.com", evidence.RootElement.GetProperty("host").GetString());
        Assert.Equal("/products", evidence.RootElement.GetProperty("path").GetString());
        Assert.Equal(2, evidence.RootElement.GetProperty("urlCount").GetInt32());
        var members = evidence.RootElement.GetProperty("urls").EnumerateArray().ToArray();
        Assert.Equal(2, members.Length);
        Assert.Contains(members, m => m.GetProperty("trailingSlash").GetBoolean());
        Assert.Contains(members, m => !m.GetProperty("trailingSlash").GetBoolean());

        // Slash variants are NOT duplicate URLs (normalization preserves slashes).
        Assert.DoesNotContain(findings, f => f.RuleKey == UrlStructureRuleKeys.DuplicateUrls);
    }

    /// <summary>The root path is exempt from trailing-slash consistency.</summary>
    [Fact]
    public async Task RootPath_IsExemptFromTrailingSlashRule()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/"));
        var analyzer = new UrlStructureAnalyzer(urls);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.DoesNotContain(findings, f => f.RuleKey == UrlStructureRuleKeys.TrailingSlashInconsistency);
    }

    /// <summary>URLs differing only in session parameters are duplicate URLs.</summary>
    [Fact]
    public async Task SessionParameterVariants_AreDuplicateUrls()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/page?sid=aaa"));
        urls.Add(Record(2, "https://example.com/page?sid=bbb"));
        var analyzer = new UrlStructureAnalyzer(urls);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.DuplicateUrls);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("https://example.com/page", evidence.RootElement.GetProperty("normalizedUrl").GetString());
        Assert.Equal(2, evidence.RootElement.GetProperty("urlCount").GetInt32());
        Assert.Equal(2, evidence.RootElement.GetProperty("urls").GetArrayLength());
    }

    /// <summary>Normalization strips tracking parameters and fragments,
    /// lowercases the host, and sorts the remaining query.</summary>
    [Fact]
    public async Task TrackingFragmentHostCaseAndOrderVariants_AreDuplicateUrls()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/page?b=2&a=1&utm_source=x#section"));
        urls.Add(Record(2, "https://EXAMPLE.com/page?a=1&b=2"));
        var analyzer = new UrlStructureAnalyzer(urls);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(findings, f => f.RuleKey == UrlStructureRuleKeys.DuplicateUrls);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("https://example.com/page?a=1&b=2", evidence.RootElement.GetProperty("normalizedUrl").GetString());
        Assert.Equal(2, evidence.RootElement.GetProperty("urlCount").GetInt32());
    }

    /// <summary>Genuinely different resources are not duplicates.</summary>
    [Fact]
    public async Task DifferentQueryValues_AreNotDuplicates()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/page?id=1"));
        urls.Add(Record(2, "https://example.com/page?id=2"));
        var analyzer = new UrlStructureAnalyzer(urls);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.DoesNotContain(findings, f => f.RuleKey == UrlStructureRuleKeys.DuplicateUrls);
    }

    /// <summary>The site pass is audit-scoped: other audits' records are ignored.</summary>
    [Fact]
    public async Task SitePass_IsAuditScoped()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/page?sid=a", auditId: 1));
        urls.Add(Record(2, "https://example.com/page?sid=b", auditId: 2));
        var analyzer = new UrlStructureAnalyzer(urls);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Without a URL repository the site pass fails loudly.</summary>
    [Fact]
    public async Task SitePass_WithoutRepository_Throws()
    {
        var analyzer = new UrlStructureAnalyzer();

        await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeSiteAsync(auditId: 1));
    }

    /// <summary>Offline determinism (§9.6): identical site-pass evidence on re-run.</summary>
    [Fact]
    public async Task SitePass_IsDeterministic()
    {
        var urls = new InMemoryUrlRecordRepository();
        urls.Add(Record(1, "https://example.com/products"));
        urls.Add(Record(2, "https://example.com/products/"));
        urls.Add(Record(3, "https://example.com/page?sid=a"));
        urls.Add(Record(4, "https://example.com/page?sid=b"));
        var analyzer = new UrlStructureAnalyzer(urls);

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
