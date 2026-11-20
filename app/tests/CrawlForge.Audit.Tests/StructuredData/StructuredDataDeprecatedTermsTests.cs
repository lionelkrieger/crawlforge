using System.Text.Json;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// CR-030/D-021 (WP-10-05): deprecated terms and retired/restricted rich-result
/// types are flagged from the BUNDLED VERSIONED list (never fetched); HowTo is
/// one entry (retired), FAQPage is restricted. Microdata itemprop URL and
/// prefixed terms resolve like RDFa (local-name normalization against the
/// bundled vocabulary). The pipeline adapter emits the locked
/// <c>structured-data.deprecated-term</c> key. Runs over the production
/// AngleSharp parser.
/// </summary>
public class StructuredDataDeprecatedTermsTests
{
    private readonly StructuredDataValidator _validator = new(FixtureAuditContext.Parser);
    private readonly StructuredDataRuleAnalyzer _analyzer;

    public StructuredDataDeprecatedTermsTests()
    {
        _analyzer = new StructuredDataRuleAnalyzer(_validator);
    }

    private const string CompleteHowTo = """
        <html><head>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org", "@type": "HowTo", "name": "Change a tyre",
          "step": [ { "@type": "HowToStep", "text": "Loosen the nuts." } ]
        }
        </script>
        </head><body></body></html>
        """;

    private const string CompleteFaq = """
        <html><head>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org", "@type": "FAQPage",
          "mainEntity": [ { "@type": "Question", "name": "Q?", "acceptedAnswer": { "@type": "Answer", "text": "A." } } ]
        }
        </script>
        </head><body></body></html>
        """;

    // ---- The bundled versioned list ----------------------------------------

    /// <summary>The list is versioned, embedded and resolves by local name.</summary>
    [Fact]
    public void List_IsVersioned_AndResolvesQualifiedTypeNames()
    {
        Assert.False(string.IsNullOrWhiteSpace(DeprecatedRichResultList.Version));
        Assert.NotEmpty(DeprecatedRichResultList.Entries);

        foreach (var spelling in new[] { "HowTo", "https://schema.org/HowTo", "schema:HowTo" })
        {
            var entry = DeprecatedRichResultList.Find(spelling);
            Assert.NotNull(entry);
            Assert.Equal("HowTo", entry!.Type);
            Assert.Equal(DeprecatedRichResultEntry.StatusRetired, entry.Status);
            Assert.True(entry.IsRetired);
            Assert.True(DeprecatedRichResultList.IsRetired(spelling));
        }

        var faq = DeprecatedRichResultList.Find("FAQPage");
        Assert.NotNull(faq);
        Assert.Equal(DeprecatedRichResultEntry.StatusRestricted, faq!.Status);
        Assert.False(faq.IsRetired);
        Assert.False(DeprecatedRichResultList.IsRetired("FAQPage"));

        Assert.Null(DeprecatedRichResultList.Find("Article"));
        Assert.Null(DeprecatedRichResultList.Find(null));
        Assert.Null(DeprecatedRichResultList.Find(""));
    }

    // ---- Validator: retired/restricted eligibility --------------------------

    /// <summary>A complete HowTo stays syntactically valid but is NEVER
    /// rich-result eligible (retired feature), and carries the list entry.</summary>
    [Fact]
    public void CompleteHowTo_IsValid_ButRetiredAndIneligible()
    {
        var detail = Assert.Single(_validator.ValidateDetailed(CompleteHowTo, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.False(detail.Block.RichResultEligible);
        Assert.NotNull(detail.DeprecatedEntry);
        Assert.Equal(DeprecatedRichResultEntry.StatusRetired, detail.DeprecatedEntry!.Status);
    }

    /// <summary>A restricted FAQPage keeps its vocabulary-driven eligibility
    /// (site authority is not observable offline) and carries the entry.</summary>
    [Fact]
    public void CompleteFaqPage_RemainsEligible_WithRestrictedEntry()
    {
        var detail = Assert.Single(_validator.ValidateDetailed(CompleteFaq, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.True(detail.Block.RichResultEligible);
        Assert.NotNull(detail.DeprecatedEntry);
        Assert.Equal(DeprecatedRichResultEntry.StatusRestricted, detail.DeprecatedEntry!.Status);
    }

    /// <summary>Types outside the list carry no entry and keep eligibility.</summary>
    [Fact]
    public void Article_HasNoDeprecatedEntry()
    {
        var markup = """
            <html><head><script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Article", "headline": "x", "image": "y", "datePublished": "2026-01-01" }
            </script></head><body></body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.RichResultEligible);
        Assert.Null(detail.DeprecatedEntry);
    }

    /// <summary>An invalid HowTo (missing required) still carries the entry —
    /// the term is flagged regardless of validity.</summary>
    [Fact]
    public void InvalidHowTo_StillCarriesDeprecatedEntry()
    {
        var markup = """
            <html><head><script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "HowTo" }
            </script></head><body></body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.False(detail.Block.IsValid);
        Assert.False(detail.Block.RichResultEligible);
        Assert.NotNull(detail.DeprecatedEntry);
    }

    // ---- CR-030 Microdata itemprop resolution (like RDFa) -------------------

    /// <summary>Microdata itemprop URL and prefixed terms resolve to their
    /// local segment: a Product with qualified itemprops validates exactly
    /// like one with plain itemprops.</summary>
    [Fact]
    public void MicrodataQualifiedItemprops_ResolveLikeRdfa()
    {
        var markup = """
            <html><body>
            <div itemscope itemtype="https://schema.org/Product">
              <span itemprop="https://schema.org/name">Widget</span>
              <img itemprop="schema:image" src="https://example.test/w.jpg" />
              <div itemprop="offers" itemscope itemtype="https://schema.org/Offer">
                <span itemprop="https://schema.org/price" content="9.99">9.99</span>
                <span itemprop="schema:priceCurrency" content="EUR">EUR</span>
              </div>
            </div>
            </body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.Equal("microdata", detail.Block.Format);
        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.True(detail.Block.RichResultEligible);
        Assert.Empty(detail.MissingRequired);
    }

    /// <summary>A Microdata HowTo with URL itemtype and qualified itemprops is
    /// parsed, validated, and flagged retired — the full CR-030 path.</summary>
    [Fact]
    public void MicrodataHowTo_WithQualifiedTerms_IsFlaggedRetired()
    {
        var markup = """
            <html><body>
            <div itemscope itemtype="https://schema.org/HowTo">
              <span itemprop="https://schema.org/name">Change a tyre</span>
              <div itemprop="schema:step" itemscope itemtype="https://schema.org/HowToStep">
                <span itemprop="https://schema.org/text">Loosen the nuts.</span>
              </div>
            </div>
            </body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.False(detail.Block.RichResultEligible);
        Assert.NotNull(detail.DeprecatedEntry);
        Assert.Equal("HowTo", detail.DeprecatedEntry!.Type);
    }

    /// <summary>RDFa HowTo (typeof + property) is flagged retired too — the
    /// list applies across all three formats.</summary>
    [Fact]
    public void RdfaHowTo_IsFlaggedRetired()
    {
        var markup = """
            <html><body>
            <div vocab="https://schema.org/" typeof="HowTo">
              <span property="name">Change a tyre</span>
              <div property="step" typeof="HowToStep"><span property="text">Loosen the nuts.</span></div>
            </div>
            </body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.Equal("rdfa", detail.Block.Format);
        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.False(detail.Block.RichResultEligible);
        Assert.NotNull(detail.DeprecatedEntry);
    }

    // ---- Pipeline adapter: the locked deprecated-term finding ---------------

    /// <summary>The adapter emits the LOCKED key with case evidence: status,
    /// effective date, list version, type, format and ordinal.</summary>
    [Fact]
    public async Task HowToPage_EmitsLockedDeprecatedTermWarning()
    {
        var context = FixtureAuditContext.Create(CompleteHowTo, urlId: 7, auditId: 3);

        var findings = await _analyzer.AnalyzeAsync(context);

        var deprecated = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.StructuredDataDeprecatedTerm);
        Assert.Equal("structured-data.deprecated-term", deprecated.RuleKey);
        Assert.Equal(FindingSeverity.Warning, deprecated.Severity);
        Assert.Equal(AuditCategory.StructuredData, deprecated.Category);
        Assert.Equal(7, deprecated.UrlId);
        using var evidence = JsonDocument.Parse(deprecated.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal("HowTo", root.GetProperty("type").GetString());
        Assert.Equal("json-ld", root.GetProperty("format").GetString());
        Assert.Equal(0, root.GetProperty("ordinal").GetInt32());
        Assert.Equal("retired", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("since").GetString()));
        Assert.Equal(DeprecatedRichResultList.Version, root.GetProperty("listVersion").GetString());
        Assert.False(root.GetProperty("richResultEligible").GetBoolean());

        // The retired block is reported ineligible with the retired reason.
        var ineligible = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataRichResultIneligible);
        using var ineligibleEvidence = JsonDocument.Parse(ineligible.EvidenceJson);
        Assert.Contains("retired", ineligibleEvidence.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>A restricted FAQPage emits the deprecated-term Warning while
    /// its valid/eligible finding stays.</summary>
    [Fact]
    public async Task FaqPage_EmitsRestrictedDeprecatedTerm_AndKeepsValidFinding()
    {
        var context = FixtureAuditContext.Create(CompleteFaq, urlId: 7);

        var findings = await _analyzer.AnalyzeAsync(context);

        var deprecated = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.StructuredDataDeprecatedTerm);
        using var evidence = JsonDocument.Parse(deprecated.EvidenceJson);
        Assert.Equal("restricted", evidence.RootElement.GetProperty("status").GetString());
        Assert.Equal("FAQPage", evidence.RootElement.GetProperty("type").GetString());
        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataValid);
    }

    /// <summary>An invalid HowTo emits BOTH the invalid error and the
    /// deprecated-term warning (the term is the observation, not the error).</summary>
    [Fact]
    public async Task InvalidHowTo_EmitsInvalidAndDeprecatedTerm()
    {
        var markup = """
            <html><head><script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "HowTo" }
            </script></head><body></body></html>
            """;
        var context = FixtureAuditContext.Create(markup, urlId: 7);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataInvalid);
        Assert.Contains(findings, f => f.RuleKey == FindingRuleKeys.StructuredDataDeprecatedTerm);
    }

    /// <summary>Pages without listed types emit no deprecated-term finding.</summary>
    [Fact]
    public async Task ArticlePage_EmitsNoDeprecatedTerm()
    {
        var markup = """
            <html><head><script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Article", "headline": "x", "image": "y", "datePublished": "2026-01-01" }
            </script></head><body></body></html>
            """;
        var context = FixtureAuditContext.Create(markup, urlId: 7);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == FindingRuleKeys.StructuredDataDeprecatedTerm);
    }

    /// <summary>The adapter advertises the locked key in SupportedRuleKeys.</summary>
    [Fact]
    public void SupportedRuleKeys_IncludeTheLockedDeprecatedTermKey() =>
        Assert.Contains(FindingRuleKeys.StructuredDataDeprecatedTerm, _analyzer.SupportedRuleKeys);
}
