using System.Text.Json;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Hreflang reciprocity post-pass suite (WP-09-04, CAP-B-060, FS-CRAWL-001
/// §7.13): the REAL fixture hreflang-no-return pages flow through the per-URL
/// collection pass and the D14-style site pass over a migrated SQLite database
/// (production SchemaMigrator + SqlRepository). Asserts the exact finding rule
/// key, source-page URL attribution, and both-page evidence — including the
/// annotation-less target page that must still be reported.
/// </summary>
public class HreflangReciprocityRuleAnalyzerTests : IAsyncLifetime
{
    private StructuredDataRealStack _stack = null!;
    private HreflangReciprocityRuleAnalyzer _analyzer = null!;
    private SqlRepository<UrlRecordDto> _urls = null!;

    public async Task InitializeAsync()
    {
        _stack = await StructuredDataRealStack.CreateAsync();
        _urls = new SqlRepository<UrlRecordDto>(_stack.Provider);
        _analyzer = new HreflangReciprocityRuleAnalyzer(new HreflangReciprocityValidator(), _stack.Provider);
    }

    public async Task DisposeAsync() => await _stack.DisposeAsync();

    private async Task<long> InsertUrlAsync(
        string url,
        CrawlStatusClass statusClass = CrawlStatusClass.Success,
        string? contentType = "text/html")
    {
        var uri = new Uri(url);
        var record = await _urls.CreateAsync(new UrlRecordDto(
            0, _stack.AuditId, url, uri.Host, uri.AbsolutePath, 1,
            DiscoverySource.Link, statusClass, statusClass == CrawlStatusClass.Success ? 200 : 404, null,
            contentType, 0, null, RobotsState.Allowed, IndexabilityFlags.None, true));
        return record.Id;
    }

    private PageAuditContext Context(string html, string url, long urlId, IReadOnlyDictionary<string, string>? headers = null, string? renderedHtml = null) =>
        FixtureAuditContext.Create(html, url, headers: headers, renderedHtml: renderedHtml, urlId: urlId, auditId: _stack.AuditId);

    [Fact]
    public async Task FixtureHreflangNoReturn_MissingReturnTag_ReportedWithBothPageEvidence()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);

        // Per-URL passes collect; they emit nothing themselves.
        Assert.Empty(await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId)));
        Assert.Empty(await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, frId)));

        var result = await _analyzer.RunSitePassAsync(_stack.AuditId);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(StructuredDataRuleKeys.HreflangMissingReciprocity, finding.RuleKey);
        // D-021 phase-gate stand-in: hreflang-no-return is reported under the
        // Internationalization category (the UI-12 filter token).
        Assert.Equal(AuditCategory.Internationalization, finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(enId, finding.UrlId);
        Assert.Equal(1, result.MissingReturnCount);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(1, result.AnnotatedPageCount);

        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal(_stack.AuditId, root.GetProperty("auditId").GetInt64());
        Assert.Equal(FixtureSitePages.HreflangEnglishUrl, root.GetProperty("pageUrl").GetString());
        Assert.Equal(FixtureSitePages.HreflangFrenchUrl, root.GetProperty("targetUrl").GetString());
        Assert.Equal(enId, root.GetProperty("pageUrlId").GetInt64());
        Assert.Equal(frId, root.GetProperty("targetUrlId").GetInt64());
        Assert.Equal("fr", root.GetProperty("hreflang").GetString());
        // Both-page evidence: the French page declares NOTHING back.
        Assert.Empty(root.GetProperty("targetAnnotations").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task ReciprocalSets_ProduceNoFindings()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var frenchWithReturn = FixtureSitePages.Page(
            "Hreflang French",
            """<link rel="alternate" hreflang="fr" href="/pages/hreflang-fr.html"><link rel="alternate" hreflang="en" href="/pages/hreflang-en.html">""",
            "<h1>Français</h1>",
            lang: "fr");

        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));
        await _analyzer.AnalyzeAsync(Context(frenchWithReturn, FixtureSitePages.HreflangFrenchUrl, frId));

        var result = await _analyzer.RunSitePassAsync(_stack.AuditId);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnnotatedPageCount);
    }

    [Fact]
    public async Task TargetWithUnrelatedAnnotations_StillMissingReturn_EvidenceShowsTargetSet()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var frenchDeclaresDeOnly = FixtureSitePages.Page(
            "Hreflang French",
            """<link rel="alternate" hreflang="de" href="/pages/hreflang-de.html">""",
            "<h1>Français</h1>",
            lang: "fr");

        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));
        await _analyzer.AnalyzeAsync(Context(frenchDeclaresDeOnly, FixtureSitePages.HreflangFrenchUrl, frId));

        var finding = Assert.Single((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);

        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        var targetAnnotations = evidence.RootElement.GetProperty("targetAnnotations").EnumerateArray().ToList();
        var annotation = Assert.Single(targetAnnotations);
        Assert.Equal("de", annotation.GetProperty("hreflang").GetString());
        Assert.Equal("html", annotation.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ExternalTarget_IsSkippedWithoutAnyFetch()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var external = FixtureSitePages.Page(
            "External",
            """<link rel="alternate" hreflang="en" href="http://127.0.0.1:47311/pages/hreflang-en.html"><link rel="alternate" hreflang="fr" href="https://external.example/fr">""",
            "<p>external</p>");

        await _analyzer.AnalyzeAsync(Context(external, FixtureSitePages.HreflangEnglishUrl, enId));

        Assert.Empty((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
    }

    [Fact]
    public async Task XDefaultTarget_DoesNotRequireReciprocity()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var selectorId = await InsertUrlAsync(FixtureSitePages.BaseUrl + "/pages/selector.html");
        var withXDefault = FixtureSitePages.Page(
            "Selector source",
            """<link rel="alternate" hreflang="en" href="/pages/hreflang-en.html"><link rel="alternate" hreflang="x-default" href="/pages/selector.html">""",
            "<p>selector</p>");

        await _analyzer.AnalyzeAsync(Context(withXDefault, FixtureSitePages.HreflangEnglishUrl, enId));
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.Page("Selector", "", "<p>selector</p>"), FixtureSitePages.BaseUrl + "/pages/selector.html", selectorId));

        Assert.Empty((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
    }

    [Fact]
    public async Task HeaderLinkAnnotations_ParticipateInReciprocity()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var headers = new Dictionary<string, string>
        {
            ["Link"] = """<http://127.0.0.1:47311/pages/hreflang-en.html>; rel="alternate"; hreflang="en", <http://127.0.0.1:47311/pages/hreflang-fr.html>; rel="alternate"; hreflang="fr" """,
        };

        // English declares via HTTP Link headers only; French declares nothing.
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.Page("English", "", "<p>en</p>"), FixtureSitePages.HreflangEnglishUrl, enId, headers));
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, frId));

        var finding = Assert.Single((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
        Assert.Equal(enId, finding.UrlId);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("fr", evidence.RootElement.GetProperty("hreflang").GetString());
    }

    [Fact]
    public async Task HeaderReturnTag_SatisfiesReciprocity()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var frenchHeaders = new Dictionary<string, string>
        {
            ["Link"] = """<http://127.0.0.1:47311/pages/hreflang-en.html>; rel="alternate"; hreflang="en" """,
        };

        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, frId, frenchHeaders));

        Assert.Empty((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
    }

    [Fact]
    public async Task NonSuccessTargetRow_IsExcludedFromTheGraph()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl, CrawlStatusClass.ClientError);

        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));

        // The 404 French row is not a crawled page: its missing return is not judged.
        Assert.Empty((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
    }

    [Fact]
    public async Task NonHtmlTargetRow_IsExcludedFromTheGraph()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl, contentType: "application/pdf");

        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));

        Assert.Empty((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
    }

    [Fact]
    public async Task NonHtmlSourcePage_IsNotCollected()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var jsonContext = FixtureAuditContext.Create(
            FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl,
            urlId: enId, auditId: _stack.AuditId, contentType: "application/json");

        await _analyzer.AnalyzeAsync(jsonContext);

        var result = await _analyzer.RunSitePassAsync(_stack.AuditId);
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.AnnotatedPageCount);
    }

    [Fact]
    public async Task RenderedDocument_TakesPrecedenceForCollection()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);

        // Raw response has no annotations; the rendered DOM declares the set.
        await _analyzer.AnalyzeAsync(Context(
            FixtureSitePages.Page("English", "", "<p>en</p>"),
            FixtureSitePages.HreflangEnglishUrl, enId,
            renderedHtml: FixtureSitePages.HreflangEnglish));
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, frId));

        Assert.Single((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
    }

    [Fact]
    public async Task SinglePageAnalysis_AuditIdZero_IsNeverCollected()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var singlePage = FixtureAuditContext.Create(
            FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, urlId: enId, auditId: 0);

        Assert.Empty(await _analyzer.AnalyzeAsync(singlePage));

        var result = await _analyzer.RunSitePassAsync(_stack.AuditId);
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.AnnotatedPageCount);
    }

    [Fact]
    public async Task SitePass_DrainsTheCollectedSets()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, frId));

        Assert.Single((await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings);
        var second = await _analyzer.RunSitePassAsync(_stack.AuditId);

        Assert.Empty(second.Findings);
        Assert.Equal(0, second.AnnotatedPageCount);
    }

    [Fact]
    public async Task DiscardAudit_ClearsCollectionWithoutValidating()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId));

        _analyzer.DiscardAudit(_stack.AuditId);

        var result = await _analyzer.RunSitePassAsync(_stack.AuditId);
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.AnnotatedPageCount);
    }

    [Fact]
    public async Task MultipleSources_FindingsAreDeterministicInUrlIdOrder()
    {
        var sourceAId = await InsertUrlAsync(FixtureSitePages.BaseUrl + "/pages/a.html");
        var sourceBId = await InsertUrlAsync(FixtureSitePages.BaseUrl + "/pages/b.html");
        var targetId = await InsertUrlAsync(FixtureSitePages.BaseUrl + "/pages/c.html");
        var sourceA = FixtureSitePages.Page("A", """<link rel="alternate" hreflang="de" href="/pages/c.html">""", "<p>a</p>");
        var sourceB = FixtureSitePages.Page("B", """<link rel="alternate" hreflang="fr" href="/pages/c.html">""", "<p>b</p>");

        // Collect out of order; the site pass must still emit in url id order.
        await _analyzer.AnalyzeAsync(Context(sourceB, FixtureSitePages.BaseUrl + "/pages/b.html", sourceBId));
        await _analyzer.AnalyzeAsync(Context(sourceA, FixtureSitePages.BaseUrl + "/pages/a.html", sourceAId));
        await _analyzer.AnalyzeAsync(Context(FixtureSitePages.Page("C", "", "<p>c</p>"), FixtureSitePages.BaseUrl + "/pages/c.html", targetId));

        var findings = (await _analyzer.RunSitePassAsync(_stack.AuditId)).Findings;

        Assert.Equal(2, findings.Count);
        Assert.Equal(sourceAId, findings[0].UrlId);
        Assert.Equal(sourceBId, findings[1].UrlId);
    }

    [Fact]
    public async Task ConcurrentCollection_ProducesTheSameFindingsAsSequential()
    {
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var contexts = new[]
        {
            Context(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, enId),
            Context(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, frId),
        };

        var sequential = new HreflangReciprocityRuleAnalyzer(new HreflangReciprocityValidator(), _stack.Provider);
        foreach (var context in contexts)
        {
            await sequential.AnalyzeAsync(context);
        }

        await Parallel.ForEachAsync(
            contexts,
            new ParallelOptions { MaxDegreeOfParallelism = 2 },
            async (context, ct) => await _analyzer.AnalyzeAsync(context, ct));

        var expected = await sequential.RunSitePassAsync(_stack.AuditId);
        var actual = await _analyzer.RunSitePassAsync(_stack.AuditId);

        Assert.Equal(expected.Findings.Count, actual.Findings.Count);
        for (var i = 0; i < expected.Findings.Count; i++)
        {
            Assert.Equal(expected.Findings[i].RuleKey, actual.Findings[i].RuleKey);
            Assert.Equal(expected.Findings[i].UrlId, actual.Findings[i].UrlId);
            Assert.Equal(expected.Findings[i].EvidenceJson, actual.Findings[i].EvidenceJson);
        }
    }

    [Fact]
    public void SupportedRuleKeys_ExposeTheReciprocityKey()
    {
        Assert.Equal(new[] { StructuredDataRuleKeys.HreflangMissingReciprocity }, _analyzer.SupportedRuleKeys);
    }
}
