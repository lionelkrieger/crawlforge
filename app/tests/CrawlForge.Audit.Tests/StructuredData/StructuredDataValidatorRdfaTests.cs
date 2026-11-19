using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// RDFa extraction suite (CAP-B2-071, FS-CRAWL-001 §7.13 as amended by
/// CR-017/D-017): typeof/property/vocab/prefix handling, RDFa value precedence
/// (content/resource/href/src/datetime/text), nested entity relationships,
/// untyped vocabulary-scoped property sets, and the Open Graph non-RDFa guard.
/// Runs over both the test parser double and the production AngleSharp parser.
/// </summary>
public class StructuredDataValidatorRdfaTests
{
    private readonly StructuredDataValidator _validator = new(new TestDomParser());
    private readonly StructuredDataValidator _realParserValidator = new(FixtureAuditContext.Parser);

    [Fact]
    public void TypedRdfaEntity_ValidatesAgainstTheBundledVocabulary()
    {
        var markup = """
            <html><body>
            <div vocab="https://schema.org/" typeof="Article">
              <span property="headline">RDFa crawling</span>
              <img property="image" src="https://example.test/cover.jpg" />
              <time property="datePublished" datetime="2026-01-15">15 January</time>
            </div>
            </body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.Equal("rdfa", detail.Block.Format);
        Assert.Equal("Article", detail.Block.Type);
        Assert.True(detail.Block.IsValid);
        Assert.True(detail.Block.RichResultEligible);
        Assert.Empty(detail.Block.Errors);
        Assert.Equal(0, detail.Ordinal);
    }

    [Fact]
    public void ContentAttribute_OverridesVisibleText()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Product">
              <span property="name" content="Canonical Widget">Visible Widget</span>
              <span property="image">https://example.test/w.jpg</span>
              <div property="offers" typeof="Offer">
                <span property="price" content="9.99">nine ninety-nine</span>
                <span property="priceCurrency" content="EUR">euros</span>
              </div>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.True(detail.Block.RichResultEligible);
    }

    [Fact]
    public void UrlValuedProperties_ResolveFromResourceHrefAndSrc()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Article">
              <span property="headline">Title</span>
              <a property="image" href="https://example.test/i.jpg">cover</a>
              <time property="datePublished" datetime="2026-01-01">Jan</time>
              <link property="url" resource="https://example.test/article" />
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
    }

    [Fact]
    public void PrefixedTypeOf_ResolvesThroughTheVocabulary()
    {
        var markup = """
            <div prefix="schema: https://schema.org/" typeof="schema:Product">
              <span property="schema:name">Widget</span>
              <span property="schema:image">https://example.test/w.jpg</span>
              <div property="schema:offers" typeof="schema:Offer">
                <span property="schema:price" content="1.00">1</span>
                <span property="schema:priceCurrency" content="USD">USD</span>
              </div>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.Equal("schema:Product", detail.Block.Type);
        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.True(detail.Block.RichResultEligible);
        Assert.Equal(new[] { "schema:Offer" }, detail.NestedEntityTypes);
    }

    [Fact]
    public void AbsoluteTypeOfUrl_ResolvesThroughTheVocabulary()
    {
        var markup = """
            <div typeof="https://schema.org/Event">
              <span property="name">Launch</span>
              <time property="startDate" datetime="2026-02-01T10:00:00+00:00">Feb 1</time>
              <span property="location">Berlin</span>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.True(detail.Block.RichResultEligible);
    }

    [Fact]
    public void NestedTypeOfWithProperty_IsARelationshipNotASeparateBlock()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Product">
              <span property="name">Widget</span>
              <span property="image">https://example.test/w.jpg</span>
              <div property="offers" typeof="Offer">
                <span property="price" content="5">5</span>
                <span property="priceCurrency" content="EUR">EUR</span>
              </div>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
        Assert.Equal(new[] { "Offer" }, detail.NestedEntityTypes);
    }

    [Fact]
    public void NestedTypeOfWithoutProperty_IsItsOwnTopLevelBlock()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Product">
              <span property="name">Widget</span>
              <span property="image">https://example.test/w.jpg</span>
              <div property="offers" typeof="Offer">
                <span property="price" content="5">5</span>
                <span property="priceCurrency" content="EUR">EUR</span>
              </div>
              <div typeof="Person"><span property="name">Ada</span></div>
            </div>
            """;

        var result = _validator.ValidateDetailed(markup, null);

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("Product", result.Blocks[0].Block.Type);
        Assert.Equal("Person", result.Blocks[1].Block.Type);
        Assert.Equal(new[] { "Offer" }, result.Blocks[0].NestedEntityTypes);
        Assert.Empty(result.Blocks[1].NestedEntityTypes);
    }

    [Fact]
    public void MissingRequiredProperties_AreNamedForRdfa()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Recipe"><span property="name">Soup</span></div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.False(detail.Block.IsValid);
        Assert.Equal(new[] { "image", "recipeIngredient", "recipeInstructions" }, detail.MissingRequired);
        Assert.Contains(detail.Block.Errors, e => e == "missing-required-field: Recipe requires 'image'");
    }

    [Fact]
    public void MissingRecommendedProperties_AreAdvisoryOnly()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Article">
              <span property="headline">Title</span>
              <span property="image">https://example.test/i.jpg</span>
              <time property="datePublished" datetime="2026-01-01">Jan</time>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.True(detail.Block.IsValid);
        Assert.True(detail.Block.RichResultEligible);
        Assert.Equal(new[] { "author", "dateModified", "publisher" }, detail.MissingRecommended);
    }

    [Fact]
    public void InvalidDateLiteral_ProducesInvalidValueError()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Article">
              <span property="headline">Title</span>
              <span property="image">https://example.test/i.jpg</span>
              <span property="datePublished">not-a-date</span>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.False(detail.Block.IsValid);
        Assert.Contains(detail.Block.Errors, e => e.StartsWith("invalid-value: Article.datePublished", StringComparison.Ordinal));
    }

    [Fact]
    public void UntypedVocabScopedProperties_ReportMissingType()
    {
        var markup = """
            <html><body vocab="https://schema.org/">
            <span property="name">Subject without a type</span>
            </body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.Equal("rdfa", detail.Block.Format);
        Assert.Null(detail.Block.Type);
        Assert.False(detail.Block.IsValid);
        Assert.Contains(detail.Block.Errors, e => e == "missing-type: RDFa block has no typeof");
    }

    [Fact]
    public void OpenGraphMetaWithoutVocabContext_IsNotRdfa()
    {
        var markup = """
            <html><head>
            <meta property="og:title" content="Social card" />
            <meta property="og:image" content="https://example.test/card.jpg" />
            </head><body></body></html>
            """;

        Assert.Empty(_validator.ValidateDetailed(markup, null).Blocks);
        Assert.Empty(_realParserValidator.ValidateDetailed(markup, null).Blocks);
    }

    [Fact]
    public void OpenGraphMetaWithDeclaredPrefix_IsAnUntypedRdfaSet()
    {
        var markup = """
            <html prefix="og: http://ogp.me/ns#"><head>
            <meta property="og:title" content="Social card" />
            </head><body></body></html>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.Equal("rdfa", detail.Block.Format);
        Assert.Contains(detail.Block.Errors, e => e.StartsWith("missing-type:", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiplePropertyTokensAndRepeatedProperties_AreCollected()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Thing">
              <span property="name alternateName">Widget</span>
              <span property="name">Second name</span>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        // 'name' present (repeated → list), alternateName present → Thing requires only name.
        Assert.True(detail.Block.IsValid, string.Join("; ", detail.Block.Errors));
    }

    [Fact]
    public void MixedFormats_AreAllExtractedInDocumentOrder()
    {
        var markup = """
            <html><head>
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "WebSite", "name": "Site", "url": "https://example.test" }</script>
            </head><body>
            <div vocab="https://schema.org/" typeof="Person"><span property="name">Ada</span></div>
            <div itemscope itemtype="https://schema.org/Organization"><span itemprop="name">Org</span></div>
            </body></html>
            """;

        var result = _validator.ValidateDetailed(markup, null);

        Assert.Equal(3, result.Blocks.Count);
        Assert.Equal("json-ld", result.Blocks[0].Block.Format);
        Assert.Equal("rdfa", result.Blocks[1].Block.Format);
        Assert.Equal("microdata", result.Blocks[2].Block.Format);
        Assert.Equal(new[] { 0, 1, 2 }, result.Blocks.Select(b => b.Ordinal).ToArray());
    }

    [Fact]
    public void ProductionParser_ProducesTheSameRdfaJudgments()
    {
        var markup = """
            <html><body>
            <div vocab="https://schema.org/" typeof="Product">
              <span property="name">Widget</span>
              <img property="image" src="https://example.test/w.jpg" />
              <div property="offers" typeof="Offer">
                <span property="price" content="9.99">9.99</span>
                <span property="priceCurrency" content="EUR">EUR</span>
              </div>
            </div>
            </body></html>
            """;

        var viaDouble = _validator.ValidateDetailed(markup, "https://example.test/rdfa").Blocks;
        var viaProduction = _realParserValidator.ValidateDetailed(markup, "https://example.test/rdfa").Blocks;

        Assert.Equal(viaDouble.Count, viaProduction.Count);
        for (var i = 0; i < viaDouble.Count; i++)
        {
            Assert.Equal(viaDouble[i].Block.Format, viaProduction[i].Block.Format);
            Assert.Equal(viaDouble[i].Block.Type, viaProduction[i].Block.Type);
            Assert.Equal(viaDouble[i].Block.IsValid, viaProduction[i].Block.IsValid);
            Assert.Equal(viaDouble[i].Block.RichResultEligible, viaProduction[i].Block.RichResultEligible);
            Assert.Equal(viaDouble[i].Block.Errors, viaProduction[i].Block.Errors);
            Assert.Equal(viaDouble[i].NestedEntityTypes, viaProduction[i].NestedEntityTypes);
        }
    }

    [Fact]
    public void RdfaOffersMissingPrice_IsInvalid()
    {
        var markup = """
            <div vocab="https://schema.org/" typeof="Product">
              <span property="name">Widget</span>
              <span property="image">https://example.test/w.jpg</span>
              <div property="offers" typeof="Offer">
                <span property="priceCurrency" content="EUR">EUR</span>
              </div>
            </div>
            """;

        var detail = Assert.Single(_validator.ValidateDetailed(markup, null).Blocks);

        Assert.False(detail.Block.IsValid);
        Assert.Contains(detail.Block.Errors, e => e == "missing-required-field: offers requires 'price'");
    }
}
