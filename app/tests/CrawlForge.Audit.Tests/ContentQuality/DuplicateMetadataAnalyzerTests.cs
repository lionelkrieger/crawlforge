using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// WP-09-05 duplicate-metadata site pass (FS-CRAWL-001 §7.4 content-quality
/// row; CAP-B-040 duplicate titles, CAP-B-042 duplicate descriptions; fixture
/// trap "duplicate-titles"). Contract-conformance style: the REAL element
/// inventory extractor produces the document_head payloads from the REAL
/// fixture markup, and the analyzer consumes exactly those persisted shapes.
/// </summary>
public class DuplicateMetadataAnalyzerTests
{
    private static readonly AngleSharpDomParser Parser = new();
    private static readonly ElementInventoryExtractor Extractor = new();

    private static UrlRecordDto Record(long id, string url, CrawlStatusClass status = CrawlStatusClass.Success, long auditId = 1) =>
        ContextFactory.Url(id, auditId) with
        {
            CanonicalUrl = url,
            Host = new Uri(url).Host,
            Path = new Uri(url).AbsolutePath,
            StatusClass = status,
        };

    /// <summary>Real producer → real consumer: document_head payload extracted
    /// from real HTML by the pipeline's extractor, stored as a UrlElementDto.</summary>
    private static async Task<UrlElementDto> HeadElementAsync(long elementId, long urlId, string html)
    {
        var document = Parser.Parse(html, $"https://example.com/page-{urlId}");
        var inventory = await Extractor.ExtractAsync(document);
        var head = inventory.Single(item => item.Group == "document_head");
        return new UrlElementDto(elementId, urlId, head.Group, head.PayloadJson, head.Ordinal);
    }

    private static async Task<(InMemoryUrlRecordRepository Urls, InMemoryUrlElementRepository Elements)> SeedAsync(
        params (long Id, string Url, string Html)[] pages)
    {
        var urls = new InMemoryUrlRecordRepository();
        var elements = new InMemoryUrlElementRepository();
        foreach (var (id, url, html) in pages)
        {
            urls.Add(Record(id, url));
            elements.Add(await HeadElementAsync(id, id, html));
        }

        return (urls, elements);
    }

    /// <summary>
    /// THE fixture regression: dup-title-a.html and dup-title-b.html share the
    /// title "Duplicate title" — ONE site-wide finding grouping ALL affected
    /// URLs (probe-2 only had the cannibalization cluster, no metadata group).
    /// </summary>
    [Fact]
    public async Task RealFixtureDuplicateTitles_GroupAllAffectedUrls()
    {
        var (urls, elements) = await SeedAsync(
            (10, FixtureSitePages.BaseUrl + "/pages/dup-title-a.html", FixtureSitePages.DupTitleAHtml),
            (11, FixtureSitePages.BaseUrl + "/pages/dup-title-b.html", FixtureSitePages.DupTitleBHtml),
            (12, FixtureSitePages.BaseUrl + "/pages/healthy.html", FixtureSitePages.RedirectTargetHtml));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateTitle);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(AuditCategory.Content, finding.Category);
        Assert.Null(finding.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("Duplicate title", evidence.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, evidence.RootElement.GetProperty("urlCount").GetInt32());
        var members = evidence.RootElement.GetProperty("urls").EnumerateArray().ToArray();
        Assert.Equal(2, members.Length);
        Assert.Equal(10, members[0].GetProperty("urlId").GetInt64());
        Assert.Equal(FixtureSitePages.BaseUrl + "/pages/dup-title-a.html", members[0].GetProperty("url").GetString());
        Assert.Equal(11, members[1].GetProperty("urlId").GetInt64());
        Assert.Equal(FixtureSitePages.BaseUrl + "/pages/dup-title-b.html", members[1].GetProperty("url").GetString());

        // Distinct bodies do not produce a duplicate-description finding here.
        Assert.DoesNotContain(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateDescription);
    }

    /// <summary>Duplicate meta descriptions group ALL affected URLs (3-way).</summary>
    [Fact]
    public async Task DuplicateDescriptions_GroupAllAffectedUrls()
    {
        const string description = "The same meta description on every page.";
        string PageWith(string title) => FixtureSitePages.Page(
            title,
            head: $"<meta name=\"description\" content=\"{description}\">",
            body: $"<p>{title} body.</p>");
        var (urls, elements) = await SeedAsync(
            (1, "https://example.com/a", PageWith("Page A")),
            (2, "https://example.com/b", PageWith("Page B")),
            (3, "https://example.com/c", PageWith("Page C")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateDescription);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(description, evidence.RootElement.GetProperty("description").GetString());
        Assert.Equal(3, evidence.RootElement.GetProperty("urlCount").GetInt32());
        Assert.Equal(3, evidence.RootElement.GetProperty("urls").GetArrayLength());
        Assert.DoesNotContain(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateTitle);
    }

    /// <summary>Whitespace-normalized equality groups spacing variants.</summary>
    [Fact]
    public async Task WhitespaceVariants_GroupTogether()
    {
        var (urls, elements) = await SeedAsync(
            (1, "https://example.com/a", FixtureSitePages.Page("Duplicate  title", body: "<p>A.</p>")),
            (2, "https://example.com/b", FixtureSitePages.Page(" Duplicate title ", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(findings, f => f.RuleKey == ContentQualityRuleKeys.DuplicateTitle);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("Duplicate title", evidence.RootElement.GetProperty("title").GetString());
    }

    /// <summary>Comparison is case-sensitive: case variants are different titles.</summary>
    [Fact]
    public async Task CaseVariants_DoNotGroup()
    {
        var (urls, elements) = await SeedAsync(
            (1, "https://example.com/a", FixtureSitePages.Page("Duplicate title", body: "<p>A.</p>")),
            (2, "https://example.com/b", FixtureSitePages.Page("duplicate title", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Empty/whitespace titles are missing-metadata territory, not duplicates.</summary>
    [Fact]
    public async Task EmptyTitles_AreSkipped()
    {
        var (urls, elements) = await SeedAsync(
            (1, "https://example.com/a", FixtureSitePages.Page("", body: "<p>A.</p>")),
            (2, "https://example.com/b", FixtureSitePages.Page("   ", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Only successfully fetched pages participate.</summary>
    [Fact]
    public async Task NonSuccessUrls_AreExcluded()
    {
        var urls = new InMemoryUrlRecordRepository();
        var elements = new InMemoryUrlElementRepository();
        urls.Add(Record(1, "https://example.com/a"));
        urls.Add(Record(2, "https://example.com/b", CrawlStatusClass.ClientError));
        elements.Add(await HeadElementAsync(1, 1, FixtureSitePages.Page("Same title", body: "<p>A.</p>")));
        elements.Add(await HeadElementAsync(2, 2, FixtureSitePages.Page("Same title", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Unique titles produce no findings.</summary>
    [Fact]
    public async Task UniqueTitles_ProduceNoFindings()
    {
        var (urls, elements) = await SeedAsync(
            (1, "https://example.com/a", FixtureSitePages.Page("Title A", body: "<p>A.</p>")),
            (2, "https://example.com/b", FixtureSitePages.Page("Title B", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Malformed inventory payloads are tolerated (no facts, no crash).</summary>
    [Fact]
    public async Task MalformedPayload_IsTolerated()
    {
        var urls = new InMemoryUrlRecordRepository();
        var elements = new InMemoryUrlElementRepository();
        urls.Add(Record(1, "https://example.com/a"));
        urls.Add(Record(2, "https://example.com/b"));
        elements.Add(new UrlElementDto(1, 1, "document_head", "not-json", 0));
        elements.Add(await HeadElementAsync(2, 2, FixtureSitePages.Page("Only title", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>The pass is audit-scoped.</summary>
    [Fact]
    public async Task SitePass_IsAuditScoped()
    {
        var urls = new InMemoryUrlRecordRepository();
        var elements = new InMemoryUrlElementRepository();
        urls.Add(Record(1, "https://example.com/a", auditId: 1));
        urls.Add(Record(2, "https://example.com/b", auditId: 2));
        elements.Add(await HeadElementAsync(1, 1, FixtureSitePages.Page("Same title", body: "<p>A.</p>")));
        elements.Add(await HeadElementAsync(2, 2, FixtureSitePages.Page("Same title", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var findings = await analyzer.AnalyzeSiteAsync(auditId: 1);

        Assert.Empty(findings);
    }

    /// <summary>Members are ordered by urlId regardless of insertion order,
    /// and repeated runs are byte-identical (§9.6 offline determinism).</summary>
    [Fact]
    public async Task MembersAreOrdered_AndRunsAreDeterministic()
    {
        var (urls, elements) = await SeedAsync(
            (30, "https://example.com/c", FixtureSitePages.Page("Shared", body: "<p>C.</p>")),
            (10, "https://example.com/a", FixtureSitePages.Page("Shared", body: "<p>A.</p>")),
            (20, "https://example.com/b", FixtureSitePages.Page("Shared", body: "<p>B.</p>")));
        var analyzer = new DuplicateMetadataAnalyzer(urls, elements);

        var first = await analyzer.AnalyzeSiteAsync(auditId: 1);
        var second = await analyzer.AnalyzeSiteAsync(auditId: 1);

        var finding = Assert.Single(first);
        using JsonDocument evidence = JsonDocument.Parse(finding.EvidenceJson);
        var ids = evidence.RootElement.GetProperty("urls").EnumerateArray()
            .Select(m => m.GetProperty("urlId").GetInt64())
            .ToArray();
        Assert.Equal([10L, 20L, 30L], ids);
        Assert.Equal(finding.EvidenceJson, Assert.Single(second).EvidenceJson);
    }
}
