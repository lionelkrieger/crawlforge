using System.Text.Json;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// i18n analyzer suite (FS-CRAWL-001 §7.13): hreflang set validation across
/// HTML and Link headers, ISO code validation, self-reference, duplicates,
/// conflicts, html lang, and offline site-wide reciprocity. The equivalency
/// rule keys (WP-03-02) must never be emitted here.
/// </summary>
public class HreflangAnalyzerTests
{
    private readonly HreflangAnalyzer _analyzer = new();

    private static string Page(string links, string lang = "en") =>
        $"""<html lang="{lang}"><head>{links}</head><body></body></html>""";

    private const string ValidLinks = """
        <link rel="alternate" hreflang="en" href="https://example.test/page" />
        <link rel="alternate" hreflang="de" href="https://example.test/de/page" />
        <link rel="alternate" hreflang="x-default" href="https://example.test/page" />
        """;

    [Fact]
    public async Task ValidSetWithSelfReference_Passes()
    {
        var context = StructuredDataTestContext.Create(Page(ValidLinks));

        var findings = await _analyzer.AnalyzeAsync(context);

        var pass = Assert.Single(findings);
        Assert.Equal(StructuredDataRuleKeys.HreflangValid, pass.RuleKey);
        Assert.Equal(FindingSeverity.Pass, pass.Severity);
        // D-021: hreflang findings belong to Internationalization (§7.4 row).
        Assert.Equal(AuditCategory.Internationalization, pass.Category);
        Assert.Equal(1, pass.UrlId);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("en-XX")]
    [InlineData("english")]
    [InlineData("e")]
    public async Task InvalidCode_ProducesInvalidCodeFinding(string code)
    {
        var links = $"""<link rel="alternate" hreflang="{code}" href="https://example.test/page" />""";
        var context = StructuredDataTestContext.Create(Page(links));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangInvalidCode);
    }

    [Theory]
    [InlineData("zh-Hant-TW")]
    [InlineData("pt-BR")]
    [InlineData("fr")]
    [InlineData("x-default")]
    public async Task ValidCodeShapes_AreAccepted(string code)
    {
        var links = $"""
            <link rel="alternate" hreflang="{code}" href="https://example.test/other" />
            <link rel="alternate" hreflang="en" href="https://example.test/page" />
            """;
        var context = StructuredDataTestContext.Create(Page(links));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangInvalidCode);
    }

    [Fact]
    public async Task MissingSelfReference_ProducesWarning()
    {
        var links = """<link rel="alternate" hreflang="de" href="https://example.test/de/page" />""";
        var context = StructuredDataTestContext.Create(Page(links));

        var findings = await _analyzer.AnalyzeAsync(context);

        var finding = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangMissingSelfReference);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task DuplicateAnnotation_ProducesWarning()
    {
        var links = """
            <link rel="alternate" hreflang="en" href="https://example.test/page" />
            <link rel="alternate" hreflang="en" href="https://example.test/page" />
            """;
        var context = StructuredDataTestContext.Create(Page(links));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangDuplicate);
    }

    [Fact]
    public async Task ConflictingTargets_ProduceError()
    {
        var links = """
            <link rel="alternate" hreflang="de" href="https://example.test/de/a" />
            <link rel="alternate" hreflang="de" href="https://example.test/de/b" />
            <link rel="alternate" hreflang="en" href="https://example.test/page" />
            """;
        var context = StructuredDataTestContext.Create(Page(links));

        var findings = await _analyzer.AnalyzeAsync(context);

        var conflict = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangConflict);
        Assert.Equal(FindingSeverity.Error, conflict.Severity);
        using var evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal(2, evidence.RootElement.GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public async Task LinkHeaderAnnotations_AreParsed()
    {
        var headers = new Dictionary<string, string>
        {
            ["Link"] = """<https://example.test/page>; rel="alternate"; hreflang="en", <https://example.test/de/page>; rel="alternate"; hreflang="de" """,
        };
        var context = StructuredDataTestContext.Create(Page(""), headers: headers);

        var findings = await _analyzer.AnalyzeAsync(context);

        var pass = Assert.Single(findings);
        Assert.Equal(StructuredDataRuleKeys.HreflangValid, pass.RuleKey);
    }

    [Fact]
    public async Task CrossSourceDiscrepancy_UsesOwnConflictKey_NotEquivalencyKey()
    {
        var links = """<link rel="alternate" hreflang="de" href="https://example.test/de/html" />""";
        var headers = new Dictionary<string, string>
        {
            ["Link"] = """<https://example.test/de/header>; rel="alternate"; hreflang="de", <https://example.test/page>; rel="alternate"; hreflang="en" """,
        };
        var context = StructuredDataTestContext.Create(Page(links), headers: headers);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangConflict);
        Assert.DoesNotContain(findings, f => f.RuleKey == "hreflang.header-html-conflict");
        Assert.DoesNotContain(findings, f => f.RuleKey == "canonical.header-html-conflict");
        Assert.DoesNotContain(findings, f => f.RuleKey == "indexability.header-html-conflict");
    }

    [Fact]
    public async Task RelativeSelfHref_ResolvesAgainstPageUrl()
    {
        var links = """
            <link rel="alternate" hreflang="en" href="/page" />
            <link rel="alternate" hreflang="de" href="/de/page" />
            """;
        var context = StructuredDataTestContext.Create(Page(links));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangMissingSelfReference);
    }

    [Fact]
    public async Task HtmlLangMissing_ProducesWarning()
    {
        var context = StructuredDataTestContext.Create("""<html><head></head><body></body></html>""");

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.HtmlLangMissing);
    }

    [Fact]
    public async Task HtmlLangInvalid_ProducesWarning()
    {
        var context = StructuredDataTestContext.Create(Page("", lang: "klingon"));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.HtmlLangInvalid);
    }

    [Fact]
    public async Task NoAnnotations_ProducesNoSetFindings()
    {
        var context = StructuredDataTestContext.Create(Page(""));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }

    [Fact]
    public void Reciprocity_MissingReturnLink_ProducesFinding()
    {
        var validator = new HreflangReciprocityValidator();
        var pages = new[]
        {
            new PageHreflangSet("https://example.test/page", new[]
            {
                new HreflangAnnotation("en", "https://example.test/page", "html"),
                new HreflangAnnotation("de", "https://example.test/de/page", "html"),
            }),
            new PageHreflangSet("https://example.test/de/page", new[]
            {
                new HreflangAnnotation("de", "https://example.test/de/page", "html"),
            }),
        };

        var findings = validator.Validate(pages, auditId: 7);

        var finding = Assert.Single(findings);
        Assert.Equal(StructuredDataRuleKeys.HreflangMissingReciprocity, finding.RuleKey);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Null(finding.UrlId);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("https://example.test/page", evidence.RootElement.GetProperty("pageUrl").GetString());
        Assert.Equal("https://example.test/de/page", evidence.RootElement.GetProperty("targetUrl").GetString());
    }

    [Fact]
    public void Reciprocity_CompleteSets_ProduceNoFindings()
    {
        var validator = new HreflangReciprocityValidator();
        var pages = new[]
        {
            new PageHreflangSet("https://example.test/page", new[]
            {
                new HreflangAnnotation("en", "https://example.test/page", "html"),
                new HreflangAnnotation("de", "https://example.test/de/page", "html"),
            }),
            new PageHreflangSet("https://example.test/de/page", new[]
            {
                new HreflangAnnotation("de", "https://example.test/de/page", "html"),
                new HreflangAnnotation("en", "https://example.test/page", "html"),
            }),
        };

        Assert.Empty(validator.Validate(pages, auditId: 7));
    }

    [Fact]
    public void Reciprocity_SkipsExternalTargetsAndXDefault()
    {
        var validator = new HreflangReciprocityValidator();
        var pages = new[]
        {
            new PageHreflangSet("https://example.test/page", new[]
            {
                new HreflangAnnotation("en", "https://example.test/page", "html"),
                new HreflangAnnotation("fr", "https://external.example/fr", "html"),
                new HreflangAnnotation("x-default", "https://example.test/selector", "html"),
            }),
        };

        Assert.Empty(validator.Validate(pages, auditId: 7));
    }

    [Fact]
    public async Task HeaderOnlySet_MissingSelfReference_IsDetected()
    {
        var headers = new Dictionary<string, string>
        {
            ["Link"] = """<https://example.test/de/page>; rel="alternate"; hreflang="de" """,
        };
        var context = StructuredDataTestContext.Create(Page(""), headers: headers);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangMissingSelfReference);
    }
}
