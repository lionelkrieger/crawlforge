using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Structured-data validator suite (FS-CRAWL-001 §7.13, criterion #27):
/// JSON-LD + Microdata parsing, syntax/semantic validation, rich-result
/// eligibility, and the structured error taxonomy.
/// </summary>
public class StructuredDataValidatorTests
{
    private readonly StructuredDataValidator _validator = new(new TestDomParser());

    private const string ValidArticle = """
        <html><head>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org",
          "@type": "Article",
          "headline": "Clean-room crawling",
          "image": "https://example.test/cover.jpg",
          "datePublished": "2026-01-15T10:30:00+00:00",
          "author": { "@type": "Person", "name": "Ada" }
        }
        </script>
        </head><body></body></html>
        """;

    [Fact]
    public void ValidArticle_IsValidAndRichResultEligible()
    {
        var result = _validator.Validate(ValidArticle, "https://example.test/page");

        var block = Assert.Single(result.Blocks);
        Assert.Equal("json-ld", block.Format);
        Assert.Equal("Article", block.Type);
        Assert.True(block.IsValid);
        Assert.True(block.RichResultEligible);
        Assert.Empty(block.Errors);
    }

    [Fact]
    public void MissingContext_ProducesTaxonomyError()
    {
        var markup = """<script type="application/ld+json">{ "@type": "Article", "headline": "x", "image": "y", "datePublished": "2026-01-01" }</script>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.Contains(block.Errors, e => e.StartsWith("missing-context:", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingType_ProducesTaxonomyError()
    {
        var markup = """<script type="application/ld+json">{ "@context": "https://schema.org", "name": "x" }</script>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.Null(block.Type);
        Assert.Contains(block.Errors, e => e.StartsWith("missing-type:", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedJson_ProducesMalformedBlock()
    {
        var markup = """<script type="application/ld+json">{ "@context": "https://schema.org", </script>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.Equal("json-ld", block.Format);
        Assert.False(block.IsValid);
        Assert.False(block.RichResultEligible);
        Assert.Contains(block.Errors, e => e.StartsWith("malformed-block:", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRequiredField_NamesTheField()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Article", "image": "https://example.test/i.jpg", "datePublished": "2026-01-01" }
            </script>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.False(block.RichResultEligible);
        Assert.Contains(block.Errors, e => e == "missing-required-field: Article requires 'headline'");
        Assert.Contains(block.Errors, e => e.StartsWith("rich-result-missing-required:", StringComparison.Ordinal) && e.Contains("headline"));
    }

    [Fact]
    public void InvalidDate_ProducesInvalidValue()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Article", "headline": "x", "image": "y", "datePublished": "not-a-date" }
            </script>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.Contains(block.Errors, e => e.StartsWith("invalid-value:", StringComparison.Ordinal) && e.Contains("datePublished"));
    }

    [Fact]
    public void ProductOffers_RequirePriceAndCurrency()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Product", "name": "Widget", "image": "https://example.test/w.jpg",
              "offers": { "@type": "Offer", "priceCurrency": "EUR" } }
            </script>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.Contains(block.Errors, e => e == "missing-required-field: offers requires 'price'");
    }

    [Fact]
    public void ProductOffers_NonNumericPriceIsInvalidValue()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Product", "name": "Widget", "image": "https://example.test/w.jpg",
              "offers": { "@type": "Offer", "price": "free", "priceCurrency": "EUR" } }
            </script>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.Contains(block.Errors, e => e.StartsWith("invalid-value:", StringComparison.Ordinal) && e.Contains("price"));
    }

    [Fact]
    public void ValidProduct_IsRichResultEligible()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Product", "name": "Widget", "image": "https://example.test/w.jpg",
              "offers": { "@type": "Offer", "price": "19.99", "priceCurrency": "EUR" } }
            </script>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.True(block.IsValid);
        Assert.True(block.RichResultEligible);
    }

    [Fact]
    public void GraphExpandsIntoMultipleBlocks_WithInheritedContext()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@graph": [
              { "@type": "Article", "headline": "x", "image": "y", "datePublished": "2026-01-01" },
              { "@type": "Product", "name": "Widget" }
            ] }
            </script>
            """;

        var result = _validator.Validate(markup, null);

        Assert.Equal(2, result.Blocks.Count);
        Assert.True(result.Blocks[0].IsValid);
        Assert.False(result.Blocks[1].IsValid);
        Assert.DoesNotContain(result.Blocks[0].Errors, e => e.StartsWith("missing-context:", StringComparison.Ordinal));
    }

    [Fact]
    public void TopLevelArrayExpandsIntoBlocks()
    {
        var markup = """
            <script type="application/ld+json">
            [ { "@context": "https://schema.org", "@type": "WebSite", "name": "Site", "url": "https://example.test/" },
              { "@context": "https://schema.org", "@type": "Organization", "name": "Org", "url": "https://example.test/", "logo": "https://example.test/l.png" } ]
            </script>
            """;

        var result = _validator.Validate(markup, null);

        Assert.Equal(2, result.Blocks.Count);
        Assert.All(result.Blocks, b => Assert.True(b.IsValid));
    }

    [Fact]
    public void MicrodataProduct_ValidatesWithNestedOffer()
    {
        var markup = """
            <div itemscope itemtype="https://schema.org/Product">
              <span itemprop="name">Widget</span>
              <img itemprop="image" src="https://example.test/w.jpg" />
              <div itemprop="offers" itemscope itemtype="https://schema.org/Offer">
                <meta itemprop="price" content="19.99" />
                <meta itemprop="priceCurrency" content="EUR" />
              </div>
            </div>
            """;

        var result = _validator.Validate(markup, null);

        var block = Assert.Single(result.Blocks);
        Assert.Equal("microdata", block.Format);
        Assert.Equal("https://schema.org/Product", block.Type);
        Assert.True(block.IsValid);
        Assert.True(block.RichResultEligible);
    }

    [Fact]
    public void MicrodataMissingItemtype_ProducesMissingType()
    {
        var markup = """<div itemscope><span itemprop="name">x</span></div>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.Contains(block.Errors, e => e.StartsWith("missing-type:", StringComparison.Ordinal));
    }

    [Fact]
    public void MicrodataNestedItem_IsNotASeparateBlock()
    {
        var markup = """
            <div itemscope itemtype="https://schema.org/Article">
              <span itemprop="headline">x</span>
              <img itemprop="image" src="https://example.test/i.jpg" />
              <meta itemprop="datePublished" content="2026-01-01" />
              <div itemprop="author" itemscope itemtype="https://schema.org/Person"><span itemprop="name">Ada</span></div>
            </div>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.True(block.IsValid);
    }

    [Fact]
    public void MicrodataMissingRequired_NamesTheField()
    {
        var markup = """
            <div itemscope itemtype="https://schema.org/Recipe">
              <span itemprop="name">Soup</span>
            </div>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.Contains(block.Errors, e => e.Contains("'image'"));
        Assert.Contains(block.Errors, e => e.Contains("'recipeIngredient'"));
        Assert.Contains(block.Errors, e => e.Contains("'recipeInstructions'"));
    }

    [Theory]
    [InlineData("FAQPage", """{ "@context": "https://schema.org", "@type": "FAQPage", "mainEntity": [ { "@type": "Question", "name": "Q?", "acceptedAnswer": { "@type": "Answer", "text": "A." } } ] }""")]
    [InlineData("BreadcrumbList", """{ "@context": "https://schema.org", "@type": "BreadcrumbList", "itemListElement": [ { "@type": "ListItem", "position": 1, "name": "Home", "item": "https://example.test/" } ] }""")]
    [InlineData("Event", """{ "@context": "https://schema.org", "@type": "Event", "name": "Launch", "startDate": "2026-02-01T18:00:00+00:00", "location": { "@type": "Place", "name": "Hall" } }""")]
    [InlineData("VideoObject", """{ "@context": "https://schema.org", "@type": "VideoObject", "name": "Demo", "thumbnailUrl": "https://example.test/t.jpg", "uploadDate": "2026-01-01" }""")]
    public void SupportedRichResultFeatures_AreEligibleWhenComplete(string type, string json)
    {
        var markup = $"""<script type="application/ld+json">{json}</script>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.Equal(type, block.Type);
        Assert.True(block.IsValid, string.Join("; ", block.Errors));
        Assert.True(block.RichResultEligible);
    }

    [Fact]
    public void UnknownType_IsSyntaxCheckedButNotEligible()
    {
        var markup = """<script type="application/ld+json">{ "@context": "https://schema.org", "@type": "CustomThing", "name": "x" }</script>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.True(block.IsValid);
        Assert.False(block.RichResultEligible);
    }

    [Fact]
    public void NamespaceQualifiedType_ResolvesAgainstVocabulary()
    {
        var markup = """
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "https://schema.org/Article", "headline": "x", "image": "y", "datePublished": "2026-01-01" }
            </script>
            """;

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.True(block.IsValid);
        Assert.True(block.RichResultEligible);
    }

    [Fact]
    public void EmptyMarkup_ProducesNoBlocks()
    {
        Assert.Empty(_validator.Validate(string.Empty, null).Blocks);
        Assert.Empty(_validator.Validate("<html><body><p>plain</p></body></html>", null).Blocks);
    }

    [Fact]
    public void MultipleBlocks_KeepDocumentOrder()
    {
        var markup = """
            <html><head>
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "WebSite", "name": "First", "url": "https://example.test/" }</script>
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "Organization", "name": "Second", "url": "https://example.test/", "logo": "l.png" }</script>
            </head><body>
            <div itemscope itemtype="https://schema.org/Person"><span itemprop="name">Third</span></div>
            </body></html>
            """;

        var result = _validator.Validate(markup, null);

        Assert.Equal(3, result.Blocks.Count);
        Assert.Equal("WebSite", result.Blocks[0].Type);
        Assert.Equal("Organization", result.Blocks[1].Type);
        Assert.Equal("microdata", result.Blocks[2].Format);
    }

    [Fact]
    public void EmptyJsonLdScript_IsMalformedBlock()
    {
        var markup = """<script type="application/ld+json"></script>""";

        var block = Assert.Single(_validator.Validate(markup, null).Blocks);

        Assert.False(block.IsValid);
        Assert.Contains(block.Errors, e => e.StartsWith("malformed-block:", StringComparison.Ordinal));
    }

    [Fact]
    public void NonJsonLdScripts_AreIgnored()
    {
        var markup = """<script type="text/javascript">var x = { "@type": "Article" };</script>""";

        Assert.Empty(_validator.Validate(markup, null).Blocks);
    }
}
